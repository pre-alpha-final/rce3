using System.Threading.Channels;

namespace FeedServer;

public sealed class FeedState
{
    private readonly Dictionary<Guid, FeedReaderState> readers = new();
    private readonly Lock stateLock = new();
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
            lock (stateLock)
            {
                return lastActivityAt;
            }
        }
    }

    public int ReaderCount
    {
        get
        {
            lock (stateLock)
            {
                return readers.Count;
            }
        }
    }

    public FeedReaderState EnsureReader(Guid readerId)
    {
        lock (stateLock)
        {
            return EnsureReaderLocked(readerId);
        }
    }

    internal void ResetReader(Guid readerId)
    {
        lock (stateLock)
        {
            EnsureReaderLocked(readerId).Reset();
        }
    }

    private FeedReaderState EnsureReaderLocked(Guid readerId)
    {
        if (!readers.TryGetValue(readerId, out var reader))
        {
            reader = new FeedReaderState(maxQueuedMessagesPerReader);
            readers.Add(readerId, reader);
        }

        return reader;
    }

    public int Publish(FeedMessage message)
    {
        lock (stateLock)
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
    }

    internal int KillReaderQueues()
    {
        lock (stateLock)
        {
            if (isExpiring)
            {
                return 0;
            }

            var count = readers.Count;
            foreach (var reader in readers.Values)
            {
                reader.Retire();
            }

            readers.Clear();
            return count;
        }
    }

    public bool TryTouch(DateTimeOffset activityAt)
    {
        lock (stateLock)
        {
            if (isExpiring)
            {
                return false;
            }

            if (activityAt > lastActivityAt)
            {
                lastActivityAt = activityAt;
            }

            return true;
        }
    }

    public bool IsExpired(DateTimeOffset now, TimeSpan ttl)
    {
        return now - LastActivityAt >= ttl;
    }

    public bool TryBeginExpiration(DateTimeOffset now, TimeSpan ttl)
    {
        lock (stateLock)
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
        lock (stateLock)
        {
            foreach (var reader in readers.Values)
            {
                reader.Complete();
            }
        }
    }
}

public sealed record FeedMessage(byte[] Body, string? ContentType)
{
    // A value means Body holds the gzip representation.
    internal long? GzipDecodedLength { get; init; }
}

public sealed class FeedReaderState
{
    private readonly Lock stateLock = new();
    private readonly int maxQueuedMessages;
    private Channel<FeedMessage> messages;
    private int resetGeneration;
    private volatile ReaderStatus status;

    private enum ReaderStatus
    {
        Active,
        Unusable,
        Retired
    }

    public FeedReaderState(int maxQueuedMessages)
    {
        this.maxQueuedMessages = maxQueuedMessages;
        messages = CreateChannel(maxQueuedMessages);
    }

    public bool IsUnusable => status == ReaderStatus.Unusable;

    public bool Enqueue(FeedMessage message)
    {
        lock (stateLock)
        {
            if (status != ReaderStatus.Active)
            {
                return false;
            }

            if (messages.Writer.TryWrite(message))
            {
                return true;
            }

            status = ReaderStatus.Unusable;
            messages.Writer.TryComplete();
            return false;
        }
    }

    public void Complete()
    {
        lock (stateLock)
        {
            messages.Writer.TryComplete();
        }
    }

    internal void Retire()
    {
        lock (stateLock)
        {
            status = ReaderStatus.Retired;
            messages.Writer.TryComplete();
            while (messages.Reader.TryRead(out _))
            {
                // Release queued messages even if a pending poll still holds this reader.
            }
        }
    }

    public void Reset()
    {
        Channel<FeedMessage> previousMessages;
        lock (stateLock)
        {
            if (status == ReaderStatus.Retired)
            {
                return;
            }

            previousMessages = messages;
            messages = CreateChannel(maxQueuedMessages);
            status = ReaderStatus.Active;
            resetGeneration++;
        }

        previousMessages.Writer.TryComplete();
    }

    public async ValueTask<FeedMessage?> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        Channel<FeedMessage> currentMessages;
        int currentResetGeneration;
        lock (stateLock)
        {
            if (status == ReaderStatus.Retired)
            {
                return null;
            }

            currentMessages = messages;
            currentResetGeneration = resetGeneration;
        }

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            var message = await currentMessages.Reader.ReadAsync(linkedCts.Token);
            return currentResetGeneration == Volatile.Read(ref resetGeneration) && status != ReaderStatus.Retired
                ? message
                : null;
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

    private static Channel<FeedMessage> CreateChannel(int maxQueuedMessages)
    {
        return Channel.CreateBounded<FeedMessage>(
            new BoundedChannelOptions(maxQueuedMessages)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false
            });
    }
}
