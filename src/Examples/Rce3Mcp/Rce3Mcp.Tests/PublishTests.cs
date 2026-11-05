using System.Net;
using System.Text;
using ModelContextProtocol.Protocol;

namespace Rce3Mcp.Tests;

public sealed class PublishTests
{
    [Theory]
    [InlineData("hello\r\n世界", "utf8", "text/plain; charset=utf-8")]
    [InlineData("AP+A", "base64", "application/octet-stream")]
    [InlineData("", "utf8", "text/plain; charset=utf-8")]
    public async Task PublishSendsExactBytesAndNeverAddsLocalHistory(string body, string encoding, string expectedType)
    {
        await using var harness = new BridgeHarness("raw test key");
        await harness.ReadyAsync();
        var sending = harness.Tools.SendMessage(body, CancellationToken.None, encoding);
        var post = await harness.Handler.NextAsync();
        Assert.Equal(HttpMethod.Post, post.Method);
        Assert.Equal("raw test key", post.Authorization);
        Assert.Equal(expectedType, post.ContentType);
        Assert.Equal(encoding == "utf8" ? Encoding.UTF8.GetBytes(body) : Convert.FromBase64String(body), post.Body);
        post.Respond(HttpStatusCode.OK, new StringContent("Message distributed to 3 reader(s)."));
        var result = await sending;
        Assert.False(result.IsError);
        Assert.Equal(3, result.StructuredContent!.Value.GetProperty("acceptedReaderCount").GetInt32());
        Assert.Empty(harness.History.Read().Messages);
    }

    [Fact]
    public async Task FullContentTypeIsPreservedOnPublish()
    {
        await using var harness = new BridgeHarness();
        await harness.ReadyAsync();
        var sending = harness.Tools.SendMessage("x", CancellationToken.None, contentType: "text/custom;mode=exact;charset=utf-8");
        var post = await harness.Handler.NextAsync();
        Assert.Equal("text/custom;mode=exact;charset=utf-8", post.ContentType);
        post.Respond(HttpStatusCode.OK, new StringContent("Message distributed to 0 reader(s)."));
        Assert.False((await sending).IsError);
    }

    [Theory]
    [InlineData("bad", "unknown", null)]
    [InlineData("@@", "base64", null)]
    [InlineData("test", "utf8", "invalid type")]
    [InlineData("test", "utf8", "text/plain\r\nInjected: value")]
    public async Task InvalidPayloadsAreToolErrorsWithoutRequests(string body, string encoding, string? contentType)
    {
        await using var harness = new BridgeHarness();
        var result = await harness.Tools.SendMessage(body, CancellationToken.None, encoding, contentType);
        Assert.True(result.IsError);
        Assert.Equal(0, harness.Handler.RequestCount);
    }

    [Theory]
    [InlineData("four", "utf8")]
    [InlineData("YWJjZA==", "base64")]
    public async Task SendSizeLimitIsMeasuredInDecodedBytes(string body, string encoding)
    {
        await using var harness = new BridgeHarness(options: new() { MaxMessageSizeBytes = 3 });
        var result = await harness.Tools.SendMessage(body, CancellationToken.None, encoding);
        Assert.True(result.IsError);
        Assert.Contains("limit", Assert.IsType<TextContentBlock>(result.Content[0]).Text);
        Assert.Equal(0, harness.Handler.RequestCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(401)]
    [InlineData(413)]
    [InlineData(503)]
    [InlineData(200)]
    public async Task FailedOrAmbiguousPostIsNeverRetried(int status)
    {
        await using var harness = new BridgeHarness();
        await harness.ReadyAsync();
        var sending = harness.Tools.SendMessage("one", CancellationToken.None);
        var post = await harness.Handler.NextAsync();
        if (status == 0) post.Reply.SetException(new IOException("raw secret"));
        else post.Respond((HttpStatusCode)status, new StringContent("invalid acceptance body"));
        var result = await sending;
        Assert.True(result.IsError);
        Assert.Contains("not retried", Assert.IsType<TextContentBlock>(result.Content[0]).Text);
        Assert.DoesNotContain("raw secret", Assert.IsType<TextContentBlock>(result.Content[0]).Text);
        Assert.Equal(3, harness.Handler.RequestCount);
    }

    [Fact]
    public async Task ShutdownCancelsPendingPublishAsWellAsPoll()
    {
        await using var harness = new BridgeHarness();
        var poll = await harness.ReadyAsync();
        var sending = harness.Tools.SendMessage("one", CancellationToken.None);
        var post = await harness.Handler.NextAsync();
        await harness.Bridge.StopAsync(CancellationToken.None);
        Assert.True((await sending.WaitAsync(TimeSpan.FromSeconds(10))).IsError);
        await post.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await poll.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task PublishCancellationStopsRequestAndReportsUncertainDelivery()
    {
        await using var harness = new BridgeHarness();
        await harness.ReadyAsync();
        using var cancellation = new CancellationTokenSource();
        var sending = harness.Tools.SendMessage("one", cancellation.Token);
        var post = await harness.Handler.NextAsync();
        cancellation.Cancel();
        Assert.True((await sending).IsError);
        await post.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(3, harness.Handler.RequestCount);
    }
}
