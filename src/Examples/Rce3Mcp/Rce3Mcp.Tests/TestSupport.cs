using System.Net;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Rce3Mcp.Tests;

internal sealed record PendingRequest(Uri Uri, HttpMethod Method, string? Authorization, byte[]? Body,
    string? ContentType, string[] ContentEncodings)
{
    public TaskCompletionSource<HttpResponseMessage> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Respond(HttpStatusCode status, HttpContent? content = null) => Reply.SetResult(new(status) { Content = content });
}

internal sealed class ControlledHandler : HttpMessageHandler
{
    private readonly Channel<PendingRequest> _requests = Channel.CreateUnbounded<PendingRequest>();
    public int RequestCount;
    public async Task<PendingRequest> NextAsync() => await _requests.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref RequestCount);
        var pending = new PendingRequest(request.RequestUri!, request.Method,
            request.Headers.TryGetValues("Authorization", out var values) ? values.Single() : null,
            request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken),
            request.Content?.Headers.NonValidated.TryGetValues("Content-Type", out var types) == true ? types.Single() : null,
            request.Content?.Headers.ContentEncoding.ToArray() ?? []);
        await _requests.Writer.WriteAsync(pending, cancellationToken);
        try { return await pending.Reply.Task.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) { pending.Cancelled.TrySetResult(); throw; }
    }
}

internal sealed class RetryTimeProvider : TimeProvider
{
    private readonly Channel<Action> _timers = Channel.CreateUnbounded<Action>();
    public override DateTimeOffset GetUtcNow() => new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Assert.Equal(TimeSpan.FromSeconds(1), dueTime);
        var timer = new ManualTimer(() => callback(state));
        _timers.Writer.TryWrite(timer.Fire);
        return timer;
    }
    public async Task ReleaseRetryAsync()
    {
        var fire = await _timers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        fire();
    }
    private sealed class ManualTimer(Action callback) : ITimer
    {
        private bool _disposed;
        public void Fire() { if (!_disposed) callback(); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
        public void Dispose() => _disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

internal sealed class BridgeHarness : IAsyncDisposable
{
    public ControlledHandler Handler { get; } = new();
    public RetryTimeProvider Time { get; } = new();
    public ReaderIdentity Identity { get; } = new(Guid.NewGuid());
    public McpOptions Options { get; }
    public MessageHistory History { get; }
    public BridgeState State { get; }
    public FeedBridge Bridge { get; }
    public FeedTools Tools { get; }
    private readonly HttpClient _client;
    public BridgeHarness(string? authorization = null, McpOptions? options = null)
    {
        Options = options ?? new();
        History = new(Options, Time);
        State = new(Identity, History, Time);
        _client = new(Handler) { Timeout = Timeout.InfiniteTimeSpan };
        Bridge = new(_client, new(new Uri("http://feed.test/00000000-0000-0000-0000-000000000000"), authorization),
            Identity, Options, History, State, Time, NullLogger<FeedBridge>.Instance);
        Tools = new(Bridge, History, State, Options);
    }
    public async Task<PendingRequest> StartAsync()
    {
        await Bridge.StartAsync(CancellationToken.None);
        return await Handler.NextAsync();
    }
    public async Task<PendingRequest> ReadyAsync()
    {
        var first = await StartAsync();
        first.Respond(HttpStatusCode.NoContent);
        return await Handler.NextAsync();
    }
    public async ValueTask DisposeAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Bridge.StopAsync(deadline.Token);
        Bridge.Dispose();
        _client.Dispose();
    }
}

internal sealed class UnknownLengthContent(byte[] bytes) : HttpContent
{
    protected override bool TryComputeLength(out long length) { length = 0; return false; }
    protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => stream.WriteAsync(bytes).AsTask();
}
