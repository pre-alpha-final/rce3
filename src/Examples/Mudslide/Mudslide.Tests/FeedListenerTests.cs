using System.Net;
using System.Text;

namespace Mudslide.Tests;

public class FeedListenerTests
{
    private static readonly Uri FeedUri = new("https://example.test/7d3e84f2-21be-4d69-beb6-52c65f0d55a6");
    private static readonly Guid ReaderId = Guid.Parse("74766693-8f1a-49a9-beac-4b9c1a275bac");

    [Fact]
    public async Task ResetsSendsConnectivityTestThenProcessesNotificationsInOrder()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new RecordingHandler((requestNumber, _, token) => requestNumber switch
        {
            1 => Completed(Response(HttpStatusCode.Found)),
            2 => Completed(Response(HttpStatusCode.OK, "notify: first")),
            3 => Completed(Response(HttpStatusCode.OK, "notify: second\nline")),
            _ => Cancel(cancellation, token)
        });
        var sender = new RecordingNotificationSender(new CommandExecutionResult(true, 0));
        using var client = CreateClient(handler);
        var output = new StringWriter();
        var listener = CreateListener(client, sender, "raw-key", output: output);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listener.RunAsync(cancellation.Token));

        Assert.Equal(["connectivity test", "first", "second\nline"], sender.Notifications);
        Assert.Equal($"/{FeedUri.Segments[^1]}/{ReaderId:D}/reset", handler.Requests[0].Uri.AbsolutePath);
        Assert.Equal($"/{FeedUri.Segments[^1]}/{ReaderId:D}", handler.Requests[1].Uri.AbsolutePath);
        Assert.All(handler.Requests, request => Assert.Equal("raw-key", request.Authorization));
        Assert.Contains("Connecting", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Reader ready", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(3, output.ToString().Split("Notification sent.").Length - 1);
        Assert.DoesNotContain("raw-key", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("second\nline", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoContentStartsAnotherPoll()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new RecordingHandler((requestNumber, _, token) => requestNumber switch
        {
            1 => Completed(Response(HttpStatusCode.Found)),
            2 => Completed(Response(HttpStatusCode.NoContent)),
            _ => Cancel(cancellation, token)
        });
        using var client = CreateClient(handler);
        var output = new StringWriter();
        var listener = CreateListener(
            client,
            new RecordingNotificationSender(new CommandExecutionResult(true, 0)),
            output: output);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listener.RunAsync(cancellation.Token));

        Assert.Equal(3, handler.Requests.Count);
        Assert.DoesNotContain("no message", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonNotificationIsReportedWithoutLoggingBody()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new RecordingHandler((requestNumber, _, token) => requestNumber switch
        {
            1 => Completed(Response(HttpStatusCode.Found)),
            2 => Completed(Response(HttpStatusCode.OK, "private ordinary message")),
            _ => Cancel(cancellation, token)
        });
        var sender = new RecordingNotificationSender(new CommandExecutionResult(true, 0));
        using var client = CreateClient(handler);
        var output = new StringWriter();
        var listener = CreateListener(client, sender, output: output);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listener.RunAsync(cancellation.Token));

        Assert.Equal(["connectivity test"], sender.Notifications);
        Assert.Contains("ignored", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private ordinary message", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GoneResetsReaderAndResumesPolling()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new RecordingHandler((requestNumber, _, token) => requestNumber switch
        {
            1 => Completed(Response(HttpStatusCode.Found)),
            2 => Completed(Response(HttpStatusCode.Gone)),
            3 => Completed(Response(HttpStatusCode.Found)),
            _ => Cancel(cancellation, token)
        });
        using var client = CreateClient(handler);
        var error = new StringWriter();
        var listener = CreateListener(
            client,
            new RecordingNotificationSender(new CommandExecutionResult(true, 0)),
            error: error);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listener.RunAsync(cancellation.Token));

        Assert.EndsWith("/reset", handler.Requests[0].Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.EndsWith("/reset", handler.Requests[2].Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("resetting", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TransientResetAndInternalServerFailuresAreRetried()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new RecordingHandler((requestNumber, _, token) => requestNumber switch
        {
            1 => Task.FromException<HttpResponseMessage>(new HttpRequestException("offline")),
            2 => Completed(Response(HttpStatusCode.Found)),
            3 => Completed(Response(HttpStatusCode.InternalServerError)),
            _ => Cancel(cancellation, token)
        });
        using var client = CreateClient(handler);
        var listener = CreateListener(client, new RecordingNotificationSender(new CommandExecutionResult(true, 0)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listener.RunAsync(cancellation.Token));

        Assert.Equal(4, handler.Requests.Count);
        Assert.False(handler.Requests[3].Uri.AbsolutePath.EndsWith("/reset", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AuthorizationFailureIsPermanent()
    {
        var handler = new RecordingHandler((_, _, _) => Completed(Response(HttpStatusCode.Unauthorized)));
        using var client = CreateClient(handler);
        var listener = CreateListener(client, new RecordingNotificationSender(new CommandExecutionResult(true, 0)));

        var exception = await Assert.ThrowsAsync<FeedProtocolException>(
            () => listener.RunAsync(CancellationToken.None));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task MudslideFailureIsLoggedWithoutMessageOrKeyAndPollingContinues()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new RecordingHandler((requestNumber, _, token) => requestNumber switch
        {
            1 => Completed(Response(HttpStatusCode.Found)),
            2 => Completed(Response(HttpStatusCode.OK, "notify: private notification")),
            3 => Completed(Response(HttpStatusCode.NoContent)),
            _ => Cancel(cancellation, token)
        });
        var sender = new RecordingNotificationSender(new CommandExecutionResult(true, 7));
        using var client = CreateClient(handler);
        var output = new StringWriter();
        var error = new StringWriter();
        var listener = CreateListener(client, sender, "private-key", error, output);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listener.RunAsync(cancellation.Token));

        Assert.Equal(["connectivity test", "private notification"], sender.Notifications);
        Assert.Contains("code 7", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private notification", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-key", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("invoking Mudslide", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Notification sent", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private notification", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-key", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task CancellationReachesActiveNotificationSender()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new RecordingHandler((requestNumber, _, _) => requestNumber switch
        {
            1 => Completed(Response(HttpStatusCode.Found)),
            _ => Completed(Response(HttpStatusCode.OK, "notify: wait"))
        });
        var sender = new BlockingNotificationSender();
        using var client = CreateClient(handler);
        var listener = CreateListener(client, sender);

        var runTask = listener.RunAsync(cancellation.Token);
        await sender.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
        Assert.True(sender.CancellationObserved);
    }

    private static FeedListener CreateListener(
        HttpClient client,
        INotificationSender sender,
        string? authorization = null,
        TextWriter? error = null,
        TextWriter? output = null)
    {
        return new FeedListener(
            client,
            new MudslideOptions(FeedUri, authorization),
            ReaderId,
            sender,
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

    private sealed class RecordingNotificationSender(CommandExecutionResult result) : INotificationSender
    {
        public List<string> Notifications { get; } = [];

        public Task<CommandExecutionResult> SendAsync(
            string notification,
            CancellationToken cancellationToken)
        {
            Notifications.Add(notification);
            return Task.FromResult(result);
        }
    }

    private sealed class BlockingNotificationSender : INotificationSender
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CancellationObserved { get; private set; }

        public async Task<CommandExecutionResult> SendAsync(
            string notification,
            CancellationToken cancellationToken)
        {
            Started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new CommandExecutionResult(true, 0);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }
        }
    }
}
