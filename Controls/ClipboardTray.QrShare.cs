using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Models;
using VNotch.Services;
using VNotch.ViewModels;

namespace VNotch.Controls;

public partial class ClipboardTray
{
    private FileShareService? _fileShareService;
    private FileShareService ShareService => _fileShareService ??= App.Services.GetRequiredService<FileShareService>();
    private bool _isShareBound;

    private async Task StartQrShareAsync(ClipboardCardViewModel card)
    {
        if (_controller == null) return;

        CloseDetails();
        foreach (var menu in _openMenus.ToArray()) menu.IsOpen = false;

        var entry = card.Entry;
        string[] paths = [];
        try
        {
            paths = await _controller.Store.ExportFilesAsync(entry.Id);
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("FILESHARE", $"Export error: {ex.Message}");
        }

        if (paths.Length == 0 || (!File.Exists(paths[0]) && !Directory.Exists(paths[0])))
        {
            StatusChanged(Loc.Get("clipboard.error"));
            return;
        }

        string targetPath = paths[0];

        if (!_isShareBound)
        {
            _isShareBound = true;
            ShareService.StateChanged += OnShareStateChanged;
            ShareService.DownloadProgress += OnShareDownloadProgress;
            ShareService.DownloadCompleted += OnShareDownloadCompleted;
        }

        QrShareFileName.Text = Path.GetFileName(targetPath);
        QrShareFileSize.Text = File.Exists(targetPath)
            ? FormatShareSize(new FileInfo(targetPath).Length)
            : "";
        QrLoadingOverlay.Visibility = Visibility.Visible;
        QrDownloadProgress.Visibility = Visibility.Visible;
        QrDownloadProgress.IsIndeterminate = true;
        QrLoadingText.Text = Loc.Get("clipboard.shareQrStarting");
        QrStatusHint.Text = Loc.Get("clipboard.shareQrStarting");
        QrShareUrlText.Text = "Generating link...";
        CopyQrLinkButton.Content = Loc.Get("clipboard.shareQrCopy");
        StopQrShareButton.Content = Loc.Get("clipboard.shareQrStop");
        QrCodeImage.Source = null;
        QrSharePanel.Visibility = Visibility.Visible;

        try
        {
            await ShareService.StartShareAsync(targetPath);
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("FILESHARE", $"Start error: {ex.Message}");
            QrLoadingText.Text = ex.Message;
            QrDownloadProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void OnShareStateChanged()
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (QrSharePanel.Visibility != Visibility.Visible) return;

            string? activeUrl = ShareService.ActiveShareUrl;
            if (!string.IsNullOrEmpty(activeUrl))
            {
                QrShareUrlText.Text = activeUrl;
                try
                {
                    QrCodeImage.Source = QrCodeHelper.GenerateQrCode(activeUrl, 10);
                    QrLoadingOverlay.Visibility = Visibility.Collapsed;
                }
                catch { }
            }

            switch (ShareService.Status)
            {
                case FileShareStatus.DownloadingEngine:
                    QrLoadingOverlay.Visibility = Visibility.Visible;
                    QrLoadingText.Text = $"Downloading Cloudflare engine ({ShareService.EngineDownloadPercent:F0}%)...";
                    QrDownloadProgress.IsIndeterminate = false;
                    QrDownloadProgress.Value = ShareService.EngineDownloadPercent;
                    QrDownloadProgress.Visibility = Visibility.Visible;
                    break;

                case FileShareStatus.ConnectingTunnel:
                    QrLoadingOverlay.Visibility = Visibility.Visible;
                    QrLoadingText.Text = Loc.Get("clipboard.shareQrStarting");
                    QrDownloadProgress.IsIndeterminate = true;
                    QrDownloadProgress.Visibility = Visibility.Visible;
                    break;

                case FileShareStatus.Ready:
                    QrLoadingOverlay.Visibility = Visibility.Collapsed;
                    QrStatusHint.Text = Loc.Get("clipboard.shareQrReady");
                    break;

                case FileShareStatus.Downloading:
                    QrStatusHint.Text = Loc.Get("clipboard.shareQrDownloading");
                    break;

                case FileShareStatus.Completed:
                    QrStatusHint.Text = Loc.Get("clipboard.shareQrCompleted");
                    break;

                case FileShareStatus.Error:
                    QrLoadingOverlay.Visibility = Visibility.Visible;
                    QrLoadingText.Text = ShareService.StatusMessage;
                    QrDownloadProgress.Visibility = Visibility.Collapsed;
                    break;
            }
        });
    }

    private void OnShareDownloadProgress(long sent, long total)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (QrSharePanel.Visibility != Visibility.Visible) return;
            double percent = total > 0 ? (double)sent / total * 100.0 : 0;
            QrStatusHint.Text = $"{Loc.Get("clipboard.shareQrDownloading")} ({percent:F0}%)";
        });
    }

    private void OnShareDownloadCompleted()
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (QrSharePanel.Visibility != Visibility.Visible) return;
            QrStatusHint.Text = Loc.Get("clipboard.shareQrCompleted");
        });
    }

    private void CloseQrShare_Click(object sender, RoutedEventArgs e)
    {
        QrSharePanel.Visibility = Visibility.Collapsed;
        ShareService.StopShare();
    }

    private void StopQrShare_Click(object sender, RoutedEventArgs e)
    {
        QrSharePanel.Visibility = Visibility.Collapsed;
        ShareService.StopShare();
    }

    private void CopyQrLink_Click(object sender, RoutedEventArgs e)
    {
        string? url = ShareService.ActiveShareUrl;
        if (!string.IsNullOrEmpty(url))
        {
            try
            {
                System.Windows.Clipboard.SetText(url);
                CopyQrLinkButton.Content = Loc.Get("clipboard.shareQrCopied");
                _ = Task.Delay(2000).ContinueWith(_ =>
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        CopyQrLinkButton.Content = Loc.Get("clipboard.shareQrCopy");
                    });
                });
            }
            catch { }
        }
    }

    private static string FormatShareSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }
}
