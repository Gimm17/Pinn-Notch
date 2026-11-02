using System;
using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class YouTubeMusicPlayerServiceTests
{
    [Fact]
    public void Instance_IsNotNull_AndConfiguredAsDefault()
    {
        var service = YouTubeMusicPlayerService.Instance;
        Assert.NotNull(service);
        Assert.True(service.IsConfiguredAsDefault);
    }

    [Fact]
    public void ConfigurationFlag_CanBeToggled()
    {
        var service = YouTubeMusicPlayerService.Instance;
        bool original = service.IsConfiguredAsDefault;

        try
        {
            service.IsConfiguredAsDefault = false;
            Assert.False(service.IsConfiguredAsDefault);

            service.IsConfiguredAsDefault = true;
            Assert.True(service.IsConfiguredAsDefault);
        }
        finally
        {
            service.IsConfiguredAsDefault = original;
        }
    }

    [Fact]
    public void UpdateState_PopulatesState_AndRaisesEvent()
    {
        var service = YouTubeMusicPlayerService.Instance;
        bool eventFired = false;
        Action handler = () => eventFired = true;
        service.MediaStateChanged += handler;

        try
        {
            service.UpdateState("Ini Abadi", "Perunggu", "https://example.com/art.jpg", true, 54.0, 233.0);

            Assert.True(eventFired);
            Assert.Equal("Ini Abadi", service.CurrentTrack);
            Assert.Equal("Perunggu", service.CurrentArtist);
            Assert.True(service.IsPlaying);
            Assert.True(service.HasActiveMedia);
            Assert.Equal(54.0, service.Position.TotalSeconds);
            Assert.Equal(233.0, service.Duration.TotalSeconds);

            var info = new MediaInfo();
            service.PopulateMediaInfo(info);

            Assert.True(info.IsAnyMediaPlaying);
            Assert.True(info.IsPlaying);
            Assert.Equal("Ini Abadi", info.CurrentTrack);
            Assert.Equal("Perunggu", info.CurrentArtist);
            Assert.Equal("YouTube Music", info.MediaSource);
            Assert.Equal(54.0, info.Position.TotalSeconds);
            Assert.Equal(233.0, info.Duration.TotalSeconds);
            Assert.True(info.IsSeekEnabled);
        }
        finally
        {
            service.MediaStateChanged -= handler;
            service.UpdateState("", "", "", false, 0, 0);
        }
    }

    [Fact]
    public void UpdateVolume_UpdatesProperties_AndRaisesEvent()
    {
        var service = YouTubeMusicPlayerService.Instance;
        double reportedVol = -1;
        bool reportedMute = false;
        bool eventFired = false;

        Action<double, bool> handler = (v, m) =>
        {
            reportedVol = v;
            reportedMute = m;
            eventFired = true;
        };

        service.VolumeChanged += handler;
        try
        {
            service.UpdateVolume(0.75, false);

            Assert.True(eventFired);
            Assert.Equal(0.75, service.Volume, 2);
            Assert.False(service.IsMuted);
            Assert.Equal(0.75, reportedVol, 2);
            Assert.False(reportedMute);

            eventFired = false;
            service.UpdateVolume(0.0, true);

            Assert.True(eventFired);
            Assert.Equal(0.0, service.Volume, 2);
            Assert.True(service.IsMuted);
            Assert.True(reportedMute);
        }
        finally
        {
            service.VolumeChanged -= handler;
            service.UpdateVolume(1.0, false);
        }
    }
}
