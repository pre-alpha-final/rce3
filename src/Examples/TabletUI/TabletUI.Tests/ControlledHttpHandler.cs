using System.Net;
using System.Threading.Channels;
using TabletUI.Services;

namespace TabletUI.Tests;

// Every request waits for a response or cancellation. Timeouts are failure watchdogs,
// never synchronization: tests advance only on request, response, and cancellation signals.
internal sealed class ControlledHttpHandler : HttpMessageHandler
{
    private readonly Channel<PendingRequest> _requests = Channel.CreateUnbounded<PendingRequest>();
    private int _requestCount;
    private int _activeGets;
    private int _maxActiveGets;

    public int RequestCount => Volatile.Read(ref _requestCount);
    public int ActiveGets => Volatile.Read(ref _activeGets);
    public int MaxActiveGets => Volatile.Read(ref _maxActiveGets);

    public async Task<PendingRequest> NextAsync() =>
        await _requests.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var pending = new PendingRequest(request.Method, request.RequestUri!,
            request.Headers.TryGetValues("Authorization", out var auth) ? auth.ToArray() : [],
            request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(token),
            request.Content?.Headers.TryGetValues("Content-Type", out var types) == true ? types.ToArray() : []);
        Interlocked.Increment(ref _requestCount);
        if (request.Method == HttpMethod.Get)
        {
            var active = Interlocked.Increment(ref _activeGets);
            int previous;
            do
            {
                previous = Volatile.Read(ref _maxActiveGets);
                if (active <= previous) { break; }
            } while (Interlocked.CompareExchange(ref _maxActiveGets, active, previous) != previous);
        }

        using var registration = token.Register(() =>
        {
            pending.Canceled.TrySetResult();
            pending.Response.TrySetCanceled(token);
        });
        _requests.Writer.TryWrite(pending);
        try
        {
            return await pending.Response.Task;
        }
        finally
        {
            if (request.Method == HttpMethod.Get) { Interlocked.Decrement(ref _activeGets); }
            pending.Finished.TrySetResult();
        }
    }
}

internal sealed record PendingRequest(
    HttpMethod Method, Uri Uri, string[] Authorization, byte[] Body, string[] ContentTypes)
{
    public TaskCompletionSource<HttpResponseMessage> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Reply(HttpStatusCode status, byte[]? body = null, string? contentType = null)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(body ?? []) };
        if (contentType is not null) { response.Content.Headers.TryAddWithoutValidation("Content-Type", contentType); }
        Assert.True(Response.TrySetResult(response), "Request already completed.");
    }

    public void Fail(Exception error) => Assert.True(Response.TrySetException(error));

    public async Task WaitForCancellationAsync()
    {
        await Canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}

internal sealed class ConnectionHarness : IAsyncDisposable
{
    public const string Feed = "https://example.test/01234567-89ab-cdef-0123-456789abcdef";
    public ControlledHttpHandler Handler { get; } = new();
    public HttpClient Client { get; }
    public FeedConnection Connection { get; }

    public ConnectionHarness()
    {
        Client = new HttpClient(Handler) { Timeout = Timeout.InfiniteTimeSpan };
        Connection = new FeedConnection(Client, TimeProvider.System, retryDelay: TimeSpan.Zero);
    }

    public Task ConnectAsync(string auth = "") => Connection.ConnectAsync(new(1, Feed, auth));

    public async Task<PendingRequest> ReadyAsync(string auth = "")
    {
        await ConnectAsync(auth);
        var reset = await Handler.NextAsync();
        Assert.EndsWith("/reset", reset.Uri.AbsolutePath);
        reset.Reply(HttpStatusCode.NoContent);
        var poll = await Handler.NextAsync();
        Assert.True(Connection.CanSend);
        return poll;
    }

    public async ValueTask DisposeAsync()
    {
        await Connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Client.Dispose();
    }
}
