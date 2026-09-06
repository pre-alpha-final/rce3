using System.Net;
using System.Text;

namespace TabletUI.Tests;

public sealed class FeedConnectionTests
{
    [Theory]
    [InlineData(200)]
    [InlineData(204)]
    [InlineData(302)]
    public async Task Reset_is_first_and_redirected_200_preserves_body(int status)
    {
        await using var h = new ConnectionHarness();
        await h.ConnectAsync("test-only raw authorization");
        var reset = await h.Handler.NextAsync();
        Assert.Equal(HttpMethod.Get, reset.Method);
        Assert.StartsWith(ConnectionHarness.Feed + "/", reset.Uri.AbsoluteUri);
        Assert.EndsWith("/reset", reset.Uri.AbsolutePath);
        var readerUrl = reset.Uri.AbsoluteUri[..^"/reset".Length];
        Assert.True(Guid.TryParse(readerUrl[(readerUrl.LastIndexOf('/') + 1)..], out _));
        Assert.False(h.Connection.CanSend);
        Assert.Null(await h.Connection.PublishAsync("too early", "text/plain"));
        Assert.Equal(1, h.Handler.RequestCount);
        Assert.Equal(["test-only raw authorization"], reset.Authorization);

        // Fetch may follow the reset redirect and return the first message as HTTP 200.
        const string body = "  first message\r\nnext line\n雪 & <tag>  ";
        const string type = "text/plain; charset=utf-8; profile=\"original\"";
        reset.Reply((HttpStatusCode)status, Encoding.UTF8.GetBytes(body), type);
        var poll = await h.Handler.NextAsync();
        Assert.Equal(readerUrl, poll.Uri.AbsoluteUri);
        Assert.Equal(reset.Authorization, poll.Authorization);
        Assert.True(h.Connection.CanSend);
        Assert.Equal("Connected", h.Connection.Status);
        if (status == 200)
        {
            var entry = Assert.Single(h.Connection.Entries);
            Assert.Equal("Received", entry.Direction);
            Assert.Equal(body, entry.Preview);
            Assert.Equal(type, entry.ContentType);
            Assert.Equal($"{Encoding.UTF8.GetByteCount(body)} bytes", entry.Detail);
        }
        else { Assert.Empty(h.Connection.Entries); }
        Assert.Equal(1, h.Handler.MaxActiveGets);
    }

    [Fact]
    public async Task No_content_continues_same_reader_without_logging_a_message()
    {
        await using var h = new ConnectionHarness();
        var poll = await h.ReadyAsync();
        Assert.Empty(poll.Authorization);
        poll.Reply(HttpStatusCode.NoContent);
        var next = await h.Handler.NextAsync();
        Assert.Equal(poll.Uri, next.Uri);
        Assert.Empty(h.Connection.Entries);
        Assert.True(h.Connection.CanSend);
    }

    [Fact]
    public async Task Gone_resets_same_reader_before_resuming_polling()
    {
        await using var h = new ConnectionHarness();
        var poll = await h.ReadyAsync();
        poll.Reply(HttpStatusCode.Gone);
        var reset = await h.Handler.NextAsync();
        Assert.Equal(poll.Uri.AbsoluteUri + "/reset", reset.Uri.AbsoluteUri);
        Assert.False(h.Connection.CanSend);
        Assert.Contains(h.Connection.Entries, e => e.Detail.Contains("resetting"));
        reset.Reply(HttpStatusCode.NoContent);
        var next = await h.Handler.NextAsync();
        Assert.Equal(poll.Uri, next.Uri);
        Assert.True(h.Connection.CanSend);
        Assert.Equal(1, h.Handler.MaxActiveGets);
    }

    [Theory]
    [InlineData(401, true)]
    [InlineData(403, true)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    public async Task Authorization_failure_stops_and_resume_does_not_restart(int status, bool duringReset)
    {
        await using var h = new ConnectionHarness();
        PendingRequest request;
        if (duringReset)
        {
            await h.ConnectAsync();
            request = await h.Handler.NextAsync();
        }
        else { request = await h.ReadyAsync(); }
        var halted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Connection.Changed += () =>
        {
            if (h.Connection.Status.Contains("Authorization failed")) { halted.TrySetResult(); }
        };
        var count = h.Handler.RequestCount;
        request.Reply((HttpStatusCode)status);
        await halted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains($"HTTP {status}", h.Connection.Status);
        Assert.False(h.Connection.CanSend);
        Assert.Null(await h.Connection.PublishAsync("blocked", "text/plain"));
        await h.Connection.ResumeAsync();
        await h.Connection.DisconnectAsync();
        Assert.Equal(count, h.Handler.RequestCount);
        Assert.Equal(0, h.Handler.ActiveGets);
    }

    [Theory]
    [InlineData(408, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(408, false)]
    [InlineData(429, false)]
    [InlineData(500, false)]
    [InlineData(503, false)]
    public async Task Transient_http_failure_retries_current_operation(int status, bool duringReset)
    {
        await using var h = new ConnectionHarness();
        PendingRequest request;
        if (duringReset)
        {
            await h.ConnectAsync();
            request = await h.Handler.NextAsync();
        }
        else { request = await h.ReadyAsync(); }
        request.Reply((HttpStatusCode)status);
        var retry = await h.Handler.NextAsync();
        Assert.Equal(request.Uri, retry.Uri);
        Assert.False(h.Connection.CanSend);
        Assert.Contains(h.Connection.Entries, e => e.Detail.Contains($"HTTP {status}") && e.Detail.Contains("retrying"));
        retry.Reply(HttpStatusCode.NoContent);
        var next = await h.Handler.NextAsync();
        Assert.DoesNotContain("/reset", next.Uri.AbsolutePath);
        Assert.True(h.Connection.CanSend);
        Assert.Equal(1, h.Handler.MaxActiveGets);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("io")]
    [InlineData("timeout")]
    public async Task Transient_transport_failure_retries_without_exposing_exception(string kind)
    {
        await using var h = new ConnectionHarness();
        var poll = await h.ReadyAsync();
        const string sensitive = "test-only secret exception data";
        poll.Fail(kind switch
        {
            "http" => new HttpRequestException(sensitive),
            "io" => new IOException(sensitive),
            _ => new OperationCanceledException(sensitive)
        });
        var retry = await h.Handler.NextAsync();
        Assert.Equal(poll.Uri, retry.Uri);
        Assert.Contains(h.Connection.Entries, e => e.Detail.Contains("retrying"));
        Assert.All(h.Connection.Entries, e => Assert.DoesNotContain(sensitive, e.ToString()));
        retry.Reply(HttpStatusCode.NoContent);
        await h.Handler.NextAsync();
        Assert.True(h.Connection.CanSend);
    }

    [Fact]
    public async Task Disconnect_cancels_pending_poll_and_post_and_never_retries()
    {
        await using var h = new ConnectionHarness();
        var poll = await h.ReadyAsync();
        var publishing = h.Connection.PublishAsync("message", "text/plain");
        var post = await h.Handler.NextAsync();
        Assert.Equal(HttpMethod.Post, post.Method);
        await h.Connection.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await poll.WaitForCancellationAsync();
        await post.WaitForCancellationAsync();
        Assert.Null(await publishing.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(h.Connection.CanSend);
        Assert.Equal("Disconnected", h.Connection.Status);
        Assert.Equal(3, h.Handler.RequestCount);
        Assert.Equal(0, h.Handler.ActiveGets);
    }

    [Fact]
    public async Task Resume_interrupts_existing_loop_without_reset_or_duplicate_polls()
    {
        await using var h = new ConnectionHarness();
        var poll = await h.ReadyAsync();
        var reader = poll.Uri;
        for (var i = 0; i < 10; i++)
        {
            await h.Connection.ResumeAsync();
            await poll.WaitForCancellationAsync();
            poll = await h.Handler.NextAsync();
            Assert.Equal(reader, poll.Uri);
            Assert.False(h.Connection.CanSend);
            poll.Reply(HttpStatusCode.NoContent);
            poll = await h.Handler.NextAsync();
            Assert.True(h.Connection.CanSend);
        }
        await h.Connection.DisconnectAsync();
        await poll.WaitForCancellationAsync();
        Assert.Equal(22, h.Handler.RequestCount);
        Assert.Equal(1, h.Handler.MaxActiveGets);
        Assert.Equal(0, h.Handler.ActiveGets);
    }

    [Fact]
    public async Task Offline_cancels_poll_and_resume_keeps_reader()
    {
        await using var h = new ConnectionHarness();
        var poll = await h.ReadyAsync();
        await h.Connection.SetOfflineAsync();
        await poll.WaitForCancellationAsync();
        Assert.False(h.Connection.CanSend);
        Assert.Equal("Offline", h.Connection.Status);
        Assert.Null(await h.Connection.PublishAsync("offline", "text/plain"));
        await h.Connection.ResumeAsync();
        var next = await h.Handler.NextAsync();
        Assert.Equal(poll.Uri, next.Uri);
        next.Reply(HttpStatusCode.NoContent);
        await h.Handler.NextAsync();
        Assert.True(h.Connection.CanSend);
        Assert.Equal(1, h.Handler.MaxActiveGets);
    }

    [Fact]
    public async Task Reconnect_cancels_previous_reader_before_creating_a_new_one()
    {
        await using var h = new ConnectionHarness();
        var poll = await h.ReadyAsync();
        await h.ConnectAsync();
        await poll.WaitForCancellationAsync();
        var reset = await h.Handler.NextAsync();
        Assert.EndsWith("/reset", reset.Uri.AbsoluteUri);
        Assert.NotEqual(poll.Uri.AbsoluteUri + "/reset", reset.Uri.AbsoluteUri);
        Assert.Equal(1, h.Handler.MaxActiveGets);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  spaces  \r\n\n雪🙂\\literal\n")]
    [InlineData("youtube: back6")]
    [InlineData("youtube: back")]
    [InlineData("youtube: playpause")]
    [InlineData("youtube: forward")]
    [InlineData("youtube: forward6")]
    public async Task Publish_preserves_exact_utf8_body_content_type_and_raw_authorization(string body)
    {
        await using var h = new ConnectionHarness();
        await h.ReadyAsync("test-only raw key");
        const string type = "text/plain; charset=utf-8; profile=\"exact\"";
        var sending = h.Connection.PublishAsync(body, type);
        var post = await h.Handler.NextAsync();
        Assert.Equal(HttpMethod.Post, post.Method);
        Assert.Equal(ConnectionHarness.Feed, post.Uri.AbsoluteUri);
        Assert.Equal(Encoding.UTF8.GetBytes(body), post.Body);
        Assert.Equal([type], post.ContentTypes);
        Assert.Equal(["test-only raw key"], post.Authorization);
        Assert.False(h.Connection.CanSend);
        Assert.Null(await h.Connection.PublishAsync("duplicate click", type));
        post.Reply(HttpStatusCode.OK, "Message distributed to 2 reader(s)."u8.ToArray());
        Assert.Equal(2, await sending);
        Assert.True(h.Connection.CanSend);
        var entry = Assert.Single(h.Connection.Entries);
        Assert.Equal("Sent", entry.Direction);
        Assert.Equal(body, entry.Preview);
        Assert.Equal(type, entry.ContentType);
        Assert.Equal("Accepted by 2 reader queue(s).", entry.Detail);
        Assert.Equal(3, h.Handler.RequestCount);
    }

    [Theory]
    [InlineData("500")]
    [InlineData("429")]
    [InlineData("401")]
    [InlineData("403")]
    [InlineData("http")]
    [InlineData("io")]
    [InlineData("timeout")]
    [InlineData("bad-count")]
    [InlineData("negative-count")]
    public async Task Failed_or_ambiguous_post_is_never_retried(string failure)
    {
        await using var h = new ConnectionHarness();
        var poll = await h.ReadyAsync();
        var sending = h.Connection.PublishAsync("send once", "text/plain");
        var post = await h.Handler.NextAsync();
        switch (failure)
        {
            case "http": post.Fail(new HttpRequestException("test-only details")); break;
            case "io": post.Fail(new IOException("test-only details")); break;
            case "timeout": post.Fail(new OperationCanceledException()); break;
            case "bad-count": post.Reply(HttpStatusCode.OK, "not a count"u8.ToArray()); break;
            case "negative-count": post.Reply(HttpStatusCode.OK, "Message distributed to -1 reader(s)."u8.ToArray()); break;
            default: post.Reply((HttpStatusCode)int.Parse(failure)); break;
        }
        Assert.Null(await sending.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains(h.Connection.Entries, e => e.Direction == "Error" && e.Detail.Contains("Not resent."));
        if (failure is "401" or "403")
        {
            await poll.WaitForCancellationAsync();
            Assert.Contains("Authorization failed", h.Connection.Status);
            Assert.False(h.Connection.CanSend);
            await h.Connection.ResumeAsync();
        }
        else
        {
            Assert.True(h.Connection.CanSend);
            poll.Reply(HttpStatusCode.NoContent);
            await h.Handler.NextAsync();
        }
        await h.Connection.DisconnectAsync();
        Assert.Equal(failure is "401" or "403" ? 3 : 4, h.Handler.RequestCount);
    }

    [Fact]
    public async Task Invalid_content_type_does_not_send()
    {
        await using var h = new ConnectionHarness();
        await h.ReadyAsync();
        Assert.Null(await h.Connection.PublishAsync("body", "not a content type"));
        Assert.Equal(2, h.Handler.RequestCount);
        Assert.Contains(h.Connection.Entries, e => e.Detail == "Enter a valid Content-Type.");
        Assert.True(h.Connection.CanSend);
    }

    [Theory]
    [InlineData("2")]
    [InlineData("message distributed to 2 reader(s).")]
    [InlineData("Message distributed to 2 readers.")]
    [InlineData("Message distributed to 2 reader(s)")]
    [InlineData("Message distributed to 2 reader(s).\n")]
    [InlineData("prefix Message distributed to 2 reader(s).")]
    [InlineData("Message distributed to 2 reader(s). suffix")]
    [InlineData("Message distributed to 2147483648 reader(s).")]
    [InlineData("Message distributed to NaN reader(s).")]
    public async Task Unexpected_post_response_format_is_not_reported_as_success_or_retried(string response)
    {
        await using var h = new ConnectionHarness();
        await h.ReadyAsync();
        var sending = h.Connection.PublishAsync("send once", "text/plain");
        var post = await h.Handler.NextAsync();
        post.Reply(HttpStatusCode.OK, Encoding.UTF8.GetBytes(response), "text/plain");
        Assert.Null(await sending.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("Error", Assert.Single(h.Connection.Entries).Direction);
        Assert.Contains("unexpected acceptance count. Not resent.", h.Connection.Entries[0].Detail);
        Assert.Equal(3, h.Handler.RequestCount);
        Assert.True(h.Connection.CanSend);
    }

    [Fact]
    public async Task Disconnect_cancels_initial_reset_and_resume_does_not_reconnect()
    {
        await using var h = new ConnectionHarness();
        await h.ConnectAsync();
        var reset = await h.Handler.NextAsync();
        await h.Connection.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await reset.WaitForCancellationAsync();
        await h.Connection.ResumeAsync();
        Assert.Equal(1, h.Handler.RequestCount);
        Assert.False(h.Connection.CanSend);
        Assert.Equal("Disconnected", h.Connection.Status);
        Assert.Equal(0, h.Handler.ActiveGets);
    }

    [Fact]
    public async Task Invalid_settings_do_not_start_network_requests()
    {
        await using var h = new ConnectionHarness();
        await h.Connection.ConnectAsync(new(1, "https://example.test/not-a-guid", ""));
        Assert.Equal(0, h.Handler.RequestCount);
        Assert.False(h.Connection.CanSend);
        Assert.Contains("feed URL", h.Connection.Status);
    }

    [Fact]
    public async Task Zero_acceptance_count_is_a_success()
    {
        await using var h = new ConnectionHarness();
        await h.ReadyAsync();
        var sending = h.Connection.PublishAsync("body", "text/plain");
        (await h.Handler.NextAsync()).Reply(HttpStatusCode.OK, "Message distributed to 0 reader(s)."u8.ToArray());
        Assert.Equal(0, await sending);
        Assert.Equal("Sent", Assert.Single(h.Connection.Entries).Direction);
    }
}
