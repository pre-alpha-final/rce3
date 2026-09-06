using System.Net;
using System.Text;
using TabletUI.Services;

namespace TabletUI.Tests;

public sealed class CommunicationLogTests
{
    [Theory]
    [InlineData("")]
    [InlineData("  first\r\nsecond\n雪 & <script>  ")]
    public void Utf8_preview_preserves_text_and_whitespace(string body) =>
        Assert.Equal(body, CommunicationEntry.FormatPreview(Encoding.UTF8.GetBytes(body)));

    [Fact]
    public void Text_preview_is_truncated_only_beyond_limit()
    {
        var exact = new string('x', CommunicationEntry.PreviewLimit);
        Assert.Equal(exact, CommunicationEntry.FormatPreview(Encoding.UTF8.GetBytes(exact)));
        Assert.Equal(exact + " … [truncated]", CommunicationEntry.FormatPreview(Encoding.UTF8.GetBytes(exact + "tail")));
    }

    [Fact]
    public void Invalid_utf8_uses_exact_hex_instead_of_replacement_characters()
    {
        Assert.Equal("Hex: ff00c328", CommunicationEntry.FormatPreview([0xff, 0x00, 0xc3, 0x28]));
        Assert.Equal("Hex: e282", CommunicationEntry.FormatPreview([0xe2, 0x82]));
    }

    [Fact]
    public void Binary_preview_is_bounded_and_marks_truncation()
    {
        var limit = CommunicationEntry.PreviewLimit / 3;
        var bytes = Enumerable.Repeat((byte)0xff, limit + 1).ToArray();
        Assert.Equal("Hex: " + new string('f', limit * 2) + " … [truncated]", CommunicationEntry.FormatPreview(bytes));
        Assert.Equal("Hex: " + new string('f', limit * 2), CommunicationEntry.FormatPreview(bytes[..limit]));
    }

    [Fact]
    public async Task Log_keeps_latest_200_messages_in_order_and_clear_empties_it()
    {
        await using var h = new ConnectionHarness();
        var poll = await h.ReadyAsync();
        for (var i = 0; i < FeedConnection.LogLimit + 7; i++)
        {
            poll.Reply(HttpStatusCode.OK, Encoding.UTF8.GetBytes($"message {i}"));
            poll = await h.Handler.NextAsync();
        }
        var snapshot = h.Connection.Entries;
        Assert.Equal(FeedConnection.LogLimit, snapshot.Count);
        Assert.Equal(Enumerable.Range(7, FeedConnection.LogLimit).Select(i => $"message {i}"), snapshot.Select(e => e.Preview));
        Assert.All(snapshot, e =>
        {
            Assert.Equal("Received", e.Direction);
            Assert.Equal("application/octet-stream", e.ContentType);
        });
        h.Connection.ClearLog();
        Assert.Empty(h.Connection.Entries);
        Assert.Equal(FeedConnection.LogLimit, snapshot.Count);
    }

    [Fact]
    public async Task Received_and_sent_logs_redact_raw_key_from_body_and_content_type()
    {
        await using var h = new ConnectionHarness();
        const string key = "test-only-secret";
        const string body = "before test-only-secret after test-only-secret";
        const string type = "text/plain; profile=\"test-only-secret\"";
        var poll = await h.ReadyAsync(key);
        poll.Reply(HttpStatusCode.OK, Encoding.UTF8.GetBytes(body), type);
        await h.Handler.NextAsync();
        var sending = h.Connection.PublishAsync(body, type);
        var post = await h.Handler.NextAsync();
        Assert.Equal(Encoding.UTF8.GetBytes(body), post.Body);
        Assert.Equal([type], post.ContentTypes);
        post.Reply(HttpStatusCode.OK, "Message distributed to 1 reader(s)."u8.ToArray());
        Assert.Equal(1, await sending);
        Assert.Equal(["Received", "Sent"], h.Connection.Entries.Select(e => e.Direction));
        Assert.All(h.Connection.Entries, e =>
        {
            Assert.DoesNotContain(key, e.ToString());
            Assert.Equal("before [redacted] after [redacted]", e.Preview);
            Assert.Equal("text/plain; profile=\"[redacted]\"", e.ContentType);
        });
    }

    [Fact]
    public async Task Error_log_redacts_payload_and_does_not_include_transport_exception()
    {
        await using var h = new ConnectionHarness();
        const string key = "test-only-secret";
        await h.ReadyAsync(key);
        var sending = h.Connection.PublishAsync(key, "text/plain");
        (await h.Handler.NextAsync()).Fail(new HttpRequestException("private exception " + key));
        Assert.Null(await sending);
        var entry = Assert.Single(h.Connection.Entries);
        Assert.Equal("Error", entry.Direction);
        Assert.Equal("[redacted]", entry.Preview);
        Assert.DoesNotContain(key, entry.ToString());
        Assert.DoesNotContain("private exception", entry.ToString());
    }

    [Fact]
    public async Task Reconnect_clears_previous_session_log()
    {
        await using var h = new ConnectionHarness();
        var poll = await h.ReadyAsync();
        poll.Reply(HttpStatusCode.OK, "old message"u8.ToArray());
        await h.Handler.NextAsync();
        Assert.Single(h.Connection.Entries);
        await h.ConnectAsync();
        await h.Handler.NextAsync();
        Assert.Empty(h.Connection.Entries);
    }
    [Fact]
    public async Task Repeated_short_keys_cannot_expand_log_previews_beyond_the_limit()
    {
        await using var h = new ConnectionHarness();
        var poll = await h.ReadyAsync("x");
        poll.Reply(HttpStatusCode.OK, Encoding.UTF8.GetBytes(new string('x', 1000)));
        await h.Handler.NextAsync();
        var preview = Assert.Single(h.Connection.Entries).Preview;
        Assert.Equal(CommunicationEntry.PreviewLimit + " … [truncated]".Length, preview.Length);
        Assert.DoesNotContain("x", preview);
        Assert.EndsWith(" … [truncated]", preview);
    }
}
