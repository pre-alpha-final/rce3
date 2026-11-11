using System.Net;
using Microsoft.AspNetCore.Http;

namespace WebServer.Tests;

public sealed class GatewayTests
{
    [Fact]
    public async Task LimitsPendingRequestsAndReleasesSlotsOnCancellation()
    {
        using var handler = new PendingPublish();
        using var http = new HttpClient(handler);
        var feed = new FeedClient(http, new(new Uri($"http://localhost/{Guid.NewGuid():D}"), null), Guid.NewGuid());
        var gateway = new GatewayRelay(feed);

        var unavailable = Request();
        await gateway.ForwardAsync(unavailable);
        Assert.Equal(503, unavailable.Response.StatusCode);

        gateway.SetReaderReady(true);
        using var abort = new CancellationTokenSource();
        var abandoned = Request();
        abandoned.RequestAborted = abort.Token;
        var canceledRequest = gateway.ForwardAsync(abandoned);
        var waiting = Enumerable.Range(0, 31).Select(_ => Request()).ToArray();
        var pending = waiting.Select(gateway.ForwardAsync).ToArray();
        var busy = Request();
        await gateway.ForwardAsync(busy);
        Assert.Equal(503, busy.Response.StatusCode);
        Assert.Equal(32, handler.Published);

        await abort.CancelAsync();
        await canceledRequest.WaitAsync(TimeSpan.FromSeconds(2));
        var replacement = Request();
        var replacementRequest = gateway.ForwardAsync(replacement);
        Assert.Equal(33, handler.Published);

        gateway.SetReaderReady(false);
        await Task.WhenAll(pending.Append(replacementRequest)).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.All(waiting.Append(replacement), context => Assert.Equal(502, context.Response.StatusCode));
    }

    private static DefaultHttpContext Request()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/";
        return context;
    }

    private sealed class PendingPublish : HttpMessageHandler
    {
        public int Published { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Published++;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new(HttpStatusCode.OK);
        }
    }
}
