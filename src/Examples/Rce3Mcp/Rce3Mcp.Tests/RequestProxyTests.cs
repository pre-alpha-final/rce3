using System.Net;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Rce3Mcp.Tests;

public sealed class RequestProxyTests
{
    private const string Target = "https://TARGET.example/api/status?x=%2f";

    [Theory]
    [InlineData(200)]
    [InlineData(302)]
    [InlineData(401)]
    [InlineData(500)]
    public async Task FirstHttpResponseWinsEvenBeforePublishAcknowledgmentAndAfterHistoryEviction(int status)
    {
        await using var harness = new BridgeHarness(options: new() { HistoryCapacity = 1 });
        var poll = await harness.ReadyAsync();
        var calling = harness.Tools.RequestProxy("  " + Target + "  ", CancellationToken.None);
        var post = await harness.Handler.NextAsync();
        Assert.Equal(HttpMethod.Post, post.Method);
        Assert.Equal(Encoding.UTF8.GetBytes("requestproxy: " + Target), post.Body);
        Assert.Equal("text/plain; charset=utf-8", post.ContentType);

        poll = await DeliverAsync(harness, poll, ErrorReply("OriginNotImported"));
        poll = await DeliverAsync(harness, poll, Prefix(Target) + "not JSON");
        poll = await DeliverAsync(harness, poll, HttpReply(target: Target.ToLowerInvariant(), body: "wrong URL"));
        poll = await DeliverAsync(harness, poll, "requestproxy: " + Target);
        poll = await DeliverAsync(harness, poll, HttpReply(status: status, body: "first\r\n世界"));
        poll = await DeliverAsync(harness, poll, HttpReply(body: "second"));
        await DeliverAsync(harness, poll, "unrelated message");
        Assert.False(calling.IsCompleted);

        Accept(post);
        var result = await FinishAsync(calling);
        Assert.False(result.IsError);
        var response = result.StructuredContent!.Value;
        Assert.Equal(status, response.GetProperty("status").GetInt32());
        Assert.Equal("Target status", response.GetProperty("statusText").GetString());
        Assert.Equal(status is >= 200 and < 300, response.GetProperty("ok").GetBoolean());
        Assert.Equal("first\r\n世界", response.GetProperty("body").GetString());
        Assert.Equal(new[] { "first", "second" },
            response.GetProperty("headers").GetProperty("x-target").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("https://target.example/redirect",
            Assert.Single(response.GetProperty("headers").GetProperty("location").EnumerateArray()).GetString());
        Assert.Equal(5, response.EnumerateObject().Count());
        using var text = JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.True(JsonElement.DeepEquals(response, text.RootElement));
        Assert.Equal("unrelated message", Assert.Single(harness.History.Read().Messages).Body);
    }

    [Fact]
    public async Task WaitsForStartupReadinessAndSerializesCalls()
    {
        await using var harness = new BridgeHarness();
        var poll = await harness.StartAsync();
        var first = harness.Tools.RequestProxy(Target, CancellationToken.None);
        var second = harness.Tools.RequestProxy(Target, CancellationToken.None);
        Assert.Equal(1, harness.Handler.RequestCount);
        poll.Respond(HttpStatusCode.OK, new StringContent(HttpReply(body: "startup backlog")));

        (var firstPost, poll) = await NextPublishAndPollAsync(harness);
        Accept(firstPost);
        poll.Respond(HttpStatusCode.OK, new StringContent(HttpReply(body: "first")));
        Assert.Equal("first", (await FinishAsync(first)).StructuredContent!.Value.GetProperty("body").GetString());

        (var secondPost, poll) = await NextPublishAndPollAsync(harness);
        Accept(secondPost);
        Assert.False(second.IsCompleted);
        await DeliverAsync(harness, poll, HttpReply(body: "second"));
        Assert.Equal("second", (await FinishAsync(second)).StructuredContent!.Value.GetProperty("body").GetString());
    }

    [Fact]
    public async Task DeadlineSummarizesMatchingErrorsAndMalformedReplies()
    {
        await using var harness = new BridgeHarness();
        var poll = await harness.ReadyAsync();
        var calling = harness.Tools.RequestProxy(Target, CancellationToken.None);
        Accept(await harness.Handler.NextAsync());

        poll = await DeliverAsync(harness, poll, ErrorReply("OriginNotImported"));
        poll = await DeliverAsync(harness, poll, ErrorReply("TimeoutError"));
        poll = await DeliverAsync(harness, poll, Prefix(Target) + """{"status":200,"statusText":"OK","ok":true,"headers":{"x-target":[1]},"body":""}""");
        poll = await DeliverAsync(harness, poll, Prefix(Target) + """{"status":"200","statusText":"OK","ok":true,"headers":{},"body":""}""");
        poll.Respond(HttpStatusCode.OK, new ByteArrayContent([.. Encoding.UTF8.GetBytes(Prefix(Target) + "\""), 0xff, (byte)'"']));
        await harness.Handler.NextAsync();
        Assert.False(calling.IsCompleted);
        (await harness.Time.NextDeadlineAsync())();
        var error = ErrorText(await FinishAsync(calling));
        Assert.Contains("60-second deadline", error);
        Assert.Contains("Proxy errors: 2, target timeouts: 1, malformed matching replies: 3", error);
        Assert.DoesNotContain("sensitive target error", error);
        Assert.True(harness.State.CanSend);
    }

    [Theory]
    [InlineData("readiness")]
    [InlineData("queue")]
    [InlineData("publication")]
    public async Task DeadlineCoversAllStagesBeforeReplyMatching(string stage)
    {
        await using var harness = new BridgeHarness();
        if (stage == "readiness") await harness.StartAsync();
        else await harness.ReadyAsync();

        using var firstCancellation = new CancellationTokenSource();
        Task<CallToolResult>? first = null;
        if (stage == "queue")
        {
            first = harness.Tools.RequestProxy(Target, firstCancellation.Token);
            await harness.Time.NextDeadlineAsync();
            Accept(await harness.Handler.NextAsync());
        }
        var calling = harness.Tools.RequestProxy(Target, CancellationToken.None);
        var post = stage == "publication" ? await harness.Handler.NextAsync() : null;
        (await harness.Time.NextDeadlineAsync())();
        Assert.Contains("60-second deadline", ErrorText(await FinishAsync(calling)));
        if (post is not null) await post.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (first is not null)
        {
            Assert.False(first.IsCompleted);
            firstCancellation.Cancel();
            Assert.Contains("cancelled", ErrorText(await FinishAsync(first)));
        }
        Assert.Equal(stage == "readiness" ? 1 : 3, harness.Handler.RequestCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/relative")]
    [InlineData("ftp://target.example/api")]
    [InlineData("https://user:password@target.example/api")]
    [InlineData("https://target.example/api#fragment")]
    [InlineData("https://target.example/\napi")]
    [InlineData("https://target.example/\r")]
    [InlineData("https://target.example/a\tb")]
    public async Task RejectsInvalidUrlsBeforePublishing(string? url)
    {
        await using var harness = new BridgeHarness();
        Assert.Contains("absolute HTTP(S)", ErrorText(await harness.Tools.RequestProxy(url!, CancellationToken.None)));
        Assert.Equal(0, harness.Handler.RequestCount);
    }

    [Fact]
    public async Task EnforcesUtf8AndBothCommandSizeLimits()
    {
        await using var small = new BridgeHarness(options: new() { MaxMessageSizeBytes = 45 });
        Assert.Contains("Unicode", ErrorText(await small.Tools.RequestProxy("https://x/" + '\ud800', CancellationToken.None)));
        Assert.Contains("45-byte limit", ErrorText(await small.Tools.RequestProxy("https://x/" + new string('世', 10), CancellationToken.None)));
        Assert.Equal(0, small.Handler.RequestCount);

        await using var normal = new BridgeHarness();
        Assert.Contains("65536-byte limit", ErrorText(await normal.Tools.RequestProxy("https://x/" + new string('世', 22000), CancellationToken.None)));
        Assert.Equal(0, normal.Handler.RequestCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationReleasesThePendingCall(bool publishing)
    {
        await using var harness = new BridgeHarness();
        var poll = await harness.ReadyAsync();
        using var cancellation = new CancellationTokenSource();
        var calling = harness.Tools.RequestProxy(Target, cancellation.Token);
        var post = await harness.Handler.NextAsync();
        if (!publishing) Accept(post);
        cancellation.Cancel();

        Assert.Contains("cancelled", ErrorText(await FinishAsync(calling)));
        if (publishing) await post.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var next = harness.Tools.RequestProxy(Target, CancellationToken.None);
        Accept(await harness.Handler.NextAsync());
        await DeliverAsync(harness, poll, HttpReply());
        Assert.False((await FinishAsync(next)).IsError);
    }

    [Theory]
    [InlineData(HttpStatusCode.Gone)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(null)]
    public async Task ReaderResetFaultAndShutdownInterruptPendingCalls(HttpStatusCode? pollStatus)
    {
        await using var harness = new BridgeHarness();
        var poll = await harness.ReadyAsync();
        var calling = harness.Tools.RequestProxy(Target, CancellationToken.None);
        var post = await harness.Handler.NextAsync();

        if (pollStatus is { } status) poll.Respond(status);
        else await harness.Bridge.StopAsync(CancellationToken.None);
        Assert.Contains("interrupted", ErrorText(await FinishAsync(calling)));
        await post.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));

        if (pollStatus != HttpStatusCode.Gone) return;
        var reset = await harness.Handler.NextAsync();
        Assert.EndsWith("/reset", reset.Uri.AbsolutePath);
        reset.Respond(HttpStatusCode.Found);
        poll = await harness.Handler.NextAsync();
        poll.Respond(HttpStatusCode.NoContent);
        poll = await harness.Handler.NextAsync();
        var next = harness.Tools.RequestProxy(Target, CancellationToken.None);
        Accept(await harness.Handler.NextAsync());
        await DeliverAsync(harness, poll, HttpReply());
        Assert.False((await FinishAsync(next)).IsError);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.OK)]
    public async Task FailedPublishIsNeverRetriedEvenWithAnEarlyResponse(HttpStatusCode status)
    {
        await using var harness = new BridgeHarness();
        var poll = await harness.ReadyAsync();
        var calling = harness.Tools.RequestProxy(Target, CancellationToken.None);
        var post = await harness.Handler.NextAsync();
        await DeliverAsync(harness, poll, HttpReply());
        post.Respond(status, new StringContent("invalid acceptance body"));

        Assert.Contains("POST was not retried", ErrorText(await FinishAsync(calling)));
        Assert.Equal(4, harness.Handler.RequestCount);
    }

    internal static string HttpReply(string target = Target, int status = 200, string body = "response body") =>
        Prefix(target) + JsonSerializer.Serialize(new
        {
            status,
            statusText = "Target status",
            ok = status is >= 200 and < 300,
            headers = new Dictionary<string, string[]>
            {
                ["x-target"] = ["first", "second"],
                ["location"] = ["https://target.example/redirect"]
            },
            body
        });

    internal static string ErrorReply(string name, string target = Target) =>
        Prefix(target) + JsonSerializer.Serialize(new { error = new { name, message = "sensitive target error" } });

    private static string Prefix(string target) => $"requestproxy_response: {target}\n";

    private static async Task<(PendingRequest Post, PendingRequest Poll)> NextPublishAndPollAsync(BridgeHarness harness)
    {
        var first = await harness.Handler.NextAsync();
        var second = await harness.Handler.NextAsync();
        Assert.NotEqual(first.Method, second.Method);
        return first.Method == HttpMethod.Post ? (first, second) : (second, first);
    }

    private static async Task<PendingRequest> DeliverAsync(BridgeHarness harness, PendingRequest poll, string reply)
    {
        poll.Respond(HttpStatusCode.OK, new StringContent(reply));
        return await harness.Handler.NextAsync();
    }

    private static void Accept(PendingRequest post)
    {
        Assert.Equal(HttpMethod.Post, post.Method);
        post.Respond(HttpStatusCode.OK, new StringContent("Message distributed to 2 reader(s)."));
    }

    private static string ErrorText(CallToolResult result)
    {
        Assert.True(result.IsError);
        return Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
    }

    private static Task<CallToolResult> FinishAsync(Task<CallToolResult> call) => call.WaitAsync(TimeSpan.FromSeconds(10));
}
