using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using VNotch.Models;

namespace VNotch.Services;

public sealed class YouTubeMusicPlayerService
{
    private static readonly Lazy<YouTubeMusicPlayerService> _lazy = new(() => new YouTubeMusicPlayerService());
    public static YouTubeMusicPlayerService Instance => _lazy.Value;

    private static readonly HttpClient _httpClient = new();

    private YouTubeMusicWindow? _window;
    public bool IsConfiguredAsDefault { get; set; } = true;

    public bool IsWindowLoaded => _window != null && _window.IsLoaded;
    public bool IsWindowVisible => _window != null && _window.IsVisible;
    public uint? BrowserProcessId => _window?.BrowserProcessId;

    public bool HasActiveMedia => !string.IsNullOrEmpty(CurrentTrack) && (IsPlaying || Duration > TimeSpan.Zero);
    public bool IsPlaying { get; private set; }
    public string CurrentTrack { get; private set; } = "";
    public string CurrentArtist { get; private set; } = "";
    public string ArtworkUrl { get; private set; } = "";
    public string VideoId { get; private set; } = "";
    public BitmapSource? Thumbnail { get; private set; }
    public TimeSpan Position { get; private set; } = TimeSpan.Zero;
    public TimeSpan Duration { get; private set; } = TimeSpan.Zero;
    public DateTimeOffset LastUpdated { get; private set; } = DateTimeOffset.Now;

    public string LastPlayedVideoId { get; private set; } = "";
    private static readonly string LastTrackIdPath = Path.Combine(AppPaths.LocalAppDataRoot, "last_played_track_id.txt");

    public double Volume { get; private set; } = 1.0;
    public bool IsMuted { get; private set; } = false;

    public event Action? MediaStateChanged;
    public event Action<double, bool>? VolumeChanged;

    private string _lastLoadedArtworkUrl = "";
    private DateTimeOffset _lastBroadcastTime = DateTimeOffset.MinValue;

    private YouTubeMusicPlayerService()
    {
        try
        {
            if (File.Exists(LastTrackIdPath))
            {
                string saved = File.ReadAllText(LastTrackIdPath).Trim();
                if (saved.Length == 11) LastPlayedVideoId = saved;
            }
        }
        catch { }
    }

    public void OpenWindow(bool activate = true)
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() => OpenWindow(activate)));
            return;
        }

        if (_window == null)
        {
            _window = new YouTubeMusicWindow();
        }

        _window.EnsureVisibleOnScreen();

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Show();
        if (activate)
        {
            _window.Activate();
        }
    }

    public void EnsureBackgroundWindow()
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.Invoke(EnsureBackgroundWindow);
            return;
        }

        if (_window == null)
        {
            _window = new YouTubeMusicWindow();
            _window.InitializeBackground();
        }
    }

    public void UpdateState(string title, string artist, string artworkUrl, bool isPlaying, double currentTime, double duration, string videoId = "")
    {
        bool trackChanged = CurrentTrack != title || CurrentArtist != artist;
        bool playStateChanged = IsPlaying != isPlaying;
        bool durationChanged = Math.Abs(Duration.TotalSeconds - duration) > 0.5;
        bool timeJumped = Math.Abs(Position.TotalSeconds - currentTime) > 1.5;

        CurrentTrack = title;
        CurrentArtist = artist;
        if (!string.IsNullOrEmpty(videoId) && videoId.Length == 11)
        {
            VideoId = videoId;
            if (LastPlayedVideoId != videoId)
            {
                LastPlayedVideoId = videoId;
                try { File.WriteAllText(LastTrackIdPath, videoId); } catch { }
            }
        }
        else if (trackChanged)
        {
            VideoId = "";
        }
        IsPlaying = isPlaying;
        Position = TimeSpan.FromSeconds(Math.Max(0, currentTime));
        Duration = TimeSpan.FromSeconds(Math.Max(0, duration));
        LastUpdated = DateTimeOffset.Now;

        if (trackChanged || ArtworkUrl != artworkUrl)
        {
            if (ArtworkUrl != artworkUrl)
            {
                Thumbnail = null;
                ArtworkUrl = artworkUrl;
                UpdateThumbnail(artworkUrl);
            }
            else if (trackChanged && string.IsNullOrWhiteSpace(artworkUrl))
            {
                Thumbnail = null;
            }
        }

        if (trackChanged || playStateChanged)
        {
            RuntimeLog.Info("YTMUSIC", $"State: '{title}' by '{artist}' ({currentTime:F1}/{duration:F1}s, playing={isPlaying})");
        }

        if (trackChanged || playStateChanged || durationChanged || timeJumped || (DateTimeOffset.Now - _lastBroadcastTime).TotalSeconds >= 1.0)
        {
            _lastBroadcastTime = DateTimeOffset.Now;
            MediaStateChanged?.Invoke();
        }
    }

    private async void UpdateThumbnail(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            Thumbnail = null;
            _lastLoadedArtworkUrl = "";
            MediaStateChanged?.Invoke();
            return;
        }

        if (url == _lastLoadedArtworkUrl) return;
        _lastLoadedArtworkUrl = url;

        try
        {
            byte[] bytes = await _httpClient.GetByteArrayAsync(url).ConfigureAwait(false);
            if (bytes == null || bytes.Length == 0) return;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    using var stream = new MemoryStream(bytes);
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.StreamSource = stream;
                    bi.EndInit();
                    if (bi.CanFreeze) bi.Freeze();

                    BitmapSource finalThumb = bi;
                    if (bi.PixelWidth > bi.PixelHeight && bi.PixelHeight > 0)
                    {
                        int size = bi.PixelHeight;
                        int offsetX = (bi.PixelWidth - size) / 2;
                        var cropped = new CroppedBitmap(bi, new Int32Rect(offsetX, 0, size, size));
                        if (cropped.CanFreeze) cropped.Freeze();
                        finalThumb = cropped;
                    }

                    Thumbnail = finalThumb;
                    MediaStateChanged?.Invoke();
                }
                catch (Exception ex)
                {
                    RuntimeLog.Warn("YTMUSIC-ARTWORK", $"Decode error: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("YTMUSIC-ARTWORK", $"Download error: {ex.Message}");
        }
    }

    public void PopulateMediaInfo(MediaInfo info)
    {
        info.IsAnyMediaPlaying = true;
        info.IsPlaying = IsPlaying;
        info.CurrentTrack = CurrentTrack;
        info.CurrentArtist = CurrentArtist;
        info.MediaSource = "YouTube Music";
        info.SourceAppId = "YouTubeMusic";
        info.SessionInstanceKey = "YouTubeMusicInternalSession";
        info.Position = Position;
        info.Duration = Duration;
        info.Thumbnail = Thumbnail;
        info.YouTubeVideoId = VideoId;
        info.IsSeekEnabled = Duration > TimeSpan.Zero;
        info.IsYouTubeRunning = true;
        info.LastUpdated = LastUpdated;
        info.PlaybackRate = 1.0;
    }

    public async Task<bool> PlayOrOpenAsync(bool showWindow = false)
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            return await Application.Current.Dispatcher.InvokeAsync(() => PlayOrOpenAsync(showWindow)).Task.Unwrap();
        }

        if (_window == null)
        {
            if (showWindow)
            {
                OpenWindow(activate: true);
            }
            else
            {
                EnsureBackgroundWindow();
            }
            _window?.RequestAutoPlay();
            return true;
        }

        if (showWindow && !_window.IsVisible)
        {
            OpenWindow(activate: true);
        }

        _window.RequestAutoPlay();
        return true;
    }

    public async Task<bool> TogglePlayPauseAsync()
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            return await Application.Current.Dispatcher.InvokeAsync(TogglePlayPauseAsync).Task.Unwrap();
        }

        if (_window == null)
        {
            return await PlayOrOpenAsync(showWindow: false);
        }

        return await _window.TogglePlayPauseAsync();
    }

    public async Task<bool> PauseAsync()
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            return await Application.Current.Dispatcher.InvokeAsync(PauseAsync).Task.Unwrap();
        }

        if (_window == null) return false;
        return await _window.PauseAsync();
    }

    public async Task<bool> NextTrackAsync()
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            return await Application.Current.Dispatcher.InvokeAsync(NextTrackAsync).Task.Unwrap();
        }

        if (_window == null) return false;
        return await _window.NextTrackAsync();
    }

    public async Task<bool> PreviousTrackAsync()
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            return await Application.Current.Dispatcher.InvokeAsync(PreviousTrackAsync).Task.Unwrap();
        }

        if (_window == null) return false;
        return await _window.PreviousTrackAsync();
    }

    public async Task<bool> SeekAsync(TimeSpan position)
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            return await Application.Current.Dispatcher.InvokeAsync(() => SeekAsync(position)).Task.Unwrap();
        }

        if (_window == null) return false;
        return await _window.SeekAsync(position);
    }

    public void UpdateVolume(double volume, bool isMuted)
    {
        double clamped = Math.Clamp(volume, 0.0, 1.0);
        bool changed = Math.Abs(Volume - clamped) > 0.01 || IsMuted != isMuted;
        Volume = clamped;
        IsMuted = isMuted;

        if (changed)
        {
            VolumeChanged?.Invoke(Volume, IsMuted);
        }
    }

    public async Task<bool> SetVolumeAsync(double volume)
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            return await Application.Current.Dispatcher.InvokeAsync(() => SetVolumeAsync(volume)).Task.Unwrap();
        }

        double clamped = Math.Clamp(volume, 0.0, 1.0);
        Volume = clamped;
        if (clamped > 0 && IsMuted) IsMuted = false;
        VolumeChanged?.Invoke(Volume, IsMuted);

        if (_window == null) return false;
        return await _window.SetVolumeAsync(clamped);
    }

    public async Task<bool> ToggleMuteAsync()
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            return await Application.Current.Dispatcher.InvokeAsync(ToggleMuteAsync).Task.Unwrap();
        }

        IsMuted = !IsMuted;
        VolumeChanged?.Invoke(Volume, IsMuted);

        if (_window == null) return false;
        return await _window.ToggleMuteAsync();
    }

    public void Shutdown()
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(Shutdown));
            return;
        }

        CurrentTrack = "";
        CurrentArtist = "";
        ArtworkUrl = "";
        VideoId = "";
        _lastLoadedArtworkUrl = "";
        Thumbnail = null;
        IsPlaying = false;
        Position = TimeSpan.Zero;
        Duration = TimeSpan.Zero;
        LastUpdated = DateTimeOffset.Now;

        if (_window != null)
        {
            var win = _window;
            _window = null;
            win.ForceClose();
        }

        MediaStateChanged?.Invoke();
        AppMemoryOptimizer.OptimizeMemory(aggressive: true);
    }
}
