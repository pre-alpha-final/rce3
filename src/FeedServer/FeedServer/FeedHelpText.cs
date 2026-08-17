namespace FeedServer;

public static class FeedHelpText
{
    public static string Create(Guid feedId, FeedServerOptions options)
    {
        return string.Join(
            Environment.NewLine,
            $"FeedId: {feedId}",
            string.Empty,
            "Idea:",
            "  An ephemeral fan-out message channel. POST a raw request body to the feed.",
            "  Each reader long-polls its own queue and receives a copy of each message.",
            "  There is no schema or content type requirement; clients define message rules.",
            string.Empty,
            "Client contract:",
            "  GET  /{FeedId}/{ReaderId}",
            $"       Long-poll one message for this reader, or return empty after {options.PollTimeout}.",
            $"       Reader queues become unusable after exceeding {options.MaxQueuedMessagesPerReader} queued messages.",
            "  POST /{FeedId}",
            $"       Publish the raw request body to all readers. Max body: {options.MaxMessageSizeBytes} bytes.",
            "",
            "  GET  /{FeedId}",
            "       Return this help without creating, touching, or authorizing the feed.",
            "  GET  /{FeedId}/admin",
            "       Open the browser debug client without creating or touching the feed.",
            "  GET  /{FeedId}/{ReaderId}/reset",
            "       Clear this reader queue, make it usable again, and redirect to it.",
            string.Empty,
            "IDs:",
            "  FeedId and ReaderId must be valid UUIDs.",
            string.Empty,
            "Auth:",
            "  The first valid feed request creates the feed mode.",
            "  No Authorization header creates an open feed; open feeds reject Authorization.",
            "  Authorization: <key> creates a protected feed; send the same key every time.",
            string.Empty,
            FormattableString.Invariant($"Lifetime: expires after {options.FeedTtl.TotalHours:0.###} hours without valid GET/POST activity."));
    }
}
