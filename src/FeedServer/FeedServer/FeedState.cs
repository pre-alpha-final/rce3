using System.Collections.Concurrent;
using System.Threading.Channels;

namespace FeedServer;

public sealed class FeedState
{
    private readonly ConcurrentDictionary<Guid, FeedReaderState> readers = new();
    private readonly int maxQueuedMessagesPerReader;

    public FeedState(
        Guid id,
        FeedAccessMode mode,
        byte[]? protectedKeyHash,
        DateTimeOffset createdAt,
        int maxQueuedMessagesPerReader)
    {
        Id = id;
        Mode = mode;
        ProtectedKeyHash = protectedKeyHash;
        CreatedAt = createdAt;
        LastActivityAt = createdAt;
        this.maxQueuedMessagesPerReader = maxQueuedMessagesPerReader;
    }

    public Guid Id { get; }

    public FeedAccessMode Mode { get; }

    public byte[]? ProtectedKeyHash { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset LastActivityAt { get; private set; }

    public int ReaderCount => readers.Count;

    public FeedReaderState EnsureReader(Guid readerId)
    {
        return readers.GetOrAdd(readerId, _ => new FeedReaderState(maxQueuedMessagesPerReader));
    }

    public int Publish(FeedMessage message)
    {
        foreach (var reader in readers.Values)
        {
            reader.Enqueue(message);
        }

        return readers.Count;
    }

    public void Touch(DateTimeOffset activityAt)
    {
        LastActivityAt = activityAt;
    }
}

public sealed record FeedMessage(byte[] Body, string? ContentType);

public sealed class FeedReaderState
{
    private readonly Channel<FeedMessage> messages;
    private int unusable;

    public FeedReaderState(int maxQueuedMessages)
    {
        messages = Channel.CreateBounded<FeedMessage>(
            new BoundedChannelOptions(maxQueuedMessages)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false
            });
    }

    public bool IsUnusable => Volatile.Read(ref unusable) == 1;

    public void Enqueue(FeedMessage message)
    {
        if (IsUnusable)
        {
            return;
        }

        if (!messages.Writer.TryWrite(message))
        {
            MarkUnusable();
        }
    }

    public async ValueTask<FeedMessage?> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            return await messages.Reader.ReadAsync(linkedCts.Token);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private void MarkUnusable()
    {
        if (Interlocked.Exchange(ref unusable, 1) == 0)
        {
            messages.Writer.TryComplete();
        }
    }
}
