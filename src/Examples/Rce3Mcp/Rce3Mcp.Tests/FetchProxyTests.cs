using System.Net;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Rce3Mcp.Tests;

public sealed class FetchProxyTests
{
    private const string Target = "https://target.example/api/status?x=1";
    internal static string HttpReply(string requested = Target, int status = 200, string body = "response body") =>
        $"fetchproxyresponse: {requested}\n" + JsonSerializer.Serialize(new
        {
            url = "https://target.example/final", status, statusText = "Status text", ok = status < 400,
            redirected = true, type = "basic", headers = new Dictionary<string, string> { ["content-type"] = "text/custom" }, body
        });
    private static string FetchError(string name = "TypeError") =>
        $"fetchproxyresponse: {Target}\n" + JsonSerializer.Serialize(new { error = new { name, message = "sensitive browser error" } });
    private static string ErrorText(CallToolResult result)
    {
        Assert.True(result.IsError);
        return Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
    }
    private static async Task<PendingRequest> DeliverAsync(BridgeHarness harness, PendingRequest poll, string text)
    {
        poll.Respond(HttpStatusCode.OK, new StringContent(text, Encoding.UTF8, "text/plain"));
        return await harness.Handler.NextAsync();
    }
    private static void Accept(PendingRequest post) => post.Respond(HttpStatusCode.OK, new StringContent("Message distributed to 3 reader(s)."));

    [Theory]
    [InlineData(200)]
    [InlineData(204)]
    [InlineData(401)]
    [InlineData(404)]
    [InlineData(500)]
    public async Task ErrorsAndMalformedRepliesDoNotBeatFirstHttpResponse(int status)
    {
        await using var harness = new BridgeHarness(options: new() { HistoryCapacity = 1 });
        var poll = await harness.ReadyAsync();
        var fetching = harness.Tools.FetchProxy("  " + Target + "  ", CancellationToken.None);
        var post = await harness.Handler.NextAsync();
        Assert.Equal(HttpMethod.Post, post.Method);
        Assert.Equal("fetchproxy: " + Target, Encoding.UTF8.GetString(post.Body!));
        Assert.Equal("text/plain; charset=utf-8", post.ContentType);
        poll = await DeliverAsync(harness, poll, FetchError());
        poll = await DeliverAsync(harness, poll, $"fetchproxyresponse: {Target}\nnot JSON");
        poll = await DeliverAsync(harness, poll, $"fetchproxyresponse: {Target}\n{{\"status\":200}}");
        poll = await DeliverAsync(harness, poll, HttpReply(Target + "extra", body: "wrong URL"));
        poll = await DeliverAsync(harness, poll, "fetchproxy: " + Target);
        Assert.False(fetching.IsCompleted);
        // A response can arrive before the publication's acknowledgement, and be evicted before it finishes.
        poll = await DeliverAsync(harness, poll, HttpReply(status: status, body: "first\r\n世界"));
        poll = await DeliverAsync(harness, poll, HttpReply(body: "second"));
        poll = await DeliverAsync(harness, poll, "unrelated history evicts the response");
        Assert.False(fetching.IsCompleted);
        Accept(post);
        var result = await fetching.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(result.IsError);
        var response = result.StructuredContent!.Value;
        Assert.Equal(status, response.GetProperty("status").GetInt32());
        Assert.Equal("https://target.example/final", response.GetProperty("url").GetString());
        Assert.Equal("first\r\n世界", response.GetProperty("body").GetString());
        Assert.Equal("text/custom", response.GetProperty("headers").GetProperty("content-type").GetString());
        Assert.True(response.GetProperty("redirected").GetBoolean());
        using var text = JsonDocument.Parse(Assert.IsType<TextContentBlock>(result.Content[0]).Text);
        Assert.True(JsonElement.DeepEquals(response, text.RootElement));
        Assert.Single(harness.History.Read().Messages);
        Assert.Equal("unrelated history evicts the response", harness.History.Read().Messages[0].Body);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeadlineWaitsDespiteFailuresAndReportsBoundedSanitizedCounts(bool errors)
    {
        await using var harness = new BridgeHarness();
        var poll = await harness.ReadyAsync();
        var fetching = harness.Tools.FetchProxy(Target, CancellationToken.None);
        Accept(await harness.Handler.NextAsync());
        if (errors)
        {
            poll = await DeliverAsync(harness, poll, FetchError());
            poll = await DeliverAsync(harness, poll, FetchError("TimeoutError"));
            poll = await DeliverAsync(harness, poll, $"fetchproxyresponse: {Target}\n{{}}");
        }
        Assert.False(fetching.IsCompleted);
        await harness.Time.ReleaseDeadlineAsync();
        var error = ErrorText(await fetching.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("60-second deadline", error);
        Assert.Contains(errors ? "2 browser fetch failure(s), including 1 timeout(s), and 1 malformed" : "0 browser fetch failure(s)", error);
        Assert.DoesNotContain("sensitive", error);
        Assert.Contains("not retried", error);
        Assert.Equal("polling", harness.State.Snapshot().ConnectionState);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task DeadlineCoversReadinessQueueingAndPublication(int stage)
    {
        await using var harness = new BridgeHarness();
        await harness.StartAsync();
        using var firstCancellation = new CancellationTokenSource();
        Task<CallToolResult>? first = stage == 1 ? harness.Tools.FetchProxy(Target, firstCancellation.Token) : null;
        var fetching = harness.Tools.FetchProxy(Target, CancellationToken.None);
        if (stage == 1) await harness.Time.NextDeadlineAsync(); // Leave the first deadline unfired so the second call stays queued.
        if (stage == 2)
        {
            harness.State.PollSucceeded(false);
            await harness.Handler.NextAsync(); // Keep publication pending.
        }
        await harness.Time.ReleaseDeadlineAsync();
        Assert.Contains("60-second deadline", ErrorText(await fetching.WaitAsync(TimeSpan.FromSeconds(10))));
        if (first is not null)
        {
            Assert.False(first.IsCompleted);
            firstCancellation.Cancel();
            Assert.True((await first).IsError);
        }
        Assert.Equal(stage == 2 ? 2 : 1, harness.Handler.RequestCount);
    }

    [Fact]
    public async Task QueuedCallsSerializeAndIgnoreAlreadyReceivedReplies()
    {
        await using var harness = new BridgeHarness();
        var poll = await harness.ReadyAsync();
        var first = harness.Tools.FetchProxy(Target, CancellationToken.None);
        var firstPost = await harness.Handler.NextAsync();
        var second = harness.Tools.FetchProxy(Target, CancellationToken.None);
        Assert.Equal(3, harness.Handler.RequestCount);
        Accept(firstPost);
        poll.Respond(HttpStatusCode.OK, new StringContent(HttpReply(body: "first")));
        Assert.False((await first).IsError);
        var a = await harness.Handler.NextAsync();
        var b = await harness.Handler.NextAsync();
        var secondPost = a.Method == HttpMethod.Post ? a : b;
        poll = a.Method == HttpMethod.Get ? a : b;
        Accept(secondPost);
        Assert.False(second.IsCompleted);
        await DeliverAsync(harness, poll, HttpReply(body: "second"));
        var result = await second;
        Assert.Equal("second", result.StructuredContent!.Value.GetProperty("body").GetString());
    }

    [Fact]
    public async Task WaitsForStartupRegistrationWithoutStatusCalls()
    {
        await using var harness = new BridgeHarness();
        var poll = await harness.StartAsync();
        var fetching = harness.Tools.FetchProxy(Target, CancellationToken.None);
        Assert.Equal(1, harness.Handler.RequestCount);
        poll.Respond(HttpStatusCode.NoContent);
        var a = await harness.Handler.NextAsync();
        var b = await harness.Handler.NextAsync();
        var post = a.Method == HttpMethod.Post ? a : b;
        poll = a.Method == HttpMethod.Get ? a : b;
        Accept(post);
        await DeliverAsync(harness, poll, HttpReply());
        Assert.False((await fetching).IsError);
    }

    [Fact]
    public async Task StartupBacklogCannotSatisfyANewRequest()
    {
        await using var harness = new BridgeHarness();
        var poll = await harness.StartAsync();
        var fetching = harness.Tools.FetchProxy(Target, CancellationToken.None);
        poll.Respond(HttpStatusCode.OK, new StringContent(HttpReply(body: "startup backlog")));
        var a = await harness.Handler.NextAsync();
        var b = await harness.Handler.NextAsync();
        var post = a.Method == HttpMethod.Post ? a : b;
        poll = a.Method == HttpMethod.Get ? a : b;
        Accept(post);
        Assert.False(fetching.IsCompleted);
        await DeliverAsync(harness, poll, HttpReply(body: "fresh response"));
        Assert.Equal("fresh response", (await fetching).StructuredContent!.Value.GetProperty("body").GetString());
    }

    [Theory]
    [InlineData("status", "0")]
    [InlineData("status", "600")]
    [InlineData("status", "\"200\"")]
    [InlineData("body", "null")]
    [InlineData("ok", "1")]
    [InlineData("redirected", "null")]
    [InlineData("headers", "{\"x\":42}")]
    public async Task MalformedResponseFieldsAreIgnoredWithoutFaultingListener(string field, string value)
    {
        await using var harness = new BridgeHarness();
        var poll = await harness.ReadyAsync();
        var fetching = harness.Tools.FetchProxy(Target, CancellationToken.None);
        Accept(await harness.Handler.NextAsync());
        var prefix = $"fetchproxyresponse: {Target}\n";
        var json = System.Text.Json.Nodes.JsonNode.Parse(HttpReply()[prefix.Length..])!;
        json[field] = System.Text.Json.Nodes.JsonNode.Parse(value);
        poll = await DeliverAsync(harness, poll, prefix + json.ToJsonString());
        Assert.False(fetching.IsCompleted);
        await DeliverAsync(harness, poll, HttpReply());
        Assert.False((await fetching).IsError);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("/relative")]
    [InlineData("//target.example/api")]
    [InlineData("ftp://target.example/api")]
    [InlineData("https://target.example/\napi")]
    [InlineData("https://target.example/\r")]
    public async Task InvalidInputsDoNotSend(string? url)
    {
        await using var harness = new BridgeHarness();
        Assert.Contains("HTTP(S)", ErrorText(await harness.Tools.FetchProxy(url!, CancellationToken.None)));
        Assert.Equal(0, harness.Handler.RequestCount);
    }

    [Fact]
    public async Task InvalidUnicodeAndEncodedCommandSizeAreRejected()
    {
        await using var harness = new BridgeHarness(options: new() { MaxMessageSizeBytes = 45 });
        Assert.Contains("Unicode", ErrorText(await harness.Tools.FetchProxy("https://x/" + '\ud800', CancellationToken.None)));
        Assert.Contains("limit", ErrorText(await harness.Tools.FetchProxy("https://x/" + new string('世', 10), CancellationToken.None)));
        Assert.Equal(0, harness.Handler.RequestCount);
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("readiness")]
    [InlineData("publication")]
    [InlineData("response")]
    public async Task CancellationCleansUpAtEveryStage(string stage)
    {
        await using var harness = new BridgeHarness();
        if (stage == "readiness") await harness.StartAsync();
        else await harness.ReadyAsync();
        using var firstCancellation = new CancellationTokenSource();
        Task<CallToolResult>? first = null;
        if (stage == "queued")
        {
            first = harness.Tools.FetchProxy(Target, firstCancellation.Token);
            Accept(await harness.Handler.NextAsync());
        }
        using var cancellation = new CancellationTokenSource();
        var fetching = harness.Tools.FetchProxy(Target, cancellation.Token);
        PendingRequest? post = null;
        if (stage is "publication" or "response")
        {
            post = await harness.Handler.NextAsync();
            if (stage == "response") Accept(post);
        }
        cancellation.Cancel();
        Assert.Contains("cancelled", ErrorText(await fetching.WaitAsync(TimeSpan.FromSeconds(10))));
        if (stage == "publication") await post!.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (first is not null)
        {
            Assert.False(first.IsCompleted);
            firstCancellation.Cancel();
            Assert.True((await first).IsError);
        }
        harness.State.PollSucceeded(false);
        using var nextCancellation = new CancellationTokenSource();
        var next = harness.Tools.FetchProxy(Target, nextCancellation.Token);
        await harness.Handler.NextAsync(); // Serialization was released and a fresh request can publish.
        nextCancellation.Cancel();
        Assert.True((await next).IsError);
    }

    [Theory]
    [InlineData("faulted", false)]
    [InlineData("resetting", false)]
    [InlineData("stopping", false)]
    [InlineData("faulted", true)]
    [InlineData("resetting", true)]
    [InlineData("stopping", true)]
    public async Task ConnectionInterruptionCancelsActivePublicationOrResponse(string state, bool publishing)
    {
        await using var harness = new BridgeHarness();
        await harness.ReadyAsync();
        var fetching = harness.Tools.FetchProxy(Target, CancellationToken.None);
        var post = await harness.Handler.NextAsync();
        if (!publishing) Accept(post);
        harness.State.Set(state);
        Assert.Contains("interrupted", ErrorText(await fetching.WaitAsync(TimeSpan.FromSeconds(10))));
        if (publishing) await post.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ActualShutdownAndResetInterruptRequests()
    {
        await using var harness = new BridgeHarness();
        var poll = await harness.ReadyAsync();
        var fetching = harness.Tools.FetchProxy(Target, CancellationToken.None);
        Accept(await harness.Handler.NextAsync());
        poll.Respond(HttpStatusCode.Gone);
        var reset = await harness.Handler.NextAsync();
        Assert.Contains("interrupted", ErrorText(await fetching.WaitAsync(TimeSpan.FromSeconds(10))));
        reset.Respond(HttpStatusCode.Found);
        poll = await harness.Handler.NextAsync();
        poll.Respond(HttpStatusCode.NoContent);
        await harness.Handler.NextAsync();
        fetching = harness.Tools.FetchProxy(Target, CancellationToken.None);
        Accept(await harness.Handler.NextAsync());
        await harness.Bridge.StopAsync(CancellationToken.None);
        Assert.Contains("interrupted", ErrorText(await fetching.WaitAsync(TimeSpan.FromSeconds(10))));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(401)]
    [InlineData(413)]
    [InlineData(503)]
    [InlineData(200)]
    public async Task FailedPublicationIsNeverRetriedEvenWithEarlyResponse(int status)
    {
        await using var harness = new BridgeHarness();
        var poll = await harness.ReadyAsync();
        var fetching = harness.Tools.FetchProxy(Target, CancellationToken.None);
        var post = await harness.Handler.NextAsync();
        await DeliverAsync(harness, poll, HttpReply());
        if (status == 0) post.Reply.SetException(new IOException("sensitive upstream detail"));
        else post.Respond((HttpStatusCode)status, new StringContent("invalid acceptance body"));
        var error = ErrorText(await fetching);
        Assert.Contains("not retried", error);
        Assert.DoesNotContain("sensitive", error);
        Assert.DoesNotContain("get_messages", error);
        Assert.Equal(4, harness.Handler.RequestCount);
    }
}
