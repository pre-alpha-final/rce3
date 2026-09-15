using System.Net;
using System.Text;

namespace Youtube.Tests;

public class FeedListenerTests
{
    private static readonly Uri FeedUri = new("https://example.test/7d3e84f2-21be-4d69-beb6-52c65f0d55a6");
    private static readonly Guid ReaderId = Guid.Parse("74766693-8f1a-49a9-beac-4b9c1a275bac");

    [Fact]
    public async Task PollsExistingReaderThenProcessesMessagesInOrder()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new RecordingHandler((requestNumber, _, token) => requestNumber switch
        {
            1 => Completed(Response(HttpStatusCode.OK, "youtube: back")),
            2 => Completed(Response(HttpStatusCode.OK, "youtube: forward6")),
            _ => Cancel(cancellation, token)
        });
        var commandHandler = new RecordingCommandHandler();
        using var client = CreateClient(handler);
        var output = new StringWriter();
        var listener = CreateListener(client, commandHandler, "raw-key", output: output);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listener.RunAsync(cancellation.Token));

        Assert.Equal(["youtube: back", "youtube: forward6"], commandHandler.Bodies);
        Assert.Equal($"/{FeedUri.Segments[^1]}{ReaderPathSuffix}", handler.Requests[0].Uri.AbsolutePath);
        Assert.Equal($"/{FeedUri.Segments[^1]}{ReaderPathSuffix}", handler.Requests[1].Uri.AbsolutePath);
        Assert.All(handler.Requests, request => Assert.Equal("raw-key", request.Authorization));
        Assert.Contains("Connecting", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Polling reader", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("youtube: back", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("raw-key", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoContentStartsAnotherPoll()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new RecordingHandler((requestNumber, _, token) => requestNumber switch
        {
            1 => Completed(Response(HttpStatusCode.NoContent)),
            _ => Cancel(cancellation, token)
        });
        using var client = CreateClient(handler);
        var listener = CreateListener(client, new RecordingCommandHandler());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listener.RunAsync(cancellation.Token));

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task GoneResetsReaderAndResumesPolling()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new RecordingHandler((requestNumber, _, token) => requestNumber switch
        {
            1 => Completed(Response(HttpStatusCode.Gone)),
            2 => Completed(Response(HttpStatusCode.Found)),
            _ => Cancel(cancellation, token)
        });
        using var client = CreateClient(handler);
        var error = new StringWriter();
        var listener = CreateListener(client, new RecordingCommandHandler(), error: error);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listener.RunAsync(cancellation.Token));

        Assert.DoesNotContain("/reset", handler.Requests[0].Uri.AbsolutePath);
        Assert.EndsWith("/reset", handler.Requests[1].Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("resetting", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OverflowResetRetriesFailuresThenResumesSameReader()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new RecordingHandler((requestNumber, _, token) => requestNumber switch
        {
            1 => Completed(Response(HttpStatusCode.Gone)),
            2 => Task.FromException<HttpResponseMessage>(new HttpRequestException("offline")),
            3 => Completed(Response(HttpStatusCode.ServiceUnavailable)),
            4 => Completed(Response(HttpStatusCode.Found)),
            _ => Cancel(cancellation, token)
        });
        using var client = CreateClient(handler);
        var listener = CreateListener(client, new RecordingCommandHandler());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listener.RunAsync(cancellation.Token));
        Assert.Equal(5, handler.Requests.Count);
        Assert.Equal(handler.Requests[0].Uri, handler.Requests[4].Uri);
        Assert.All(handler.Requests.Skip(1).Take(3), request =>
            Assert.Equal(handler.Requests[0].Uri.AbsoluteUri + "/reset", request.Uri.AbsoluteUri));
    }

    [Fact]
    public async Task TransientPollFailuresAreRetriedWithoutReset()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new RecordingHandler((requestNumber, _, token) => requestNumber switch
        {
            1 => Task.FromException<HttpResponseMessage>(new HttpRequestException("offline")),
            2 => Completed(Response(HttpStatusCode.RequestTimeout)),
            3 => Completed(Response(HttpStatusCode.TooManyRequests)),
            4 => Completed(Response(HttpStatusCode.InternalServerError)),
            _ => Cancel(cancellation, token)
        });
        using var client = CreateClient(handler);
        var listener = CreateListener(client, new RecordingCommandHandler());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listener.RunAsync(cancellation.Token));

        Assert.Equal(5, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.DoesNotContain("/reset", request.Uri.AbsolutePath));
    }

    [Fact]
    public async Task AuthorizationFailureIsPermanentAndDoesNotExposeKey()
    {
        var handler = new RecordingHandler((_, _, _) => Completed(Response(HttpStatusCode.Unauthorized)));
        using var client = CreateClient(handler);
        var error = new StringWriter();
        var output = new StringWriter();
        var listener = CreateListener(
            client,
            new RecordingCommandHandler(),
            "private-key",
            error,
            output);

        var exception = await Assert.ThrowsAsync<FeedProtocolException>(
            () => listener.RunAsync(CancellationToken.None));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Single(handler.Requests);
        Assert.DoesNotContain("private-key", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-key", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-key", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MessageBodyAndAuthorizationAreNotLogged()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new RecordingHandler((requestNumber, _, token) => requestNumber switch
        {
            1 => Completed(Response(HttpStatusCode.OK, "private ordinary message")),
            _ => Cancel(cancellation, token)
        });
        using var client = CreateClient(handler);
        var output = new StringWriter();
        var error = new StringWriter();
        var listener = CreateListener(
            client,
            new RecordingCommandHandler(),
            "private-key",
            error,
            output);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listener.RunAsync(cancellation.Token));

        Assert.DoesNotContain("private ordinary message", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private ordinary message", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-key", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-key", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationReachesActiveCommandHandler()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new RecordingHandler((requestNumber, _, _) => requestNumber switch
        {
            _ => Completed(Response(HttpStatusCode.OK, "youtube: playpause"))
        });
        var commandHandler = new BlockingCommandHandler();
        using var client = CreateClient(handler);
        var listener = CreateListener(client, commandHandler);

        var runTask = listener.RunAsync(cancellation.Token);
        await commandHandler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
        Assert.True(commandHandler.CancellationObserved);
    }

    private static string ReaderPathSuffix => $"/{ReaderId:D}";

    private static FeedListener CreateListener(
        HttpClient client,
        IYoutubeCommandHandler commandHandler,
        string? authorization = null,
        TextWriter? error = null,
        TextWriter? output = null)
    {
        return new FeedListener(
            client,
            new YoutubeOptions(FeedUri, authorization),
            ReaderId,
            commandHandler,
            TimeSpan.Zero,
            output ?? TextWriter.Null,
            error ?? TextWriter.Null);
    }

    private static HttpClient CreateClient(HttpMessageHandler handler)
    {
        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    private static HttpResponseMessage Response(HttpStatusCode statusCode, string? body = null)
    {
        var response = new HttpResponseMessage(statusCode);
        if (body is not null)
        {
            response.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        }

        return response;
    }

    private static Task<HttpResponseMessage> Completed(HttpResponseMessage response)
    {
        return Task.FromResult(response);
    }

    private static Task<HttpResponseMessage> Cancel(
        CancellationTokenSource cancellation,
        CancellationToken requestToken)
    {
        cancellation.Cancel();
        return Task.FromCanceled<HttpResponseMessage>(requestToken);
    }

    private sealed record RequestSnapshot(Uri Uri, string? Authorization);

    private sealed class RecordingHandler(
        Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory)
        : HttpMessageHandler
    {
        public List<RequestSnapshot> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            request.Headers.TryGetValues("Authorization", out var authorizationValues);
            Requests.Add(new RequestSnapshot(
                request.RequestUri!,
                authorizationValues?.SingleOrDefault()));
            return responseFactory(Requests.Count, request, cancellationToken);
        }
    }

    private sealed class RecordingCommandHandler : IYoutubeCommandHandler
    {
        public List<string> Bodies { get; } = [];

        public Task HandleAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
        {
            Bodies.Add(Encoding.UTF8.GetString(body.Span));
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingCommandHandler : IYoutubeCommandHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CancellationObserved { get; private set; }

        public async Task HandleAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
        {
            Started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }
        }
    }
}
