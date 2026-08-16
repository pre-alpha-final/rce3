using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FeedServer;

public sealed class FeedStore
{
    private readonly ConcurrentDictionary<Guid, FeedState> feeds = new();
    private readonly TimeSpan feedTtl;
    private readonly ILogger<FeedStore> logger;
    private readonly int maxQueuedMessagesPerReader;
    private readonly TimeProvider timeProvider;

    public FeedStore(TimeProvider timeProvider, IOptions<FeedServerOptions> options)
        : this(timeProvider, options, NullLogger<FeedStore>.Instance)
    {
    }

    public FeedStore(
        TimeProvider timeProvider,
        IOptions<FeedServerOptions> options,
        ILogger<FeedStore> logger)
    {
        this.timeProvider = timeProvider;
        this.logger = logger;
        feedTtl = options.Value.FeedTtl;
        maxQueuedMessagesPerReader = options.Value.MaxQueuedMessagesPerReader;
    }

    public FeedAccessResult GetOrCreate(Guid feedId, FeedAuthorizationKey? key)
    {
        var now = timeProvider.GetUtcNow();

        while (true)
        {
            var feed = feeds.GetOrAdd(feedId, _ => CreateFeed(feedId, key, now));
            if (TryExpire(feedId, feed, now))
            {
                continue;
            }

            var access = TryAuthorizeAndTouch(feed, key, now);
            if (access is not null)
            {
                return access;
            }
        }
    }

    public FeedAccessResult? AuthorizeExisting(Guid feedId, FeedAuthorizationKey? key)
    {
        var now = timeProvider.GetUtcNow();

        while (true)
        {
            if (!feeds.TryGetValue(feedId, out var feed))
            {
                return null;
            }

            if (TryExpire(feedId, feed, now))
            {
                continue;
            }

            return TryAuthorize(feed, key);
        }
    }

    public int ExpireInactiveFeeds()
    {
        var now = timeProvider.GetUtcNow();
        var expiredCount = 0;

        foreach (var pair in feeds)
        {
            if (TryExpire(pair.Key, pair.Value, now))
            {
                expiredCount++;
            }
        }

        return expiredCount;
    }

    private FeedState CreateFeed(Guid feedId, FeedAuthorizationKey? key, DateTimeOffset now)
    {
        if (key is null)
        {
            FeedServerLog.OpenFeedCreated(logger, feedId);
            return new FeedState(feedId, FeedAccessMode.Open, null, now, maxQueuedMessagesPerReader);
        }

        FeedServerLog.ProtectedFeedCreated(logger, feedId);
        return new FeedState(
            feedId,
            FeedAccessMode.Protected,
            (byte[])key.Hash.Clone(),
            now,
            maxQueuedMessagesPerReader);
    }

    private FeedAccessResult? TryAuthorizeAndTouch(
        FeedState feed,
        FeedAuthorizationKey? key,
        DateTimeOffset now)
    {
        var access = TryAuthorize(feed, key);
        if (access is null || !access.Succeeded)
        {
            return access;
        }

        if (!feed.TryTouch(now))
        {
            return null;
        }

        return access;
    }

    private FeedAccessResult? TryAuthorize(FeedState feed, FeedAuthorizationKey? key)
    {
        if (feed.Mode == FeedAccessMode.Open && key is not null)
        {
            FeedServerLog.AuthorizationRejectedForOpenFeed(logger, feed.Id);
            return FeedAccessResult.Failure(
                StatusCodes.Status403Forbidden,
                "Open feed rejects authorization keys",
                "This feed was created as open and must be accessed without an Authorization header.");
        }

        if (feed.Mode == FeedAccessMode.Protected)
        {
            if (key is null || feed.ProtectedKeyHash is null)
            {
                FeedServerLog.AuthorizationRequiredForProtectedFeed(logger, feed.Id);
                return FeedAccessResult.Failure(
                    StatusCodes.Status401Unauthorized,
                    "Authorization key required",
                    "This feed is protected and requires a matching Authorization key.");
            }

            if (!key.MatchesHash(feed.ProtectedKeyHash))
            {
                FeedServerLog.AuthorizationMismatchForProtectedFeed(logger, feed.Id);
                return FeedAccessResult.Failure(
                    StatusCodes.Status401Unauthorized,
                    "Authorization key failed",
                    "The supplied Authorization key does not match this feed.");
            }
        }

        return FeedAccessResult.Success(feed);
    }

    private bool TryExpire(Guid feedId, FeedState feed, DateTimeOffset now)
    {
        if (!feed.TryBeginExpiration(now, feedTtl))
        {
            return false;
        }

        if (!TryRemove(feedId, feed))
        {
            return false;
        }

        Expire(feed);
        return true;
    }

    private bool TryRemove(Guid feedId, FeedState feed)
    {
        return ((ICollection<KeyValuePair<Guid, FeedState>>)feeds).Remove(
            new KeyValuePair<Guid, FeedState>(feedId, feed));
    }

    private void Expire(FeedState feed)
    {
        feed.CompleteReaders();
        FeedServerLog.FeedExpired(logger, feed.Id, feedTtl);
    }
}
