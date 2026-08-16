using FeedServer;

namespace FeedServer.Tests;

public class FeedReaderStateTests
{
    [Fact]
    public async Task ReadAsync_ReturnsQueuedMessagesImmediatelyOneAtATime()
    {
        var reader = new FeedReaderState(maxQueuedMessages: 10);
        var first = Message("first");
        var second = Message("second");

        reader.Enqueue(first);
        reader.Enqueue(second);

        var firstRead = reader.ReadAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        var secondRead = reader.ReadAsync(TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.True(firstRead.IsCompletedSuccessfully);
        Assert.True(secondRead.IsCompletedSuccessfully);
        Assert.Same(first, await firstRead);
        Assert.Same(second, await secondRead);
    }

    [Fact]
    public async Task ReadAsync_ReturnsNullAfterTimeout()
    {
        var reader = new FeedReaderState(maxQueuedMessages: 10);

        var message = await reader.ReadAsync(TimeSpan.FromMilliseconds(20), CancellationToken.None);

        Assert.Null(message);
    }

    [Fact]
    public async Task ReadAsync_WakesPendingReaderWhenMessageArrives()
    {
        var reader = new FeedReaderState(maxQueuedMessages: 10);
        var message = Message("wake");

        var readTask = reader.ReadAsync(TimeSpan.FromSeconds(30), CancellationToken.None).AsTask();
        Assert.False(readTask.IsCompleted);

        reader.Enqueue(message);

        var completed = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(readTask, completed);
        Assert.Same(message, await readTask);
    }

    [Fact]
    public async Task ReadAsync_CancelledPollDoesNotConsumeLaterMessage()
    {
        var reader = new FeedReaderState(maxQueuedMessages: 10);
        using var cts = new CancellationTokenSource();

        var readTask = reader.ReadAsync(TimeSpan.FromSeconds(30), cts.Token).AsTask();

        await cts.CancelAsync();
        Assert.Null(await readTask);

        var message = Message("after-cancel");
        reader.Enqueue(message);

        Assert.Same(message, await reader.ReadAsync(TimeSpan.FromSeconds(30), CancellationToken.None));
    }

    [Fact]
    public void TryBeginExpiration_ReturnsFalseWhenFeedWasRecentlyTouched()
    {
        var createdAt = DateTimeOffset.Parse("2026-08-14T00:00:00Z");
        var ttl = TimeSpan.FromHours(1);
        var feed = new FeedState(Guid.NewGuid(), FeedAccessMode.Open, null, createdAt, maxQueuedMessagesPerReader: 10);
        var cleanupNow = createdAt + ttl + TimeSpan.FromTicks(1);

        Assert.True(feed.TryTouch(cleanupNow));

        Assert.False(feed.TryBeginExpiration(cleanupNow, ttl));
    }

    [Fact]
    public void TryTouch_ReturnsFalseAfterExpirationBegins()
    {
        var createdAt = DateTimeOffset.Parse("2026-08-14T00:00:00Z");
        var ttl = TimeSpan.FromHours(1);
        var feed = new FeedState(Guid.NewGuid(), FeedAccessMode.Open, null, createdAt, maxQueuedMessagesPerReader: 10);
        var cleanupNow = createdAt + ttl + TimeSpan.FromTicks(1);

        Assert.True(feed.TryBeginExpiration(cleanupNow, ttl));

        Assert.False(feed.TryTouch(cleanupNow));
    }

    [Fact]
    public async Task Publish_ConcurrentPostsFanOutEveryMessageToEveryReader()
    {
        const int readerCount = 20;
        const int messageCount = 100;

        var feed = new FeedState(
            Guid.NewGuid(),
            FeedAccessMode.Open,
            protectedKeyHash: null,
            DateTimeOffset.Parse("2026-08-14T00:00:00Z"),
            maxQueuedMessagesPerReader: messageCount);
        var readers = Enumerable
            .Range(0, readerCount)
            .Select(_ => feed.EnsureReader(Guid.NewGuid()))
            .ToArray();
        var messages = Enumerable
            .Range(0, messageCount)
            .Select(index => Message($"message-{index}"))
            .ToArray();

        await Task.WhenAll(messages.Select(message => Task.Run(() => feed.Publish(message))));

        foreach (var reader in readers)
        {
            var receivedBodies = new HashSet<string>();
            for (var i = 0; i < messageCount; i++)
            {
                var received = await reader.ReadAsync(TimeSpan.FromSeconds(1), CancellationToken.None);

                Assert.NotNull(received);
                receivedBodies.Add(System.Text.Encoding.UTF8.GetString(received.Body));
            }

            Assert.Equal(messageCount, receivedBodies.Count);
        }
    }

    [Fact]
    public void Publish_ReturnsOnlyReadersThatAcceptedMessage()
    {
        var feed = new FeedState(
            Guid.NewGuid(),
            FeedAccessMode.Open,
            protectedKeyHash: null,
            DateTimeOffset.Parse("2026-08-14T00:00:00Z"),
            maxQueuedMessagesPerReader: 1);

        feed.EnsureReader(Guid.NewGuid());

        Assert.Equal(1, feed.Publish(Message("first")));
        Assert.Equal(0, feed.Publish(Message("second")));
    }

    private static FeedMessage Message(string body)
    {
        return new FeedMessage(System.Text.Encoding.UTF8.GetBytes(body), "text/plain");
    }
}
