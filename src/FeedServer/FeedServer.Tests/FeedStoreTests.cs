using FeedServer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FeedServer.Tests;

public class FeedStoreTests
{
    [Fact]
    public async Task GetOrCreate_ExpiresInactiveFeedBeforeAccessAndCompletesReaders()
    {
        var timeProvider = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-14T00:00:00Z"));
        var store = CreateStore(timeProvider, TimeSpan.FromHours(1));
        var feedId = Guid.NewGuid();
        var readerId = Guid.NewGuid();
        var firstAccess = store.GetOrCreate(feedId, key: null);
        var reader = firstAccess.Feed!.EnsureReader(readerId);
        var pendingRead = reader.ReadAsync(TimeSpan.FromHours(1), CancellationToken.None).AsTask();

        timeProvider.Advance(TimeSpan.FromHours(1) + TimeSpan.FromTicks(1));
        var secondAccess = store.GetOrCreate(feedId, key: null);

        Assert.NotSame(firstAccess.Feed, secondAccess.Feed);
        Assert.Equal(0, secondAccess.Feed!.ReaderCount);
        Assert.True(pendingRead.IsCompletedSuccessfully);
        Assert.Null(await pendingRead);
    }

    [Fact]
    public void GetOrCreate_RecentActivityKeepsFeedAlive()
    {
        var timeProvider = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-14T00:00:00Z"));
        var store = CreateStore(timeProvider, TimeSpan.FromHours(1));
        var feedId = Guid.NewGuid();
        var firstAccess = store.GetOrCreate(feedId, key: null);

        timeProvider.Advance(TimeSpan.FromMinutes(45));
        var refreshedAccess = store.GetOrCreate(feedId, key: null);
        timeProvider.Advance(TimeSpan.FromMinutes(45));
        var stillActiveAccess = store.GetOrCreate(feedId, key: null);

        Assert.Same(firstAccess.Feed, refreshedAccess.Feed);
        Assert.Same(firstAccess.Feed, stillActiveAccess.Feed);
    }

    [Fact]
    public async Task ExpireInactiveFeeds_RemovesOnlyExpiredFeedsAndCompletesTheirReaders()
    {
        var timeProvider = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-14T00:00:00Z"));
        var store = CreateStore(timeProvider, TimeSpan.FromHours(1));
        var expiredFeedId = Guid.NewGuid();
        var activeFeedId = Guid.NewGuid();
        var expiredFeed = store.GetOrCreate(expiredFeedId, key: null).Feed!;
        var activeFeed = store.GetOrCreate(activeFeedId, key: null).Feed!;
        var expiredReader = expiredFeed.EnsureReader(Guid.NewGuid());
        var pendingRead = expiredReader.ReadAsync(TimeSpan.FromHours(1), CancellationToken.None).AsTask();

        timeProvider.Advance(TimeSpan.FromMinutes(45));
        store.GetOrCreate(activeFeedId, key: null);
        timeProvider.Advance(TimeSpan.FromMinutes(20));

        var expiredCount = store.ExpireInactiveFeeds();
        var recreatedExpiredFeed = store.GetOrCreate(expiredFeedId, key: null).Feed;
        var stillActiveFeed = store.GetOrCreate(activeFeedId, key: null).Feed;

        Assert.Equal(1, expiredCount);
        Assert.NotSame(expiredFeed, recreatedExpiredFeed);
        Assert.Same(activeFeed, stillActiveFeed);
        Assert.True(pendingRead.IsCompletedSuccessfully);
        Assert.Null(await pendingRead);
    }

    [Fact]
    public void ExpiredFeedCanBeRecreatedWithNewAccessMode()
    {
        var timeProvider = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-14T00:00:00Z"));
        var store = CreateStore(timeProvider, TimeSpan.FromHours(1));
        var feedId = Guid.NewGuid();
        var protectedAccess = store.GetOrCreate(feedId, ReadKey("secret"));

        timeProvider.Advance(TimeSpan.FromHours(1) + TimeSpan.FromTicks(1));
        var openAccess = store.GetOrCreate(feedId, key: null);

        Assert.Equal(FeedAccessMode.Protected, protectedAccess.Feed!.Mode);
        Assert.Equal(FeedAccessMode.Open, openAccess.Feed!.Mode);
        Assert.NotSame(protectedAccess.Feed, openAccess.Feed);
    }

    [Fact]
    public void RejectedAuthorizationDoesNotExtendFeedLifetime()
    {
        var timeProvider = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-14T00:00:00Z"));
        var store = CreateStore(timeProvider, TimeSpan.FromHours(1));
        var feedId = Guid.NewGuid();
        var firstAccess = store.GetOrCreate(feedId, ReadKey("secret"));

        timeProvider.Advance(TimeSpan.FromMinutes(45));
        var rejectedAccess = store.GetOrCreate(feedId, ReadKey("wrong"));
        timeProvider.Advance(TimeSpan.FromMinutes(16));
        var recreatedAccess = store.GetOrCreate(feedId, ReadKey("secret"));

        Assert.False(rejectedAccess.Succeeded);
        Assert.NotSame(firstAccess.Feed, recreatedAccess.Feed);
    }

    private static FeedStore CreateStore(ManualTimeProvider timeProvider, TimeSpan feedTtl)
    {
        return new FeedStore(
            timeProvider,
            Options.Create(new FeedServerOptions
            {
                FeedTtl = feedTtl
            }),
            NullLogger<FeedStore>.Instance);
    }

    private static FeedAuthorizationKey ReadKey(string value)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = value;

        Assert.True(FeedAuthorizationKey.TryRead(context.Request, out var key, out var problem), problem);
        return key!;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset utcNow;

        public ManualTimeProvider(DateTimeOffset utcNow)
        {
            this.utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return utcNow;
        }

        public void Advance(TimeSpan timeSpan)
        {
            utcNow += timeSpan;
        }
    }
}
