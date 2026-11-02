using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace VNotch.Services;

public sealed class YouTubeMusicSleepTimerService
{
    private const string LogCategory = "SLEEP-TIMER";
    private static readonly Lazy<YouTubeMusicSleepTimerService> _lazy = new(() => new YouTubeMusicSleepTimerService());
    public static YouTubeMusicSleepTimerService Instance => _lazy.Value;

    private readonly DispatcherTimer _timer;
    private TimeSpan _remainingTime = TimeSpan.Zero;
    private TimeSpan _totalDuration = TimeSpan.Zero;
    private bool _isEndOfTrackMode = false;
    private string _initialTrackTitle = "";
    private string _selectedPresetLabel = "";

    public bool IsActive => _timer.IsEnabled;
    public TimeSpan RemainingTime => _remainingTime;
    public TimeSpan TotalDuration => _totalDuration;
    public bool IsEndOfTrackMode => _isEndOfTrackMode;
    public string SelectedPresetLabel => _selectedPresetLabel;

    public string FormattedRemainingTime
    {
        get
        {
            if (_isEndOfTrackMode) return "Akhir Lagu";
            if (_remainingTime.TotalHours >= 1)
                return $"{(int)_remainingTime.TotalHours}:{_remainingTime.Minutes:D2}:{_remainingTime.Seconds:D2}";
            return $"{_remainingTime.Minutes:D2}:{_remainingTime.Seconds:D2}";
        }
    }

    public event Action? StateChanged;

    private YouTubeMusicSleepTimerService()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += OnTimerTick;
    }

    public void StartTimer(TimeSpan duration, string presetLabel = "")
    {
        _isEndOfTrackMode = false;
        _totalDuration = duration;
        _remainingTime = duration;
        _selectedPresetLabel = string.IsNullOrEmpty(presetLabel) ? $"{(int)duration.TotalMinutes} Menit" : presetLabel;

        RuntimeLog.Log(LogCategory, $"Sleep timer started for {duration.TotalMinutes} minutes");
        _timer.Stop();
        _timer.Start();
        StateChanged?.Invoke();
    }

    public void StartEndOfTrack()
    {
        _isEndOfTrackMode = true;
        _totalDuration = TimeSpan.Zero;
        _remainingTime = TimeSpan.Zero;
        _selectedPresetLabel = "Akhir Lagu Ini";
        _initialTrackTitle = YouTubeMusicPlayerService.Instance.CurrentTrack;

        RuntimeLog.Log(LogCategory, $"Sleep timer started for end of track: '{_initialTrackTitle}'");
        _timer.Stop();
        _timer.Start();
        StateChanged?.Invoke();
    }

    public void CancelTimer()
    {
        if (!_timer.IsEnabled && !_isEndOfTrackMode) return;

        _timer.Stop();
        _isEndOfTrackMode = false;
        _remainingTime = TimeSpan.Zero;
        _totalDuration = TimeSpan.Zero;
        _selectedPresetLabel = "";

        RuntimeLog.Log(LogCategory, "Sleep timer cancelled");
        StateChanged?.Invoke();
    }

    private async void OnTimerTick(object? sender, EventArgs e)
    {
        if (_isEndOfTrackMode)
        {
            var ytm = YouTubeMusicPlayerService.Instance;
            bool trackChanged = !string.IsNullOrEmpty(_initialTrackTitle) && 
                               !string.IsNullOrEmpty(ytm.CurrentTrack) && 
                               !string.Equals(ytm.CurrentTrack, _initialTrackTitle, StringComparison.OrdinalIgnoreCase);

            bool reachedEnd = ytm.Duration > TimeSpan.Zero && 
                              ytm.Position >= ytm.Duration - TimeSpan.FromSeconds(1.5);

            if (trackChanged || reachedEnd)
            {
                RuntimeLog.Log(LogCategory, "End of track reached -> triggering sleep");
                CancelTimer();
                await StopPlaybackAsync();
                return;
            }

            StateChanged?.Invoke();
            return;
        }

        _remainingTime = _remainingTime.Subtract(TimeSpan.FromSeconds(1));

        if (_remainingTime <= TimeSpan.Zero)
        {
            RuntimeLog.Log(LogCategory, "Sleep timer expired -> triggering sleep");
            CancelTimer();
            await StopPlaybackAsync();
            return;
        }

        StateChanged?.Invoke();
    }

    private async Task StopPlaybackAsync()
    {
        try
        {
            var ytm = YouTubeMusicPlayerService.Instance;
            if (ytm.HasActiveMedia && ytm.IsPlaying)
            {
                double initialVol = ytm.Volume;
                if (initialVol > 0.1)
                {
                    // Gentle 1.2s fade out before pausing
                    await ytm.SetVolumeAsync(initialVol * 0.5);
                    await Task.Delay(400);
                    await ytm.SetVolumeAsync(initialVol * 0.15);
                    await Task.Delay(400);
                    await ytm.SetVolumeAsync(0.0);
                    await Task.Delay(200);
                }

                await ytm.PauseAsync();

                // Restore original volume for next playback session
                if (initialVol > 0.1)
                {
                    await Task.Delay(300);
                    await ytm.SetVolumeAsync(initialVol);
                }
            }
            else
            {
                Win32Interop.SendExtendedKeyTap(Win32Interop.VK_MEDIA_PLAY_PAUSE);
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(LogCategory, $"StopPlaybackAsync failed: {ex.Message}");
        }
    }
}
