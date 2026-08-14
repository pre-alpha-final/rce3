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

    private static FeedMessage Message(string body)
    {
        return new FeedMessage(System.Text.Encoding.UTF8.GetBytes(body), "text/plain");
    }
}
