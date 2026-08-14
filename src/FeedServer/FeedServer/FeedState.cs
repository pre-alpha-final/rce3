using System.Collections.Concurrent;

namespace FeedServer;

public sealed class FeedState
{
    private readonly ConcurrentDictionary<Guid, byte> readers = new();

    public FeedState(Guid id, FeedAccessMode mode, byte[]? protectedKeyHash, DateTimeOffset createdAt)
    {
        Id = id;
        Mode = mode;
        ProtectedKeyHash = protectedKeyHash;
        CreatedAt = createdAt;
        LastActivityAt = createdAt;
    }

    public Guid Id { get; }

    public FeedAccessMode Mode { get; }

    public byte[]? ProtectedKeyHash { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset LastActivityAt { get; private set; }

    public int ReaderCount => readers.Count;

    public void EnsureReader(Guid readerId)
    {
        readers.TryAdd(readerId, 0);
    }

    public void Touch(DateTimeOffset activityAt)
    {
        LastActivityAt = activityAt;
    }
}
