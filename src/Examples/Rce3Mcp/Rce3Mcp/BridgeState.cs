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
                    throw new BridgeException("FetchProxy connection is unavailable. Check the configured connection and restart if necessary.");
                if (_state == "polling") return _readerLifetime.Token;
                changed = _changed.Task;
            }
            await changed.WaitAsync(cancellationToken);
        }
    }

    private void SignalChanged()
    {
        var changed = _changed;
        _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        changed.TrySetResult();
    }

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
            SignalChanged();
        }
    }

    public void Set(string state, string? error = null)
    {
        CancellationTokenSource? interrupted = null;
        lock (_gate)
        {
            _state = state;
            _error = error;
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

    public void ReaderReset()
    {
        lock (_gate) _resets++;
    }

    public void Dispose() => _readerLifetime.Dispose();

    public BridgeStatus Snapshot()
    {
        lock (_gate)
        {
            var stats = history.Statistics();
            return new(identity.Id, _state, _lastPoll, _lastMessage, stats.Count, stats.EvictedCount, _resets, _error);
        }
    }
}
