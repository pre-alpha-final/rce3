using System.Text;
using System.Text.Json;

namespace RequestProxy.Tests;

public sealed class ProtocolTests
{
    [Theory]
    [InlineData("requestproxy:  https://example.test/a  ", "https://example.test/a")]
    [InlineData("requestproxy: ", "")]
    [InlineData("RequestProxy: https://example.test/a", null)]
    [InlineData("requestproxy:https://example.test/a", null)]
    [InlineData("requestproxy: https://example.test/a\nhttps://example.test/b", null)]
    [InlineData("requestproxy_response: https://example.test/a\n{}", null)]
    public void OnlyTheExactSingleLineCommandPrefixIsRecognized(string message, string? expected)
    {
        Assert.Equal(expected, ProxyProtocol.ReadCommand(Encoding.UTF8.GetBytes(message)));
    }

    [Fact]
    public void InvalidUtf8IsIgnored()
    {
        Assert.Null(ProxyProtocol.ReadCommand([.. "requestproxy: "u8, 0xff]));
    }

    [Theory]
    [InlineData("/relative")]
    [InlineData("ftp://example.test/")]
    [InlineData("https://user:secret@example.test/")]
    [InlineData("https://example.test/#fragment")]
    public void InvalidTargetUrlsAreRejected(string value)
    {
        Assert.False(ProxyProtocol.TryParseUrl(value, out _));
    }

    [Fact]
    public void ReplyLimitIncludesEscapedJsonAndTheCorrelationLine()
    {
        const string url = "https://example.test/a";
        var prefix = $"requestproxy_response: {url}\n";
        var reply = ProxyProtocol.Reply(url, new { body = new string('\n', ProxyProtocol.MaxReplyBytes / 2) });
        Assert.True(reply.Length < ProxyProtocol.MaxReplyBytes);
        var text = Encoding.UTF8.GetString(reply);
        Assert.StartsWith(prefix, text);
        using var json = JsonDocument.Parse(text[prefix.Length..]);
        Assert.Equal("ResponseTooLarge", json.RootElement.GetProperty("error").GetProperty("name").GetString());
    }

    [Fact]
    public async Task BodyReadStopsAtTheByteLimit()
    {
        using var atLimit = new ByteArrayContent(new byte[8192]);
        using var aboveLimit = new ByteArrayContent(new byte[8193]);
        Assert.Equal(8192, (await ProxyProtocol.ReadBoundedAsync(atLimit, 8192, CancellationToken.None))!.Length);
        Assert.Null(await ProxyProtocol.ReadBoundedAsync(aboveLimit, 8192, CancellationToken.None));
    }
}
