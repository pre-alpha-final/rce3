namespace FeedServer;

public static class FeedHelpText
{
    public static string Create(Guid feedId, FeedServerOptions options)
    {
        return string.Join(
            Environment.NewLine,
            $"Feed {feedId}",
            string.Empty,
            "Endpoints:",
            $"  GET  /{feedId}",
            $"  POST /{feedId}",
            $"  GET  /{feedId}/{{readerGuid}}",
            string.Empty,
            "IDs must be valid C# Guid values.",
            $"Poll timeout: {options.PollTimeout}",
            $"Feed TTL: {options.FeedTtl}",
            $"Max message size: {options.MaxMessageSizeBytes} bytes");
    }
}
