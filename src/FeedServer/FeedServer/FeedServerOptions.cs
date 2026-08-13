namespace FeedServer;

public sealed class FeedServerOptions
{
    public const string SectionName = "FeedServer";

    public const string ValidationFailureMessage =
        "FeedServer configuration requires Port 1-65535, positive PollTimeout, positive FeedTtl, and positive MaxMessageSizeBytes.";

    public int Port { get; init; } = 5137;

    public TimeSpan PollTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan FeedTtl { get; init; } = TimeSpan.FromHours(24);

    public long MaxMessageSizeBytes { get; init; } = 1024 * 1024;

    public static bool IsValid(FeedServerOptions options)
    {
        return options.Port is >= 1 and <= 65535
            && options.PollTimeout > TimeSpan.Zero
            && options.FeedTtl > TimeSpan.Zero
            && options.MaxMessageSizeBytes > 0;
    }
}
