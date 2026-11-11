using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;

namespace WebServer;

internal sealed class GatewayRelay(FeedClient feed)
{
    private const int MaxPending = 32;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, TaskCompletionSource<RelayReply>> _pending = new();
    private CancellationTokenSource? _readerConnection;

    public async Task ForwardAsync(HttpContext context)
    {
        var head = HttpMethods.IsHead(context.Request.Method);
        if (!head && !HttpMethods.IsGet(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            context.Response.Headers.Allow = "GET, HEAD";
            return;
        }

        var id = Guid.NewGuid();
        var reply = new TaskCompletionSource<RelayReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken readerInterrupted;
        CancellationTokenSource requestDeadline;
        lock (_gate)
        {
            if (_readerConnection is null || _pending.Count >= MaxPending)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            readerInterrupted = _readerConnection.Token;
            requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, readerInterrupted);
            _pending.Add(id, reply);
        }

        using var deadline = requestDeadline;
        deadline.CancelAfter(FeedClient.WriteTimeout);
        RelayReply response;
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            var request = new RelayRequest(id, context.Request.Path.Value ?? "/");
            await feed.PublishAsync(RelayProtocol.RequestType, RelayProtocol.Write(request), deadline.Token);
            response = await reply.Task.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return;
        }
        catch (OperationCanceledException) when (readerInterrupted.IsCancellationRequested)
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
            return;
        }
        catch (Exception exception) when (exception is FeedStatusException || FeedClient.IsTransportFailure(exception))
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }
        finally
        {
            lock (_gate)
                _pending.Remove(id);
        }

        context.Response.StatusCode = response.Status;
        context.Response.ContentType = response.ContentType;
        context.Response.ContentLength = response.Body.Length;
        if (!head && response.Body.Length > 0)
            await context.Response.Body.WriteAsync(response.Body, context.RequestAborted);
    }

    public Task ReceiveReplyAsync(byte[] message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reply = RelayProtocol.Read<RelayReply>(message);
        if (reply?.Body is null || reply.Body.Length > RelayProtocol.MaxAssetBytes || !IsValidReply(reply))
            return Task.CompletedTask;

        lock (_gate)
        {
            if (_pending.TryGetValue(reply.Id, out var pending))
                pending.TrySetResult(reply);
        }

        return Task.CompletedTask;
    }

    public void SetReaderReady(bool ready)
    {
        CancellationTokenSource? interrupted;
        lock (_gate)
        {
            if (ready)
            {
                _readerConnection ??= new CancellationTokenSource();
                return;
            }

            interrupted = _readerConnection;
            _readerConnection = null;
        }

        interrupted?.Cancel();
        interrupted?.Dispose();
    }

    private static bool IsValidReply(RelayReply reply) => reply.Status switch
    {
        StatusCodes.Status200OK => MediaTypeHeaderValue.TryParse(reply.ContentType, out _),
        StatusCodes.Status404NotFound or StatusCodes.Status502BadGateway => reply.ContentType is null && reply.Body.Length == 0,
        _ => false
    };
}
