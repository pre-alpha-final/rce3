using TabletUI.Services;

namespace TabletUI.Tests;

public sealed class YoutubeCommandsTests
{
    [Fact]
    public void Commands_have_exact_labels_order_and_wire_text()
    {
        Assert.Equal(
        [
            new YoutubeAction("Back 1 minute", "youtube: back6"),
            new YoutubeAction("Back 10 seconds", "youtube: back"),
            new YoutubeAction("Play/pause", "youtube: playpause"),
            new YoutubeAction("Forward 10 seconds", "youtube: forward"),
            new YoutubeAction("Forward 1 minute", "youtube: forward6")
        ], YoutubeCommands.All);
    }
}
