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

    public FeedAccessResult GetOrCreate(Guid feedId, FeedAuthorizationKey? key)
    {
        var now = timeProvider.GetUtcNow();
        var feed = feeds.GetOrAdd(feedId, _ => CreateFeed(feedId, key, now));

        return TryAuthorizeAndTouch(feed, key, now);
    }

    private static FeedState CreateFeed(Guid feedId, FeedAuthorizationKey? key, DateTimeOffset now)
    {
        if (key is null)
        {
            return new FeedState(feedId, FeedAccessMode.Open, null, now);
        }

        return new FeedState(feedId, FeedAccessMode.Protected, (byte[])key.Hash.Clone(), now);
    }

    private static FeedAccessResult TryAuthorizeAndTouch(
        FeedState feed,
        FeedAuthorizationKey? key,
        DateTimeOffset now)
    {
        if (feed.Mode == FeedAccessMode.Open && key is not null)
        {
            return FeedAccessResult.Failure(
                StatusCodes.Status403Forbidden,
                "Open feed rejects authorization keys",
                "This feed was created as open and must be accessed without an Authorization header.");
        }

        if (feed.Mode == FeedAccessMode.Protected)
        {
            if (key is null || feed.ProtectedKeyHash is null)
            {
                return FeedAccessResult.Failure(
                    StatusCodes.Status401Unauthorized,
                    "Authorization key required",
                    "This feed is protected and requires a matching Authorization key.");
            }

            if (!key.MatchesHash(feed.ProtectedKeyHash))
            {
                return FeedAccessResult.Failure(
                    StatusCodes.Status401Unauthorized,
                    "Authorization key failed",
                    "The supplied Authorization key does not match this feed.");
            }
        }

        feed.Touch(now);
        return FeedAccessResult.Success(feed);
    }
}
