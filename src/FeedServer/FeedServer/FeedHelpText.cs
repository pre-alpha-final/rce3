namespace FeedServer;

public static class FeedHelpText
{
    public static string Create(FeedState? feed, FeedServerOptions options)
    {
        var feedId = feed?.Id ?? Guid.Empty;
        var mode = feed?.Mode.ToString().ToLowerInvariant() ?? "unknown";
        var lastActivity = feed?.LastActivityAt.ToString("O") ?? "unknown";
        var readerCount = feed?.ReaderCount.ToString() ?? "unknown";

        return string.Join(
            Environment.NewLine,
            $"Feed {feedId}",
            $"Mode: {mode}",
            $"Readers: {readerCount}",
            $"Last activity: {lastActivity}",
            string.Empty,
            "Endpoints:",
            $"  GET  /{feedId}",
            $"  POST /{feedId}",
            $"  GET  /{feedId}/{{readerGuid}}",
            string.Empty,
            "Auth:",
            "  Missing feeds are created by the first valid request.",
            "  Requests without Basic authorization create open feeds.",
            "  Requests with Basic authorization create protected feeds.",
            "  Open feeds reject Basic authorization.",
            "  Protected feeds require the same Basic authorization credentials on every request.",
            string.Empty,
            "IDs must be valid C# Guid values.",
            $"Poll timeout: {options.PollTimeout}",
            $"Feed TTL: {options.FeedTtl}",
            $"Max message size: {options.MaxMessageSizeBytes} bytes");
    }
}
