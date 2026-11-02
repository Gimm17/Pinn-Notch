using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using VNotch.Services;

namespace VNotch;

public partial class YouTubeMusicWindow : Window
{
    private const string LogCategory = "YTMUSIC-WINDOW";
    private const string YouTubeMusicUrl = "https://music.youtube.com";

    private readonly string _userDataFolder;
    private bool _isInitialized;
    private bool _isExplicitExit;
    private bool _autoPlayRequested;
    private CancellationTokenSource? _autoPlayCts;

    // Ad-Block, Promo Dismissal & State-Reporting JS injected on load (tuned from th-ch-ytmusic / ytmdesktop)
    private const string TunedInjectedScript = @"
(function() {
    // 1. Fast ad-skip loop
    function skipAds() {
        try {
            const skipSelectors = [
                '.ytp-ad-skip-button',
                '.ytp-ad-skip-button-modern',
                '.ytp-skip-ad-button',
                'button.ytp-ad-skip-button',
                '.ytp-ad-overlay-close-button'
            ];
            for (const sel of skipSelectors) {
                const btn = document.querySelector(sel);
                if (btn) {
                    btn.click();
                    break;
                }
            }

            const adShowing = document.querySelector('.ad-showing, .ad-interrupting');
            const video = document.querySelector('video');
            if (adShowing && video && isFinite(video.duration) && video.duration > 0) {
                video.currentTime = video.duration;
            }

            const dismissSelectors = [
                'ytmusic-mealbar-promo-renderer #dismiss-button',
                'ytmusic-popup-container #dismiss-button',
                'yt-confirm-dialog-renderer #cancel-button',
                '.ytmusic-mealbar-promo-renderer button[aria-label=""Dismiss""]'
            ];
            for (const sel of dismissSelectors) {
                const btn = document.querySelector(sel);
                if (btn && btn.offsetParent !== null) {
                    btn.click();
                }
            }
        } catch (e) {}
    }

    setInterval(skipAds, 400);

    // 2. Safe style injection (does not throw even before document.head is created)
    function injectStyle() {
        try {
            if (!document.getElementById('pinn-notch-ytmusic-tuning')) {
                const target = document.head || document.documentElement || document.body;
                if (!target) return;
                const style = document.createElement('style');
                style.id = 'pinn-notch-ytmusic-tuning';
                style.textContent = `
                    ytmusic-mealbar-promo-renderer,
                    ytmusic-statement-banner-renderer,
                    .ytp-ad-overlay-container,
                    .ytp-ad-message-container,
                    #offer-module,
                    ytmusic-popup-container[elevation] {
                        display: none !important;
                    }
                    ::-webkit-scrollbar {
                        width: 8px !important;
                        height: 8px !important;
                    }
                    ::-webkit-scrollbar-thumb {
                        background: #333333 !important;
                        border-radius: 4px !important;
                    }
                    ::-webkit-scrollbar-thumb:hover {
                        background: #555555 !important;
                    }

                    /* Background Mode: Throttling video decoding and heavy visuals to save GPU & RAM */
                    html.pinn-bg-mode video {
                        visibility: hidden !important;
                        opacity: 0 !important;
                        pointer-events: none !important;
                    }
                    html.pinn-bg-mode ytmusic-player {
                        visibility: hidden !important;
                    }
                    html.pinn-bg-mode #song-video {
                        visibility: hidden !important;
                    }
                    html.pinn-bg-mode #player-page {
                        contain: strict !important;
                    }
                    html.pinn-bg-mode canvas {
                        display: none !important;
                    }
                `;
                target.appendChild(style);
            }
        } catch (e) {}
    }
    injectStyle();
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', injectStyle);
    }

    // 3. Helper functions exposed on window for programmatic control
    window.__pinnPlay = function() {
        const playerBar = document.querySelector('ytmusic-player-bar');
        const playBtn = document.querySelector('#play-pause-button');
        const hasBarTrack = playerBar && playerBar.querySelector('.title')?.textContent?.trim();
        if (playBtn && hasBarTrack) {
            const label = (playBtn.getAttribute('aria-label') || playBtn.getAttribute('title') || '').toLowerCase();
            if (label.includes('play') || label.includes('putar')) {
                clickElement(playBtn);
                return 'true';
            }
        }
        const v = document.querySelector('video');
        if (v && v.src && v.paused && isFinite(v.duration) && v.duration > 0) {
            v.play();
            return 'true';
        }
        return 'false';
    };

    function clickElement(el) {
        if (!el) return false;
        try {
            if (typeof el.click === 'function') {
                el.click();
            } else {
                const opts = { bubbles: true, cancelable: true, composed: true, view: window };
                el.dispatchEvent(new MouseEvent('click', opts));
            }
            return true;
        } catch (e) {
            return false;
        }
    }

    let lastPlayTriggerTime = 0;

    window.__pinnPlayOrStart = function() {
        try {
            const now = Date.now();
            if (now - lastPlayTriggerTime < 4000) {
                return 'play_in_progress';
            }

            const v = document.querySelector('video');
            if (v && !v.paused && !v.ended) {
                return 'already_playing';
            }

            // Check if player is currently loading, buffering or starting a radio mix
            const spinner = document.querySelector('paper-spinner-lite[active], .loading-spinner, ytmusic-player .ytp-spinner');
            if (spinner && spinner.offsetParent !== null) {
                return 'buffering';
            }

            const toast = document.querySelector('ytmusic-toast-renderer, paper-toast');
            if (toast && /radio|queue|memuat|starting/i.test(toast.textContent || '')) {
                return 'starting_radio';
            }

            // 1. Resume existing paused video if valid and past beginning
            if (v && v.src && v.paused && isFinite(v.duration) && v.duration > 0 && v.currentTime > 0) {
                lastPlayTriggerTime = now;
                v.play();
                return 'resumed_video';
            }

            // 2. Click play button in player bar if a track is queued and paused
            const playBtn = document.querySelector('#play-pause-button');
            const playerBar = document.querySelector('ytmusic-player-bar');
            const hasBarTrack = playerBar && playerBar.querySelector('.title')?.textContent?.trim();
            if (playBtn && hasBarTrack) {
                const label = (playBtn.getAttribute('aria-label') || playBtn.getAttribute('title') || '').toLowerCase();
                if (label.includes('play') || label.includes('putar')) {
                    lastPlayTriggerTime = now;
                    clickElement(playBtn);
                    return 'clicked_player_bar_play';
                }
            }

            // 3. Look for 'Play all' button in 'Quick picks' section
            const allShelves = document.querySelectorAll('ytmusic-shelf-renderer');
            for (const shelf of allShelves) {
                const headerText = (shelf.querySelector('h2, .title')?.textContent || '').toLowerCase();
                if (headerText.includes('quick') || headerText.includes('picks') || headerText.includes('cepat') || headerText.includes('pilihan')) {
                    const playAll = shelf.querySelector('button[aria-label*=""Play all"" i], button[aria-label*=""Putar semua"" i], #play-button button, yt-button-renderer button');
                    if (playAll && clickElement(playAll)) {
                        lastPlayTriggerTime = now;
                        return 'clicked_quick_picks_play_all';
                    }

                    // Or first item in this shelf
                    const firstRow = shelf.querySelector('ytmusic-responsive-list-item-renderer, ytmusic-two-row-item-renderer');
                    if (firstRow) {
                        const rowPlayBtn = firstRow.querySelector('ytmusic-play-button-renderer #button, .play-button, ytmusic-item-thumbnail-overlay-renderer');
                        if (rowPlayBtn && clickElement(rowPlayBtn)) {
                            lastPlayTriggerTime = now;
                            return 'clicked_quick_picks_first_item_btn';
                        }
                        const rowLink = firstRow.querySelector('.title a, yt-formatted-string.title a, a.yt-simple-endpoint');
                        if (rowLink && clickElement(rowLink)) {
                            lastPlayTriggerTime = now;
                            return 'clicked_quick_picks_first_item_link';
                        }
                    }
                }
            }

            // 4. Try any first song row on Home
            const firstItem = document.querySelector('ytmusic-responsive-list-item-renderer, ytmusic-two-row-item-renderer');
            if (firstItem) {
                const btn = firstItem.querySelector('ytmusic-play-button-renderer #button, .play-button, ytmusic-item-thumbnail-overlay-renderer');
                if (btn && clickElement(btn)) {
                    lastPlayTriggerTime = now;
                    return 'clicked_first_item_btn';
                }
                const link = firstItem.querySelector('.title a, yt-formatted-string.title a, a.yt-simple-endpoint');
                if (link && clickElement(link)) {
                    lastPlayTriggerTime = now;
                    return 'clicked_first_item_link';
                }
            }

            // 5. Try any general play button
            const anyPlay = document.querySelector('ytmusic-play-button-renderer #button, button[aria-label*=""Play all"" i], button[aria-label*=""Putar semua"" i], .play-button');
            if (anyPlay && clickElement(anyPlay)) {
                lastPlayTriggerTime = now;
                return 'clicked_any_play';
            }
        } catch (e) {
            return 'error:' + e.message;
        }
        return 'no_target_found';
    };

    window.__pinnPause = function() {
        const playerBar = document.querySelector('ytmusic-player-bar');
        const playBtn = document.querySelector('#play-pause-button');
        const hasBarTrack = playerBar && playerBar.querySelector('.title')?.textContent?.trim();

        if (playBtn && hasBarTrack) {
            const label = (playBtn.getAttribute('aria-label') || playBtn.getAttribute('title') || '').toLowerCase();
            if (label.includes('pause') || label.includes('jeda')) {
                clickElement(playBtn);
                return 'true';
            }
        }
        const v = document.querySelector('video');
        if (v && !v.paused) {
            v.pause();
            return 'true';
        }
        return 'false';
    };

    window.__pinnToggle = function() {
        const playerBar = document.querySelector('ytmusic-player-bar');
        const playBtn = document.querySelector('#play-pause-button');
        const hasBarTrack = playerBar && playerBar.querySelector('.title')?.textContent?.trim();

        if (playBtn && hasBarTrack) {
            clickElement(playBtn);
            return 'true';
        }

        const v = document.querySelector('video');
        if (v && v.src && isFinite(v.duration) && v.duration > 0) {
            if (v.paused) v.play();
            else v.pause();
            return 'true';
        }
        return 'false';
    };

    window.__pinnNext = function() {
        const btn = document.querySelector('.next-button');
        if (btn) { btn.click(); return true; }
        return false;
    };

    window.__pinnPrev = function() {
        const btn = document.querySelector('.previous-button');
        if (btn) { btn.click(); return true; }
        return false;
    };

    window.__pinnSeek = function(sec) {
        const v = document.querySelector('video');
        if (v && isFinite(v.duration)) {
            v.currentTime = sec;
            return true;
        }
        return false;
    };

    window.__pinnSetVolume = function(val) {
        try {
            const v = document.querySelector('video');
            const clamped = Math.max(0, Math.min(1, val));
            if (v) {
                v.volume = clamped;
                if (clamped > 0 && v.muted) v.muted = false;
            }
            const slider = document.querySelector('#volume-slider');
            if (slider) {
                slider.value = Math.round(clamped * 100);
                slider.dispatchEvent(new Event('change'));
            }
            pollMediaState();
            return 'true';
        } catch (e) {
            return 'false';
        }
    };

    window.__pinnToggleMute = function() {
        try {
            const v = document.querySelector('video');
            if (v) {
                v.muted = !v.muted;
                pollMediaState();
                return 'true';
            }
            return 'false';
        } catch (e) {
            return 'false';
        }
    };

    window.__pinnGetVolume = function() {
        const v = document.querySelector('video');
        return v ? (v.muted ? 0 : v.volume) : 1;
    };

    window.__pinnSetBackgroundMode = function(isBackground) {
        try {
            if (isBackground) {
                document.documentElement.classList.add('pinn-bg-mode');
            } else {
                document.documentElement.classList.remove('pinn-bg-mode');
            }
            return 'true';
        } catch (e) {
            return 'error:' + e.message;
        }
    };

    // 4. Real-time playback and metadata sync with Pinn-Notch
    let lastSent = '';
    function pollMediaState() {
        try {
            // Never report ad metadata or ad cover art while an ad is showing/interrupting!
            const adShowing = document.querySelector('.ad-showing, .ad-interrupting, .ytp-ad-player-overlay, .ytp-ad-player-overlay-instream-info');
            if (adShowing) {
                skipAds();
                return;
            }

            const video = document.querySelector('video');
            const playerBar = document.querySelector('ytmusic-player-bar');

            let title = '';
            let artist = '';
            let artworkUrl = '';
            let videoId = '';

            // Always read official song metadata & album cover from ytmusic-player-bar FIRST
            // because navigator.mediaSession.metadata gets polluted by pre-roll video ads!
            if (playerBar) {
                const tEl = playerBar.querySelector('yt-formatted-string.title, .title');
                if (tEl && tEl.textContent) title = tEl.textContent.trim();

                const bEl = playerBar.querySelector('yt-formatted-string.byline, .byline');
                if (bEl && bEl.textContent) {
                    artist = bEl.textContent.trim();
                    const dot = artist.indexOf('•');
                    if (dot > 0) artist = artist.substring(0, dot).trim();
                }

                const img = playerBar.querySelector('img.image, .middle-controls img, #thumbnail img');
                if (img && img.src && !img.src.startsWith('data:') && !img.src.includes('empty')) {
                    artworkUrl = img.src;
                }
            }

            if (!artworkUrl) {
                const mainImg = document.querySelector('ytmusic-player #song-image img, #song-image yt-img-shadow img');
                if (mainImg && mainImg.src && !mainImg.src.startsWith('data:')) {
                    artworkUrl = mainImg.src;
                }
            }

            // Upgrade Google User Content / YT Music square album cover to crisp 544x544
            if (artworkUrl && (artworkUrl.includes('googleusercontent.com') || artworkUrl.includes('ggpht.com'))) {
                artworkUrl = artworkUrl.replace(/=w\d+-h\d+[^&?]*/g, '=w544-h544-l90-rj');
            }

            // Extract videoId if present for subtitle fallback
            try {
                const titleLink = document.querySelector('ytmusic-player a.ytp-title-link, ytmusic-player-bar a.yt-simple-endpoint[href*=""watch?v=""]');
                const href = (titleLink && titleLink.href) ? titleLink.href : window.location.href;
                const match = href.match(/[?&]v=([a-zA-Z0-9_-]{11})/);
                if (match && match[1]) videoId = match[1];
            } catch (e) {}

            // Fallback to navigator.mediaSession ONLY if playerBar DOM was not populated yet
            try {
                if (navigator.mediaSession && navigator.mediaSession.metadata) {
                    const meta = navigator.mediaSession.metadata;
                    if (!title && meta.title) title = meta.title;
                    if (!artist && meta.artist) artist = meta.artist;
                    if (!artworkUrl && meta.artwork && meta.artwork.length > 0) {
                        const candidate = meta.artwork[meta.artwork.length - 1].src || '';
                        if (candidate && !candidate.includes('i.ytimg.com')) {
                            artworkUrl = candidate;
                        }
                    }
                }
            } catch (e) {}

            let isPlaying = false;
            let currentTime = 0;
            let duration = 0;

            if (navigator.mediaSession && navigator.mediaSession.playbackState) {
                isPlaying = navigator.mediaSession.playbackState === 'playing';
            }

            const playPauseBtn = document.querySelector('#play-pause-button');
            if (playPauseBtn) {
                const label = playPauseBtn.getAttribute('aria-label') || playPauseBtn.getAttribute('title') || '';
                if (label.toLowerCase().includes('pause')) {
                    isPlaying = true;
                } else if (label.toLowerCase().includes('play')) {
                    isPlaying = false;
                }
            }

            if (video) {
                if (!video.paused && !video.ended) {
                    isPlaying = true;
                }
                currentTime = isFinite(video.currentTime) ? video.currentTime : 0;
                duration = isFinite(video.duration) ? video.duration : 0;
            }

            if (duration === 0 && playerBar) {
                const timeInfo = playerBar.querySelector('.time-info');
                if (timeInfo && timeInfo.textContent && timeInfo.textContent.includes('/')) {
                    const parts = timeInfo.textContent.split('/');
                    if (parts.length === 2) {
                        function parseTime(s) {
                            const segs = s.trim().split(':').map(Number);
                            if (segs.length === 2) return segs[0] * 60 + segs[1];
                            if (segs.length === 3) return segs[0] * 3600 + segs[1] * 60 + segs[2];
                            return 0;
                        }
                        if (currentTime === 0) currentTime = parseTime(parts[0]);
                        duration = parseTime(parts[1]);
                    }
                }
            }

            if (title || isPlaying) {
                let vol = 1;
                let isMuted = false;
                if (video) {
                    vol = isFinite(video.volume) ? video.volume : 1;
                    isMuted = !!video.muted;
                }

                const stateObj = {
                    type: 'playback_state',
                    title: title,
                    artist: artist,
                    artworkUrl: artworkUrl,
                    videoId: videoId,
                    isPlaying: isPlaying,
                    currentTime: Math.round(currentTime * 10) / 10,
                    duration: Math.round(duration * 10) / 10,
                    volume: Math.round(vol * 100) / 100,
                    isMuted: isMuted
                };

                const serialized = JSON.stringify(stateObj);
                if (serialized !== lastSent) {
                    if (window.chrome && window.chrome.webview && window.chrome.webview.postMessage) {
                        window.chrome.webview.postMessage(serialized);
                        lastSent = serialized;
                    }
                }
            }
        } catch (e) {}
    }

    setInterval(pollMediaState, 400);

    function attachVideoListeners() {
        const v = document.querySelector('video');
        if (v && !v.__pinnAttached) {
            v.__pinnAttached = true;
            v.addEventListener('play', pollMediaState);
            v.addEventListener('pause', pollMediaState);
            v.addEventListener('timeupdate', pollMediaState);
            v.addEventListener('volumechange', pollMediaState);
            v.addEventListener('loadedmetadata', pollMediaState);
        }
    }
    setInterval(attachVideoListeners, 1000);
})();
";

    public YouTubeMusicWindow()
    {
        InitializeComponent();

        _userDataFolder = Path.Combine(AppPaths.LocalAppDataRoot, "YouTubeMusicWebView2");
        Directory.CreateDirectory(_userDataFolder);

        Loaded += YouTubeMusicWindow_Loaded;
        Closing += YouTubeMusicWindow_Closing;
        IsVisibleChanged += YouTubeMusicWindow_IsVisibleChanged;
        StateChanged += YouTubeMusicWindow_StateChanged;
    }

    private void YouTubeMusicWindow_StateChanged(object? sender, EventArgs e)
    {
        if (_isExplicitExit) return;
        bool isBg = !IsVisible || WindowState == WindowState.Minimized;
        UpdateBackgroundThrottling(isBg);
    }

    private void YouTubeMusicWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_isExplicitExit) return;
        bool isBg = !(bool)e.NewValue || WindowState == WindowState.Minimized;
        UpdateBackgroundThrottling(isBg);
    }

    private async void UpdateBackgroundThrottling(bool isBackground)
    {
        if (_isExplicitExit) return;
        try
        {
            if (MusicWebView?.CoreWebView2 != null)
            {
                MusicWebView.CoreWebView2.MemoryUsageTargetLevel = isBackground
                    ? Microsoft.Web.WebView2.Core.CoreWebView2MemoryUsageTargetLevel.Low
                    : Microsoft.Web.WebView2.Core.CoreWebView2MemoryUsageTargetLevel.Normal;

                try
                {
                    string script = $"window.__pinnSetBackgroundMode ? window.__pinnSetBackgroundMode({isBackground.ToString().ToLowerInvariant()}) : 'false'";
                    await MusicWebView.CoreWebView2.ExecuteScriptAsync(script);
                }
                catch (Exception ex)
                {
                    RuntimeLog.Warn(LogCategory, $"Failed to toggle background mode in webview: {ex.Message}");
                }

                if (isBackground)
                {
                    await Task.Delay(1000);
                    if (!IsVisible || WindowState == WindowState.Minimized)
                    {
                        VNotch.Services.AppMemoryOptimizer.OptimizeMemory();
                    }
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // Ignored during closing / disposal
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(LogCategory, $"UpdateBackgroundThrottling error: {ex.Message}");
        }
    }

    public uint? BrowserProcessId
    {
        get
        {
            if (!_isInitialized || _isExplicitExit) return null;
            try
            {
                return MusicWebView?.CoreWebView2?.BrowserProcessId;
            }
            catch
            {
                return null;
            }
        }
    }

    public void InitializeBackground()
    {
        if (_isInitialized) return;

        // Position far off-screen and hide from taskbar to prevent any visual popup or taskbar flicker
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000;
        Top = -32000;
        Opacity = 0;

        Show();
        Hide();

        // Restore properties for when user explicitly opens the window later
        Opacity = 1;
        ShowInTaskbar = true;

        if (!_isInitialized)
        {
            _ = InitializeWebViewAsync();
        }
    }

    public void EnsureVisibleOnScreen()
    {
        ShowInTaskbar = true;
        Opacity = 1;
        if (Left < -10000 || Top < -10000)
        {
            double screenW = SystemParameters.PrimaryScreenWidth;
            double screenH = SystemParameters.PrimaryScreenHeight;
            Left = Math.Max(0, (screenW - Width) / 2);
            Top = Math.Max(0, (screenH - Height) / 2);
        }
    }

    private async void YouTubeMusicWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isInitialized) return;
        await InitializeWebViewAsync();
    }

    private async Task InitializeWebViewAsync()
    {
        if (_isInitialized) return;
        try
        {
            var envOptions = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = "--renderer-process-limit=1 --js-flags=\"--max-old-space-size=96\" --media-cache-size=16777216 --disk-cache-size=33554432 --enable-low-end-device-mode --disable-gpu-shader-disk-cache --disable-features=Translate,OptimizationHints,MediaRouter,CalculateNativeWinOcclusion --disable-background-timer-throttling"
            };
            var environment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: _userDataFolder,
                options: envOptions);

            await MusicWebView.EnsureCoreWebView2Async(environment);

            MusicWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            MusicWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            MusicWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;

            // Injected script runs on every page/document creation
            await MusicWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(TunedInjectedScript);

            MusicWebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
            MusicWebView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;

            string startUrl = YouTubeMusicUrl;
            if (!string.IsNullOrEmpty(YouTubeMusicPlayerService.Instance.LastPlayedVideoId))
            {
                startUrl = $"https://music.youtube.com/watch?v={YouTubeMusicPlayerService.Instance.LastPlayedVideoId}";
            }
            MusicWebView.CoreWebView2.Navigate(startUrl);
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogCategory, ex, "Failed to initialize YouTube Music WebView2");
            LoadingOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            string json = e.TryGetWebMessageAsString();
            if (string.IsNullOrEmpty(json)) return;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "playback_state")
            {
                string title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                string artist = root.TryGetProperty("artist", out var a) ? a.GetString() ?? "" : "";
                string artworkUrl = root.TryGetProperty("artworkUrl", out var art) ? art.GetString() ?? "" : "";
                string videoId = root.TryGetProperty("videoId", out var vid) ? vid.GetString() ?? "" : "";
                bool isPlaying = root.TryGetProperty("isPlaying", out var p) && p.GetBoolean();
                double curTime = root.TryGetProperty("currentTime", out var c) ? c.GetDouble() : 0;
                double dur = root.TryGetProperty("duration", out var d) ? d.GetDouble() : 0;
                double vol = root.TryGetProperty("volume", out var v) ? v.GetDouble() : 1.0;
                bool isMuted = root.TryGetProperty("isMuted", out var m) && m.GetBoolean();

                YouTubeMusicPlayerService.Instance.UpdateState(title, artist, artworkUrl, isPlaying, curTime, dur, videoId);
                YouTubeMusicPlayerService.Instance.UpdateVolume(vol, isMuted);
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(LogCategory, $"WebMessage error: {ex.Message}");
        }
    }

    private async void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _isInitialized = true;
        LoadingOverlay.Visibility = Visibility.Collapsed;

        try
        {
            if (e.IsSuccess && MusicWebView.CoreWebView2 != null)
            {
                await MusicWebView.CoreWebView2.ExecuteScriptAsync(TunedInjectedScript);
                bool isBg = !IsVisible || WindowState == WindowState.Minimized;
                if (isBg)
                {
                    UpdateBackgroundThrottling(true);
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(LogCategory, $"ExecuteScript error: {ex.Message}");
        }

        if (_autoPlayRequested)
        {
            _autoPlayRequested = false;
            RequestAutoPlay();
        }
    }

    public void RequestAutoPlay()
    {
        if (!_isInitialized || MusicWebView.CoreWebView2 == null)
        {
            _autoPlayRequested = true;
            return;
        }

        _autoPlayCts?.Cancel();
        _autoPlayCts = new CancellationTokenSource();
        var ct = _autoPlayCts.Token;

        Task.Run(async () =>
        {
            for (int i = 0; i < 20 && !ct.IsCancellationRequested; i++)
            {
                await Task.Delay(400, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) return;

                if (YouTubeMusicPlayerService.Instance.IsPlaying)
                {
                    return;
                }

                bool triggered = false;
                try
                {
                    triggered = await Dispatcher.InvokeAsync(PlayOrStartAsync).Task.Unwrap();
                }
                catch (Exception ex)
                {
                    RuntimeLog.Warn(LogCategory, $"AutoPlay invocation error: {ex.Message}");
                }

                // If playback was triggered, buffering, or already playing, stop the retry loop immediately!
                if (triggered)
                {
                    RuntimeLog.Info(LogCategory, "AutoPlay action successfully triggered; exiting auto-play loop.");
                    return;
                }
            }
        }, ct);
    }

    public async Task<bool> PlayOrStartAsync()
    {
        if (_isExplicitExit) return false;
        try
        {
            if (MusicWebView?.CoreWebView2 == null) return false;
            string res = await MusicWebView.CoreWebView2.ExecuteScriptAsync("window.__pinnPlayOrStart ? window.__pinnPlayOrStart() : 'no_function'");
            RuntimeLog.Info(LogCategory, $"PlayOrStartAsync result: {res}");
            if (string.IsNullOrEmpty(res)) return false;

            if (res.Contains("no_target_found") || res.Contains("no_function") || res.Contains("error") || res == "\"false\"" || res == "false")
            {
                return false;
            }

            // Any successful action or in-progress state ("clicked_...", "play_in_progress", "buffering", "starting_radio", "already_playing", "resumed_video")
            return true;
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(LogCategory, $"PlayOrStartAsync failed: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> TogglePlayPauseAsync()
    {
        if (_isExplicitExit) return false;
        try
        {
            if (MusicWebView?.CoreWebView2 == null) return false;
            string res = await MusicWebView.CoreWebView2.ExecuteScriptAsync("window.__pinnToggle ? window.__pinnToggle() : 'false'");
            if (res == "\"true\"" || res == "true") return true;

            // If toggle could not find an active track, start playback from Quick picks / Home!
            return await PlayOrStartAsync();
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(LogCategory, $"TogglePlayPauseAsync failed: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> PlayAsync()
    {
        if (_isExplicitExit) return false;
        try
        {
            if (MusicWebView?.CoreWebView2 == null) return false;
            string res = await MusicWebView.CoreWebView2.ExecuteScriptAsync("window.__pinnPlay ? window.__pinnPlay() : 'false'");
            return res == "\"true\"" || res == "true";
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(LogCategory, $"PlayAsync failed: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> PauseAsync()
    {
        if (_isExplicitExit) return false;
        try
        {
            if (MusicWebView?.CoreWebView2 == null) return false;
            string res = await MusicWebView.CoreWebView2.ExecuteScriptAsync("window.__pinnPause ? window.__pinnPause() : 'false'");
            return res == "\"true\"" || res == "true";
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(LogCategory, $"PauseAsync failed: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> NextTrackAsync()
    {
        if (_isExplicitExit) return false;
        try
        {
            if (MusicWebView?.CoreWebView2 == null) return false;
            string res = await MusicWebView.CoreWebView2.ExecuteScriptAsync("window.__pinnNext ? window.__pinnNext() : false");
            return res == "true";
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(LogCategory, $"NextTrackAsync failed: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> PreviousTrackAsync()
    {
        if (_isExplicitExit) return false;
        try
        {
            if (MusicWebView?.CoreWebView2 == null) return false;
            string res = await MusicWebView.CoreWebView2.ExecuteScriptAsync("window.__pinnPrev ? window.__pinnPrev() : false");
            return res == "true";
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(LogCategory, $"PreviousTrackAsync failed: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> SeekAsync(TimeSpan position)
    {
        if (_isExplicitExit) return false;
        try
        {
            if (MusicWebView?.CoreWebView2 == null) return false;
            double sec = position.TotalSeconds;
            string res = await MusicWebView.CoreWebView2.ExecuteScriptAsync($"window.__pinnSeek ? window.__pinnSeek({sec.ToString(CultureInfo.InvariantCulture)}) : false");
            return res == "true";
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(LogCategory, $"SeekAsync failed: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> SetVolumeAsync(double volume)
    {
        if (_isExplicitExit) return false;
        try
        {
            if (MusicWebView?.CoreWebView2 == null) return false;
            double clamped = Math.Clamp(volume, 0.0, 1.0);
            string res = await MusicWebView.CoreWebView2.ExecuteScriptAsync(
                $"window.__pinnSetVolume ? window.__pinnSetVolume({clamped.ToString(CultureInfo.InvariantCulture)}) : 'false'");
            return res == "\"true\"" || res == "true";
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(LogCategory, $"SetVolumeAsync failed: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> ToggleMuteAsync()
    {
        if (_isExplicitExit) return false;
        try
        {
            if (MusicWebView?.CoreWebView2 == null) return false;
            string res = await MusicWebView.CoreWebView2.ExecuteScriptAsync(
                "window.__pinnToggleMute ? window.__pinnToggleMute() : 'false'");
            return res == "\"true\"" || res == "true";
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(LogCategory, $"ToggleMuteAsync failed: {ex.Message}");
            return false;
        }
    }

    public async Task<double?> GetVolumeAsync()
    {
        if (_isExplicitExit) return null;
        try
        {
            if (MusicWebView?.CoreWebView2 == null) return null;
            string res = await MusicWebView.CoreWebView2.ExecuteScriptAsync(
                "window.__pinnGetVolume ? window.__pinnGetVolume() : 1");
            if (double.TryParse(res, NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
            {
                return val;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            MaximizeButton_Click(sender, e);
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            MusicWebView.CoreWebView2?.Reload();
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(LogCategory, $"Reload error: {ex.Message}");
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        if (MaximizeIconPath != null)
        {
            MaximizeIconPath.Data = Geometry.Parse(WindowState == WindowState.Maximized ? "M2,0 L10,0 L10,8 L2,8 Z M0,2 L8,2 L8,10 L0,10 Z" : "M0,0 L10,0 L10,10 L0,10 Z");
        }
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        YouTubeMusicPlayerService.Instance.Shutdown();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            YouTubeMusicPlayerService.Instance.Shutdown();
            return;
        }

        // Hide window to background/tray so audio stream continues smoothly
        Hide();
    }

    public void ForceClose()
    {
        _isExplicitExit = true;
        IsVisibleChanged -= YouTubeMusicWindow_IsVisibleChanged;
        StateChanged -= YouTubeMusicWindow_StateChanged;
        Closing -= YouTubeMusicWindow_Closing;

        try
        {
            if (MusicWebView?.CoreWebView2 != null)
            {
                MusicWebView.CoreWebView2.WebMessageReceived -= CoreWebView2_WebMessageReceived;
                MusicWebView.CoreWebView2.NavigationCompleted -= CoreWebView2_NavigationCompleted;
            }
        }
        catch { }

        try
        {
            MusicWebView?.Dispose();
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(LogCategory, $"WebView2 dispose error: {ex.Message}");
        }
        Close();
    }

    private void YouTubeMusicWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isExplicitExit)
        {
            e.Cancel = true;
            Hide();
            try
            {
                if (MusicWebView?.CoreWebView2 != null)
                {
                    MusicWebView.CoreWebView2.MemoryUsageTargetLevel = Microsoft.Web.WebView2.Core.CoreWebView2MemoryUsageTargetLevel.Low;
                }
            }
            catch { }
            VNotch.Services.AppMemoryOptimizer.OptimizeMemory();
        }
    }
}
