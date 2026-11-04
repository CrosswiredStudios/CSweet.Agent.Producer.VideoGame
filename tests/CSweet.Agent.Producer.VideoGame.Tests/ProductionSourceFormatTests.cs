using CrosswiredStudios.VideoGame.ProductionReview;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class ProductionSourceFormatTests
{
    [Fact]
    public void OriginalsStayReadableAndRoundTripExactlyIncludingNestedMarkers()
    {
        var pitch = "# Approved pitch\r\n\r\nKeep keyboard support.\n\n## Accepted GDD\nThis heading is source text.";
        var gdd = "# Design\n\n```json\n{}\n```\n\nTwo explicit controls.";
        var sources = new ProductionSources(1, Guid.NewGuid(), Guid.NewGuid(),
            new(Guid.NewGuid(), Guid.NewGuid(), "pitch-hash", "video-game.vision.v1", pitch),
            new(Guid.NewGuid(), Guid.NewGuid(), "gdd-hash", "video-game.gdd.v1", gdd));
        var content = sources.Serialize();
        Assert.Contains(pitch, content); Assert.Contains(gdd, content);
        Assert.Equal(sources, ProductionSources.Parse(content));
        Assert.Throws<InvalidOperationException>(() => ProductionSources.Parse(content + "extra"));
    }

    [Theory]
    [InlineData("<!-- accepted-pitch-sources -->")]
    [InlineData("<!-- production-sources-reference -->")]
    public void LegacyAndCurrentReferencesDoNotRepeatInWorkingContext(string marker) =>
        Assert.Equal("Working brief", ProductionSources.WorkingBrief("Working brief\r\n" + marker + "\r\nRetained source evidence"));
}
