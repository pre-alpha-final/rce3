namespace FeedServer;

public static class FeedHelpText
{
    public static string Create(FeedState? feed, FeedServerOptions options)
    {
        var feedId = feed?.Id ?? Guid.Empty;
        var mode = feed?.Mode.ToString().ToLowerInvariant() ?? "unknown";

        return string.Join(
            Environment.NewLine,
            $"FeedId: {feedId}",
            $"Mode: {mode}",
            string.Empty,
            "Idea:",
            "  An ephemeral fan-out message channel. POST a raw request body to the feed.",
            "  Each reader long-polls its own queue and receives a copy of each message.",
            "  There is no schema or content type requirement; clients define message rules.",
            string.Empty,
            "Client contract:",
            "  GET  /{FeedId}",
            "       Create or touch the feed and return this help.",
            "  POST /{FeedId}",
            $"       Publish the raw request body to all readers. Max body: {options.MaxMessageSizeBytes} bytes.",
            "  GET  /{FeedId}/{ReaderId}",
            $"       Long-poll one message for this reader, or return empty after {options.PollTimeout}.",
            string.Empty,
            "IDs:",
            "  FeedId and ReaderId must be valid UUIDs.",
            "  ReaderId must be globally unique across feeds.",
            string.Empty,
            "Auth:",
            "  The first valid request creates the feed mode.",
            "  No Authorization header creates an open feed; open feeds reject Authorization.",
            "  Authorization: <key> creates a protected feed; send the same key every time.",
            string.Empty,
            FormattableString.Invariant($"Lifetime: expires after {options.FeedTtl.TotalHours:0.###} hours without valid GET/POST activity."));
    }
}
