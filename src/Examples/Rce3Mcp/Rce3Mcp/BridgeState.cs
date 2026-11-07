namespace Rce3Mcp;

internal sealed record BridgeStatus(Guid ReaderId, string ConnectionState, DateTimeOffset? LastSuccessfulPollAt,
    DateTimeOffset? LastMessageAt, int HistoryCount, long EvictedCount, long ResetCount, string? LastError);

internal sealed class BridgeState(ReaderIdentity identity, MessageHistory history, TimeProvider timeProvider)
{
    private readonly object _gate = new();
    private string _state = "connecting";
    private DateTimeOffset? _lastPoll;
    private DateTimeOffset? _lastMessage;
    private long _resets;
    private string? _error;

    public bool CanSend { get { lock (_gate) return _state == "polling"; } }

    public void PollSucceeded(bool receivedMessage)
    {
        lock (_gate)
        {
            if (_state == "stopping") return;
            _state = "polling";
            _lastPoll = timeProvider.GetUtcNow();
            if (receivedMessage) _lastMessage = _lastPoll;
            _error = null;
        }
    }

    public void Set(string state, string? error = null)
    {
        lock (_gate)
        {
            _state = state;
            _error = error;
        }
    }

    public void ReaderReset()
    {
        lock (_gate) _resets++;
    }

    public BridgeStatus Snapshot()
    {
        lock (_gate)
        {
            var stats = history.Statistics();
            return new(identity.Id, _state, _lastPoll, _lastMessage, stats.Count, stats.EvictedCount, _resets, _error);
        }
    }
}
