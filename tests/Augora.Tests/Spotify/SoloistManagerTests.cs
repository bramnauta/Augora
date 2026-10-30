using Augora.Contracts;
using Augora.Providers.Spotify;
using Augora.Providers.Spotify.Soloist;

namespace Augora.Tests.Spotify;

public sealed class SoloistManagerTests
{
    [Fact]
    public void The_build_date_is_read_from_the_version()
    {
        const string version = "soloist 1.3.10.28 build 1793296881 (20261029) (g19412df6ea) (linux/x86_64)";

        Assert.Equal(new DateTimeOffset(2026, 10, 29, 0, 0, 0, TimeSpan.Zero), SoloistManager.ParseBuildDate(version));
        Assert.Null(SoloistManager.ParseBuildDate("soloist without a date"));
    }

    [Fact]
    public void Sink_names_are_safe_for_pulseaudio()
    {
        var account = new SpotifyAccount("Bram.Nauta-1", "Bram", null, null, "refresh", SpotifyEngine.Soloist, "key");

        Assert.Equal("ac_bram_nauta_1", SoloistManager.SinkName(account));
        Assert.Equal("Augora (Bram)", SoloistManager.DeviceName(account));
    }
}
