using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WebServer;

internal sealed class FeedReader(
    FeedClient feed,
    string mediaType,
    Func<byte[], CancellationToken, Task> receive,
    Action<bool>? readerReadyChanged,
    ILogger<FeedReader> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var ready = false;
        var needsReset = true;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                FeedPoll poll;
                try
                {
                    if (needsReset)
                    {
                        await feed.ResetAsync(stoppingToken);
                        needsReset = false;
                        SetReady(true);
                    }

                    poll = await feed.PollAsync(mediaType, stoppingToken);
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested
                    && (FeedClient.IsTransportFailure(exception) || exception is FeedStatusException { IsTransient: true }))
                {
                    SetReady(false);
                    logger.LogWarning("{Reason} Retrying.", (exception as FeedStatusException)?.Message ?? "Feed connection failed.");
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    continue;
                }

                if (poll.Overflow)
                {
                    SetReady(false);
                    logger.LogWarning("Reader queue overflowed. Resetting.");
                    needsReset = true;
                    continue;
                }

                SetReady(true);
                if (poll.Message is not null)
                    await receive(poll.Message, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown cancels the outstanding poll or feed write.
        }
        finally
        {
            SetReady(false);
        }

        void SetReady(bool value)
        {
            if (ready == value)
                return;

            ready = value;
            readerReadyChanged?.Invoke(value);
            if (value)
                logger.LogInformation("Reader ready.");
        }
    }
}
