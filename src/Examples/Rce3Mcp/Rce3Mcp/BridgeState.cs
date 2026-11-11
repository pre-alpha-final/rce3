namespace Rce3Mcp;

internal sealed record BridgeStatus(Guid ReaderId, string ConnectionState, DateTimeOffset? LastSuccessfulPollAt,
    DateTimeOffset? LastMessageAt, int HistoryCount, long EvictedCount, long ResetCount, string? LastError);

internal sealed class BridgeState(ReaderIdentity identity, MessageHistory history, TimeProvider timeProvider) : IDisposable
{
    private readonly object _gate = new();
    private string _state = "connecting";
    private DateTimeOffset? _lastPoll;
    private DateTimeOffset? _lastMessage;
    private long _resets;
    private string? _error;
    private CancellationTokenSource _readerLifetime = new();
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<CancellationToken> WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (_state is "faulted" or "stopping")
                    throw new BridgeException("Feed reader is unavailable. Check the feed connection and restart if necessary.");
                if (_state == "polling") return _readerLifetime.Token;
                changed = _changed.Task;
            }
            await changed.WaitAsync(cancellationToken);
        }
    }

    public bool CanSend { get { lock (_gate) return _state == "polling"; } }

    public void PollSucceeded(bool receivedMessage)
    {
        lock (_gate)
        {
            if (_state == "stopping") return;
            if (_state != "polling")
            {
                _state = "polling";
                SignalChanged();
            }
            _lastPoll = timeProvider.GetUtcNow();
            if (receivedMessage) _lastMessage = _lastPoll;
            _error = null;
        }
    }

    public void Set(string state, string? error = null)
    {
        CancellationTokenSource? interrupted = null;
        lock (_gate)
        {
            if (_state == "stopping") return;
            _error = error;
            if (_state == state) return;
            _state = state;
            if (state is "resetting" or "faulted" or "stopping")
            {
                interrupted = _readerLifetime;
                _readerLifetime = new();
            }
            SignalChanged();
        }
        interrupted?.Cancel();
        interrupted?.Dispose();
    }

    private void SignalChanged()
    {
        var changed = _changed;
        _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        changed.TrySetResult();
    }

    public void Dispose() => _readerLifetime.Dispose();

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
