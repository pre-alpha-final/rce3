namespace FeedServer;

public sealed class FeedServerOptions
{
    public const string SectionName = "FeedServer";

    public const string ValidationFailureMessage =
        "FeedServer configuration requires Port 1-65535, positive PollTimeout, positive FeedTtl, MaxMessageSizeBytes 1-2147483647, positive MaxQueuedMessagesPerReader, positive RequestHeadersTimeout, positive MinRequestBodyDataRateBytesPerSecond, and positive MinRequestBodyDataRateGracePeriod.";

    public int Port { get; init; } = 5137;

    public TimeSpan PollTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan FeedTtl { get; init; } = TimeSpan.FromHours(24);

    public long MaxMessageSizeBytes { get; init; } = 1024 * 1024;

    public int MaxQueuedMessagesPerReader { get; init; } = 1024;

    public TimeSpan RequestHeadersTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public double MinRequestBodyDataRateBytesPerSecond { get; init; } = 240;

    public TimeSpan MinRequestBodyDataRateGracePeriod { get; init; } = TimeSpan.FromSeconds(10);

    public static bool IsValid(FeedServerOptions options)
    {
        return options.Port is >= 1 and <= 65535
            && options.PollTimeout > TimeSpan.Zero
            && options.FeedTtl > TimeSpan.Zero
            && options.MaxMessageSizeBytes is > 0 and <= int.MaxValue
            && options.MaxQueuedMessagesPerReader > 0
            && options.RequestHeadersTimeout > TimeSpan.Zero
            && options.MinRequestBodyDataRateBytesPerSecond > 0
            && options.MinRequestBodyDataRateGracePeriod > TimeSpan.Zero;
    }
}
