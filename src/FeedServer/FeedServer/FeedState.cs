using System.Collections.Concurrent;
using System.Threading.Channels;

namespace FeedServer;

public sealed class FeedState
{
    private readonly ConcurrentDictionary<Guid, FeedReaderState> readers = new();
    private readonly Lock activityLock = new();
    private readonly int maxQueuedMessagesPerReader;
    private bool isExpiring;
    private DateTimeOffset lastActivityAt;

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
        lastActivityAt = createdAt;
        this.maxQueuedMessagesPerReader = maxQueuedMessagesPerReader;
    }

    public Guid Id { get; }

    public FeedAccessMode Mode { get; }

    public byte[]? ProtectedKeyHash { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset LastActivityAt
    {
        get
        {
            lock (activityLock)
            {
                return lastActivityAt;
            }
        }
    }

    public int ReaderCount => readers.Count;

    public FeedReaderState EnsureReader(Guid readerId)
    {
        return readers.GetOrAdd(readerId, _ => new FeedReaderState(maxQueuedMessagesPerReader));
    }

    public int Publish(FeedMessage message)
    {
        var deliveredCount = 0;
        foreach (var reader in readers.Values)
        {
            if (reader.Enqueue(message))
            {
                deliveredCount++;
            }
        }

        return deliveredCount;
    }

    public bool TryTouch(DateTimeOffset activityAt)
    {
        lock (activityLock)
        {
            if (isExpiring)
            {
                return false;
            }

            lastActivityAt = activityAt;
            return true;
        }
    }

    public bool IsExpired(DateTimeOffset now, TimeSpan ttl)
    {
        return now - LastActivityAt >= ttl;
    }

    public bool TryBeginExpiration(DateTimeOffset now, TimeSpan ttl)
    {
        lock (activityLock)
        {
            if (isExpiring)
            {
                return true;
            }

            if (now - lastActivityAt < ttl)
            {
                return false;
            }

            isExpiring = true;
            return true;
        }
    }

    public void CompleteReaders()
    {
        foreach (var reader in readers.Values)
        {
            reader.Complete();
        }
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

    public bool Enqueue(FeedMessage message)
    {
        if (IsUnusable)
        {
            return false;
        }

        if (messages.Writer.TryWrite(message))
        {
            return true;
        }

        MarkUnusable();
        return false;
    }

    public void Complete()
    {
        messages.Writer.TryComplete();
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
