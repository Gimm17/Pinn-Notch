using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace VNotch.Services;

public enum FileShareStatus
{
    Idle,
    Starting,
    DownloadingEngine,
    ConnectingTunnel,
    Ready,
    Downloading,
    Completed,
    Error,
    Stopped
}

public sealed class FileShareService : IDisposable
{
    private const string CloudflaredDownloadUrl = "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe";
    private static readonly Regex CloudflareTunnelRegex = new(@"https://[a-zA-Z0-9-]+\.trycloudflare\.com", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly object _gate = new();
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Process? _tunnelProcess;

    public FileShareStatus Status { get; private set; } = FileShareStatus.Idle;
    public string? FilePath { get; private set; }
    public string? FileName { get; private set; }
    public long FileSizeBytes { get; private set; }
    public string FormattedFileSize { get; private set; } = "0 B";
    public string? ShareToken { get; private set; }
    public int LocalPort { get; private set; }
    public string? LocalUrl { get; private set; }
    public string? PublicUrl { get; private set; }
    public string? ActiveShareUrl => PublicUrl ?? LocalUrl;
    public double EngineDownloadPercent { get; private set; }
    public long BytesTransferred { get; private set; }
    public string StatusMessage { get; private set; } = "";

    public event Action? StateChanged;
    public event Action<string>? TunnelReady;
    public event Action<long, long>? DownloadProgress;
    public event Action? DownloadCompleted;

    public static string GetToolsDirectory()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppPaths.DirectoryName, "Tools");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string GetCloudflaredPath() => Path.Combine(GetToolsDirectory(), "cloudflared.exe");

    public async Task StartShareAsync(string filePath)
    {
        StopShare();

        if (!File.Exists(filePath))
            throw new FileNotFoundException("File not found.", filePath);

        lock (_gate)
        {
            var info = new FileInfo(filePath);
            FilePath = filePath;
            FileName = info.Name;
            FileSizeBytes = info.Length;
            FormattedFileSize = FormatBytes(FileSizeBytes);
            ShareToken = Guid.NewGuid().ToString("N")[..12];
            BytesTransferred = 0;
            Status = FileShareStatus.Starting;
            StatusMessage = "clipboard.shareQrStarting";
        }

        StateChanged?.Invoke();

        try
        {
            LocalPort = AllocateFreePort();
            _cts = new CancellationTokenSource();

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{LocalPort}/");
            _listener.Start();

            // Setup Local LAN URL as quick fallback/instant share
            string? localIp = GetLocalNetworkIp();
            if (!string.IsNullOrEmpty(localIp))
            {
                LocalUrl = $"http://{localIp}:{LocalPort}/share/{ShareToken}";
            }
            else
            {
                LocalUrl = $"http://127.0.0.1:{LocalPort}/share/{ShareToken}";
            }

            // Start listening loop in background
            _ = Task.Run(() => ListenLoopAsync(_cts.Token));

            // Mark as Ready initially with LocalUrl so QR displays immediately
            lock (_gate)
            {
                Status = FileShareStatus.ConnectingTunnel;
                StatusMessage = "clipboard.shareQrStarting";
            }
            StateChanged?.Invoke();

            // Start Cloudflare Tunnel in background
            _ = Task.Run(() => StartCloudflareTunnelAsync(_cts.Token));
            await Task.Yield();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("FILESHARE", $"Failed to start share: {ex.Message}");
            lock (_gate)
            {
                Status = FileShareStatus.Error;
                StatusMessage = ex.Message;
            }
            StateChanged?.Invoke();
        }
    }

    private async Task StartCloudflareTunnelAsync(CancellationToken cancellation)
    {
        try
        {
            string exePath = GetCloudflaredPath();
            bool exists = File.Exists(exePath) && new FileInfo(exePath).Length > 10_000_000;

            if (!exists)
            {
                lock (_gate)
                {
                    Status = FileShareStatus.DownloadingEngine;
                    StatusMessage = "Downloading Cloudflare engine...";
                }
                StateChanged?.Invoke();

                await DownloadCloudflaredAsync(exePath, cancellation);
            }

            if (cancellation.IsCancellationRequested) return;

            lock (_gate)
            {
                Status = FileShareStatus.ConnectingTunnel;
                StatusMessage = "Connecting public tunnel...";
            }
            StateChanged?.Invoke();

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"tunnel --url http://127.0.0.1:{LocalPort}",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            DataReceivedEventHandler handler = (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;

                var match = CloudflareTunnelRegex.Match(e.Data);
                if (match.Success)
                {
                    string tunnelBase = match.Value;
                    string publicShareUrl = $"{tunnelBase}/share/{ShareToken}";

                    lock (_gate)
                    {
                        PublicUrl = publicShareUrl;
                        Status = FileShareStatus.Ready;
                        StatusMessage = "clipboard.shareQrReady";
                    }

                    TunnelReady?.Invoke(publicShareUrl);
                    StateChanged?.Invoke();
                }
            };

            process.OutputDataReceived += handler;
            process.ErrorDataReceived += handler;

            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to launch cloudflared process.");
            }

            _tunnelProcess = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var reg = cancellation.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            });

            await process.WaitForExitAsync(cancellation);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            RuntimeLog.Error("FILESHARE", $"Tunnel error: {ex.Message}");
            lock (_gate)
            {
                // If tunnel fails, we keep local share working
                if (Status != FileShareStatus.Ready)
                {
                    Status = FileShareStatus.Ready;
                    StatusMessage = "clipboard.shareQrReady";
                    StateChanged?.Invoke();
                }
            }
        }
    }

    private async Task DownloadCloudflaredAsync(string targetPath, CancellationToken cancellation)
    {
        string tmpPath = targetPath + ".tmp";
        using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Pinn-Notch/2.2.0");

        using var response = await client.GetAsync(CloudflaredDownloadUrl, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cancellation);
        response.EnsureSuccessStatusCode();

        long? total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellation);
        await using var dest = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long totalRead = 0;
        int read;

        while ((read = await source.ReadAsync(buffer, cancellation)) > 0)
        {
            await dest.WriteAsync(buffer.AsMemory(0, read), cancellation);
            totalRead += read;

            if (total.HasValue && total.Value > 0)
            {
                EngineDownloadPercent = Math.Clamp((double)totalRead / total.Value * 100.0, 0, 100);
                StateChanged?.Invoke();
            }
        }

        dest.Close();
        if (File.Exists(targetPath)) File.Delete(targetPath);
        File.Move(tmpPath, targetPath);
    }

    private async Task ListenLoopAsync(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested && _listener?.IsListening == true)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequestAsync(context, cancellation));
            }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                RuntimeLog.Error("FILESHARE", $"Listen loop error: {ex.Message}");
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken cancellation)
    {
        try
        {
            string path = context.Request.Url?.AbsolutePath ?? "/";
            var parts = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

            // Handle Direct Download: /d/{token} or /d/{token}/filename
            if (parts.Length >= 2 && parts[0].Equals("d", StringComparison.OrdinalIgnoreCase) && parts[1] == ShareToken)
            {
                await ServeFileDownloadAsync(context, cancellation);
                return;
            }

            // Handle Landing Webpage: / or /share or /share/{token}
            if (parts.Length == 0 || (parts.Length >= 1 && parts[0].Equals("share", StringComparison.OrdinalIgnoreCase)))
            {
                await ServeLandingPageAsync(context);
                return;
            }

            // 404 for any other route
            context.Response.StatusCode = 404;
            byte[] notFound = Encoding.UTF8.GetBytes("Not Found");
            context.Response.ContentType = "text/plain";
            context.Response.ContentLength64 = notFound.Length;
            await context.Response.OutputStream.WriteAsync(notFound, cancellation);
            context.Response.Close();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("FILESHARE", $"Request handle error: {ex.Message}");
            try
            {
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
            catch { }
        }
    }

    private async Task ServeFileDownloadAsync(HttpListenerContext context, CancellationToken cancellation)
    {
        string? path = FilePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            context.Response.StatusCode = 404;
            context.Response.Close();
            return;
        }

        lock (_gate)
        {
            Status = FileShareStatus.Downloading;
            StatusMessage = "clipboard.shareQrDownloading";
        }
        StateChanged?.Invoke();

        try
        {
            var info = new FileInfo(path);
            string safeName = FileName ?? "download";
            string mime = GetMimeType(path);

            context.Response.StatusCode = 200;
            context.Response.ContentType = mime;
            context.Response.ContentLength64 = info.Length;
            context.Response.AddHeader("Content-Disposition", $"attachment; filename=\"{safeName}\"; filename*=UTF-8''{Uri.EscapeDataString(safeName)}");
            context.Response.AddHeader("Cache-Control", "no-cache, no-store, must-revalidate");
            context.Response.AddHeader("Access-Control-Allow-Origin", "*");

            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
            var buffer = new byte[65536];
            long sent = 0;
            int read;

            while ((read = await fs.ReadAsync(buffer, cancellation)) > 0)
            {
                await context.Response.OutputStream.WriteAsync(buffer.AsMemory(0, read), cancellation);
                sent += read;
                BytesTransferred = sent;
                DownloadProgress?.Invoke(sent, info.Length);
            }

            context.Response.OutputStream.Flush();
            context.Response.Close();

            lock (_gate)
            {
                Status = FileShareStatus.Completed;
                StatusMessage = "clipboard.shareQrCompleted";
            }
            DownloadCompleted?.Invoke();
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("FILESHARE", $"File stream error: {ex.Message}");
            try { context.Response.Abort(); } catch { }
        }
    }

    private async Task ServeLandingPageAsync(HttpListenerContext context)
    {
        string safeName = WebUtility.HtmlEncode(FileName ?? "Shared File");
        string sizeStr = FormattedFileSize;
        string directDownloadUrl = $"/d/{ShareToken}/{Uri.EscapeDataString(FileName ?? "file")}";

        string html = $@"<!DOCTYPE html>
<html lang=""en"">
<head>
  <meta charset=""UTF-8"">
  <meta name=""viewport"" content=""width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no"">
  <title>Pinn-Notch &bull; {safeName}</title>
  <style>
    * {{ box-sizing: border-box; margin: 0; padding: 0; }}
    body {{
      font-family: -apple-system, BlinkMacSystemFont, ""SF Pro Display"", ""Segoe UI"", Roboto, Helvetica, Arial, sans-serif;
      background: #09090b;
      color: #f4f4f5;
      min-height: 100vh;
      display: flex;
      flex-direction: column;
      align-items: center;
      justify-content: center;
      padding: 24px;
    }}
    .card {{
      background: rgba(24, 24, 27, 0.85);
      backdrop-filter: blur(20px);
      border: 1px solid rgba(255, 255, 255, 0.12);
      border-radius: 24px;
      padding: 32px 28px;
      width: 100%;
      max-width: 400px;
      text-align: center;
      box-shadow: 0 20px 40px rgba(0,0,0,0.6);
    }}
    .icon-badge {{
      width: 72px;
      height: 72px;
      background: linear-gradient(135deg, #3b82f6, #6366f1);
      border-radius: 18px;
      display: inline-flex;
      align-items: center;
      justify-content: center;
      margin-bottom: 20px;
      box-shadow: 0 8px 24px rgba(99, 102, 241, 0.35);
    }}
    .icon-badge svg {{
      width: 36px;
      height: 36px;
      fill: #ffffff;
    }}
    .file-name {{
      font-size: 19px;
      font-weight: 700;
      word-break: break-word;
      line-height: 1.35;
      margin-bottom: 8px;
      color: #ffffff;
    }}
    .file-size {{
      font-size: 14px;
      color: #a1a1aa;
      margin-bottom: 28px;
    }}
    .download-btn {{
      display: inline-flex;
      align-items: center;
      justify-content: center;
      gap: 10px;
      width: 100%;
      padding: 16px 20px;
      background: #ffffff;
      color: #09090b;
      font-size: 16px;
      font-weight: 600;
      text-decoration: none;
      border-radius: 14px;
      transition: transform 0.15s ease, opacity 0.15s ease;
      box-shadow: 0 4px 14px rgba(255,255,255,0.2);
    }}
    .download-btn:active {{
      transform: scale(0.98);
      opacity: 0.9;
    }}
    .hint {{
      font-size: 12px;
      color: #71717a;
      margin-top: 18px;
      line-height: 1.5;
    }}
    .brand {{
      margin-top: 32px;
      font-size: 12px;
      color: #52525b;
      display: flex;
      align-items: center;
      gap: 6px;
    }}
  </style>
</head>
<body>
  <div class=""card"">
    <div class=""icon-badge"">
      <svg viewBox=""0 0 24 24""><path d=""M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8l-6-6zm2 16H8v-2h8v2zm0-4H8v-2h8v2zm-3-5V3.5L18.5 9H13z""/></svg>
    </div>
    <div class=""file-name"">{safeName}</div>
    <div class=""file-size"">{sizeStr}</div>
    <a href=""{directDownloadUrl}"" class=""download-btn"" id=""dlBtn"">
      <svg style=""width:20px;height:20px;fill:currentColor;"" viewBox=""0 0 24 24""><path d=""M19 9h-4V3H9v6H5l7 7 7-7zM5 18v2h14v-2H5z""/></svg>
      Download File
    </a>
    <div class=""hint"" id=""hintText"">Download will begin automatically in a moment...</div>
  </div>
  <div class=""brand"">
    <span>Shared via Pinn-Notch</span>
  </div>
  <script>
    setTimeout(function() {{
      window.location.href = ""{directDownloadUrl}"";
      document.getElementById(""hintText"").textContent = ""If download did not start automatically, click the button above."";
    }}, 700);
  </script>
</body>
</html>";

        byte[] bytes = Encoding.UTF8.GetBytes(html);
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    public void StopShare()
    {
        try { _cts?.Cancel(); } catch { }
        try { _cts?.Dispose(); } catch { }
        _cts = null;

        try
        {
            if (_tunnelProcess != null && !_tunnelProcess.HasExited)
            {
                _tunnelProcess.Kill(entireProcessTree: true);
                _tunnelProcess.Dispose();
            }
        }
        catch { }
        _tunnelProcess = null;

        try
        {
            if (_listener != null && _listener.IsListening)
            {
                _listener.Stop();
                _listener.Close();
            }
        }
        catch { }
        _listener = null;

        lock (_gate)
        {
            Status = FileShareStatus.Stopped;
            StatusMessage = "";
            PublicUrl = null;
            LocalUrl = null;
            ShareToken = null;
        }

        StateChanged?.Invoke();
    }

    public void Dispose() => StopShare();

    private static int AllocateFreePort()
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        return port;
    }

    private static string? GetLocalNetworkIp()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                var props = ni.GetIPProperties();
                if (props.GatewayAddresses.Count == 0) continue;

                foreach (var addr in props.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        return addr.Address.ToString();
                }
            }
        }
        catch { }
        return null;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }

    private static string GetMimeType(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".pdf" => "application/pdf",
            ".zip" => "application/zip",
            ".rar" => "application/x-rar-compressed",
            ".7z" => "application/x-7z-compressed",
            ".tar" or ".gz" => "application/gzip",
            ".mp4" => "video/mp4",
            ".mkv" => "video/x-matroska",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".txt" => "text/plain; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".csv" => "text/csv; charset=utf-8",
            ".doc" or ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xls" or ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".ppt" or ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".apk" => "application/vnd.android.package-archive",
            _ => "application/octet-stream"
        };
    }
}
