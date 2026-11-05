using System.Net;
using System.Text;
using ModelContextProtocol.Protocol;

namespace Rce3Mcp.Tests;

public sealed class FeedBridgeTests
{
    [Fact]
    public async Task StartupPollsWithoutResetAndBlocksSendsUntilConfirmed()
    {
        await using var harness = new BridgeHarness("raw test key");
        var poll = await harness.StartAsync();
        Assert.EndsWith(harness.Identity.Id.ToString("D"), poll.Uri.AbsolutePath);
        Assert.Equal("raw test key", poll.Authorization);
        Assert.Equal("connecting", harness.State.Snapshot().ConnectionState);
        var denied = await harness.Tools.SendMessage("test", CancellationToken.None);
        Assert.True(denied.IsError);
        Assert.Contains("Reader not ready", Assert.IsType<TextContentBlock>(denied.Content[0]).Text);
        Assert.Equal(1, harness.Handler.RequestCount);
        poll.Respond(HttpStatusCode.NoContent);
        var next = await harness.Handler.NextAsync();
        Assert.True(harness.State.CanSend);
        Assert.Equal("polling", harness.State.Snapshot().ConnectionState);
        Assert.NotNull(harness.State.Snapshot().LastSuccessfulPollAt);
        Assert.Null(harness.State.Snapshot().LastMessageAt);
        await harness.Bridge.StopAsync(CancellationToken.None);
        await next.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("stopping", harness.State.Snapshot().ConnectionState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceivedBodiesHaveBoundsAndOversizedBodiesKeepMetadata(bool unknownLength)
    {
        await using var harness = new BridgeHarness(options: new() { MaxMessageSizeBytes = 3 });
        var poll = await harness.StartAsync();
        HttpContent content = unknownLength ? new UnknownLengthContent([1, 2, 3, 4, 5]) : new ByteArrayContent([1, 2, 3, 4, 5]);
        content.Headers.TryAddWithoutValidation("Content-Type", "application/custom;mode=exact");
        poll.Respond(HttpStatusCode.OK, content);
        var next = await harness.Handler.NextAsync();
        var entry = Assert.Single(harness.History.Read().Messages);
        Assert.True(entry.BodyOmitted);
        Assert.Null(entry.Body);
        Assert.Equal(unknownLength ? null : (long?)5, entry.ByteLength);
        Assert.Equal("application/custom;mode=exact", entry.ContentType);
        next.Respond(HttpStatusCode.OK, new UnknownLengthContent([0, 255, 128]));
        await harness.Handler.NextAsync();
        var accepted = harness.History.Read().Messages[1];
        Assert.False(accepted.BodyOmitted);
        Assert.Equal("application/octet-stream", accepted.ContentType);
        Assert.Equal("AP+A", accepted.Body);
    }

    [Fact]
    public async Task OverflowResetsSameReaderOnlyAndPreservesHistory()
    {
        await using var harness = new BridgeHarness("raw test key");
        var poll = await harness.ReadyAsync();
        harness.History.Add([65], "text/plain", 1);
        poll.Respond(HttpStatusCode.Gone);
        var reset = await harness.Handler.NextAsync();
        Assert.Equal(poll.Uri.AbsoluteUri + "/reset", reset.Uri.AbsoluteUri);
        Assert.Equal(poll.Authorization, reset.Authorization);
        Assert.Equal("resetting", harness.State.Snapshot().ConnectionState);
        reset.Reply.SetResult(new(HttpStatusCode.Found) { Headers = { Location = new Uri("http://untrusted.test/") } });
        var resumed = await harness.Handler.NextAsync();
        Assert.Equal(poll.Uri, resumed.Uri);
        Assert.Equal(1, harness.State.Snapshot().ResetCount);
        resumed.Respond(HttpStatusCode.NoContent);
        await harness.Handler.NextAsync();
        Assert.True(harness.State.CanSend);
        Assert.Single(harness.History.Read().Messages);
    }

    [Fact]
    public async Task TransientResetFailureRetriesResetBeforeReturningToPolling()
    {
        await using var harness = new BridgeHarness();
        var poll = await harness.ReadyAsync();
        poll.Respond(HttpStatusCode.Gone);
        var reset = await harness.Handler.NextAsync();
        reset.Respond(HttpStatusCode.ServiceUnavailable);
        await harness.Time.ReleaseRetryAsync();
        var retriedReset = await harness.Handler.NextAsync();
        Assert.Equal(reset.Uri, retriedReset.Uri);
        Assert.False(harness.State.CanSend);
        retriedReset.Respond(HttpStatusCode.Found);
        var resumed = await harness.Handler.NextAsync();
        Assert.Equal(poll.Uri, resumed.Uri);
        resumed.Respond(HttpStatusCode.NoContent);
        await harness.Handler.NextAsync();
        Assert.Equal(1, harness.State.Snapshot().ResetCount);
        Assert.True(harness.State.CanSend);
    }

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(0)]
    public async Task TransientFailuresRetryWithInjectedTime(int status)
    {
        await using var harness = new BridgeHarness();
        var poll = await harness.StartAsync();
        if (status == 0) poll.Reply.SetException(new HttpRequestException("sensitive raw test key"));
        else poll.Respond((HttpStatusCode)status);
        await harness.Time.ReleaseRetryAsync();
        var retry = await harness.Handler.NextAsync();
        Assert.Equal("retrying", harness.State.Snapshot().ConnectionState);
        Assert.DoesNotContain("sensitive", harness.State.Snapshot().LastError!);
        retry.Respond(HttpStatusCode.NoContent);
        await harness.Handler.NextAsync();
        Assert.Null(harness.State.Snapshot().LastError);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    public async Task PermanentFailuresKeepUsefulFaultedStatus(int status)
    {
        await using var harness = new BridgeHarness();
        var poll = await harness.StartAsync();
        poll.Respond((HttpStatusCode)status, new StringContent("sensitive upstream body"));
        await harness.Bridge.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
        var snapshot = harness.State.Snapshot();
        Assert.Equal("faulted", snapshot.ConnectionState);
        Assert.Contains(status.ToString(), snapshot.LastError);
        Assert.DoesNotContain("sensitive", snapshot.LastError!);
        Assert.Equal(1, harness.Handler.RequestCount);
        Assert.False(harness.Tools.GetStatus().IsError);
        Assert.False(harness.Tools.GetMessages().IsError);
    }
}
