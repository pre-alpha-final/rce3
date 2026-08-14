using Microsoft.Extensions.Options;

namespace FeedServer;

public sealed class FeedExpirationService : BackgroundService
{
    private static readonly TimeSpan MaximumCleanupInterval = TimeSpan.FromMinutes(1);

    private readonly FeedStore feedStore;
    private readonly ILogger<FeedExpirationService> logger;
    private readonly FeedServerOptions options;

    public FeedExpirationService(
        FeedStore feedStore,
        IOptions<FeedServerOptions> options,
        ILogger<FeedExpirationService> logger)
    {
        this.feedStore = feedStore;
        this.options = options.Value;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(GetCleanupInterval(options.FeedTtl));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var expiredFeeds = feedStore.ExpireInactiveFeeds();
                if (expiredFeeds > 0)
                {
                    logger.LogInformation("Expired {ExpiredFeedCount} inactive feed(s).", expiredFeeds);
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
