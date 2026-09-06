namespace TabletUI.Services;

public sealed record YoutubeAction(string Label, string Body);

public static class YoutubeCommands
{
    public static IReadOnlyList<YoutubeAction> All { get; } = Array.AsReadOnly<YoutubeAction>(
    [
        new("Back 1 minute", "youtube: back6"),
        new("Back 10 seconds", "youtube: back"),
        new("Play/pause", "youtube: playpause"),
        new("Forward 10 seconds", "youtube: forward"),
        new("Forward 1 minute", "youtube: forward6")
    ]);
}
