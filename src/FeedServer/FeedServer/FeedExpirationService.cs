using Microsoft.Extensions.Options;

namespace FeedServer;

public sealed class FeedExpirationService : BackgroundService
{
    private static readonly TimeSpan MaximumCleanupInterval = TimeSpan.FromMinutes(1);

    private readonly FeedStore feedStore;
    private readonly ILogger<FeedExpirationService> logger;
    private readonly FeedServerOptions options;
    private readonly TimeProvider timeProvider;

    public FeedExpirationService(
        FeedStore feedStore,
        IOptions<FeedServerOptions> options,
        TimeProvider timeProvider,
        ILogger<FeedExpirationService> logger)
    {
        this.feedStore = feedStore;
        this.options = options.Value;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(GetCleanupInterval(options.FeedTtl), timeProvider);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var expiredFeeds = feedStore.ExpireInactiveFeeds();
                if (expiredFeeds > 0)
                {
                    FeedServerLog.InactiveFeedsExpired(logger, expiredFeeds);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private static TimeSpan GetCleanupInterval(TimeSpan feedTtl)
    {
        var ttlHalf = TimeSpan.FromMilliseconds(feedTtl.TotalMilliseconds / 2);
        if (ttlHalf <= TimeSpan.Zero)
        {
            return feedTtl;
        }

        return ttlHalf < MaximumCleanupInterval ? ttlHalf : MaximumCleanupInterval;
    }
}
