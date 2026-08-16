namespace FeedServer;

internal static partial class FeedServerLog
{
    [LoggerMessage(1, LogLevel.Warning, "Rejected malformed feed route {FeedGuid}: {Problem}")]
    public static partial void MalformedFeedRoute(ILogger logger, string feedGuid, string problem);

    [LoggerMessage(2, LogLevel.Warning, "Rejected invalid authorization for feed {FeedId}: {Problem}")]
    public static partial void InvalidAuthorization(ILogger logger, Guid feedId, string problem);

    [LoggerMessage(3, LogLevel.Warning, "Rejected payload for feed {FeedId}: {ContentLength} bytes exceeds {MaxMessageSizeBytes} bytes.")]
    public static partial void DeclaredPayloadTooLarge(
        ILogger logger,
        Guid feedId,
        long? contentLength,
        long maxMessageSizeBytes);

    [LoggerMessage(4, LogLevel.Warning, "Rejected streaming payload for feed {FeedId}: body exceeds {MaxMessageSizeBytes} bytes.")]
    public static partial void StreamingPayloadTooLarge(ILogger logger, Guid feedId, long maxMessageSizeBytes);

    [LoggerMessage(5, LogLevel.Information, "Posted {MessageSize} byte message to feed {FeedId}; distributed to {ReaderCount} reader(s).")]
    public static partial void MessagePosted(ILogger logger, int messageSize, Guid feedId, int readerCount);

    [LoggerMessage(6, LogLevel.Warning, "Rejected malformed reader route {FeedGuid}/{ReaderGuid}: {Problem}")]
    public static partial void MalformedReaderRoute(
        ILogger logger,
        string feedGuid,
        string readerGuid,
        string problem);

    [LoggerMessage(7, LogLevel.Warning, "Rejected unusable reader {ReaderId} for feed {FeedId}.")]
    public static partial void UnusableReader(ILogger logger, Guid readerId, Guid feedId);

    [LoggerMessage(8, LogLevel.Warning, "Rejected unusable reader {ReaderId} for feed {FeedId} after poll completion.")]
    public static partial void UnusableReaderAfterPoll(ILogger logger, Guid readerId, Guid feedId);

    [LoggerMessage(9, LogLevel.Debug, "Reader {ReaderId} completed with no message for feed {FeedId}.")]
    public static partial void EmptyReaderPoll(ILogger logger, Guid readerId, Guid feedId);

    [LoggerMessage(10, LogLevel.Warning, "Rejected malformed reader reset route {FeedGuid}/{ReaderGuid}: {Problem}")]
    public static partial void MalformedReaderResetRoute(
        ILogger logger,
        string feedGuid,
        string readerGuid,
        string problem);

    [LoggerMessage(11, LogLevel.Information, "Reset reader {ReaderId} for feed {FeedId}.")]
    public static partial void ReaderReset(ILogger logger, Guid readerId, Guid feedId);

    [LoggerMessage(12, LogLevel.Warning, "Rejected unsupported feed path {Path}.")]
    public static partial void UnsupportedFeedPath(ILogger logger, PathString path);

    [LoggerMessage(13, LogLevel.Information, "Created open feed {FeedId}.")]
    public static partial void OpenFeedCreated(ILogger logger, Guid feedId);

    [LoggerMessage(14, LogLevel.Information, "Created protected feed {FeedId}.")]
    public static partial void ProtectedFeedCreated(ILogger logger, Guid feedId);

    [LoggerMessage(15, LogLevel.Warning, "Rejected authorized request for open feed {FeedId}.")]
    public static partial void AuthorizationRejectedForOpenFeed(ILogger logger, Guid feedId);

    [LoggerMessage(16, LogLevel.Warning, "Rejected unauthorized request for protected feed {FeedId}.")]
    public static partial void AuthorizationRequiredForProtectedFeed(ILogger logger, Guid feedId);

    [LoggerMessage(17, LogLevel.Warning, "Rejected request with mismatched authorization key for protected feed {FeedId}.")]
    public static partial void AuthorizationMismatchForProtectedFeed(ILogger logger, Guid feedId);

    [LoggerMessage(18, LogLevel.Information, "Expired feed {FeedId} after {FeedTtl} without activity.")]
    public static partial void FeedExpired(ILogger logger, Guid feedId, TimeSpan feedTtl);

    [LoggerMessage(19, LogLevel.Information, "Expired {ExpiredFeedCount} inactive feed(s).")]
    public static partial void InactiveFeedsExpired(ILogger logger, int expiredFeedCount);
}
