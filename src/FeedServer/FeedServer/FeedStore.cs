using System.Collections.Concurrent;

namespace FeedServer;

public sealed class FeedStore
{
    private readonly ConcurrentDictionary<Guid, FeedState> feeds = new();
    private readonly TimeProvider timeProvider;

    public FeedStore(TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider;
    }

    public FeedAccessResult GetOrCreate(Guid feedId, BasicAuthCredential? credential)
    {
        var now = timeProvider.GetUtcNow();
        var feed = feeds.GetOrAdd(feedId, _ => CreateFeed(feedId, credential, now));

        return TryAuthorizeAndTouch(feed, credential, now);
    }

    private static FeedState CreateFeed(Guid feedId, BasicAuthCredential? credential, DateTimeOffset now)
    {
        if (credential is null)
        {
            return new FeedState(feedId, FeedAccessMode.Open, null, now);
        }

        return new FeedState(feedId, FeedAccessMode.Protected, (byte[])credential.Hash.Clone(), now);
    }

    private static FeedAccessResult TryAuthorizeAndTouch(
        FeedState feed,
        BasicAuthCredential? credential,
        DateTimeOffset now)
    {
        if (feed.Mode == FeedAccessMode.Open && credential is not null)
        {
            return FeedAccessResult.Failure(
                StatusCodes.Status403Forbidden,
                "Open feed rejects Basic authorization",
                "This feed was created as open and must be accessed without Basic authorization.");
        }

        if (feed.Mode == FeedAccessMode.Protected)
        {
            if (credential is null || feed.ProtectedKeyHash is null)
            {
                return FeedAccessResult.Failure(
                    StatusCodes.Status401Unauthorized,
                    "Basic authorization required",
                    "This feed is protected and requires matching Basic authorization.");
            }

            if (!credential.MatchesHash(feed.ProtectedKeyHash))
            {
                return FeedAccessResult.Failure(
                    StatusCodes.Status401Unauthorized,
                    "Basic authorization failed",
                    "The supplied Basic authorization credentials do not match this feed.");
            }
        }

        feed.Touch(now);
        return FeedAccessResult.Success(feed);
    }
}
