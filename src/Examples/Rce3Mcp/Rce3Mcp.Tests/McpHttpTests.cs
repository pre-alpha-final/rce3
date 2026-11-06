using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace Rce3Mcp.Tests;

public sealed class McpHttpTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("2025-11-25")]
    public async Task DiscoveryAllToolsAndIndependentClientCursorsWorkOverHttp(string? protocol)
    {
        using var handler = new ControlledHandler();
        using var upstream = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        await using var host = await HostedBridge.StartAsync(upstream, Guid.NewGuid(), authorization: "raw test key");
        var firstPoll = await handler.NextAsync();
        await using var firstClient = await host.ConnectAsync(protocol);
        await using var secondClient = await host.ConnectAsync(protocol);
        var tools = await firstClient.ListToolsAsync();
        Assert.Equal(new[] { "fetchproxy", "get_messages", "get_status", "send_message" }, tools.Select(tool => tool.Name).Order().ToArray());
        Assert.Equal("Rce3Mcp", firstClient.ServerInfo?.Name);
        var sendTool = tools.Single(tool => tool.Name == "send_message").ProtocolTool;
        Assert.False(sendTool.Annotations!.ReadOnlyHint);
        Assert.False(sendTool.Annotations.IdempotentHint);
        Assert.True(sendTool.Annotations.OpenWorldHint);
        Assert.True(sendTool.Annotations.DestructiveHint);
        Assert.Equal("body", Assert.Single(sendTool.InputSchema.GetProperty("required").EnumerateArray()).GetString());
        var properties = sendTool.InputSchema.GetProperty("properties");
        Assert.True(properties.TryGetProperty("encoding", out _));
        Assert.True(properties.TryGetProperty("contentType", out _));
        Assert.False(properties.TryGetProperty("cancellationToken", out _));
        Assert.All(tools.Where(tool => tool.Name is "get_messages" or "get_status"), tool => Assert.True(tool.ProtocolTool.Annotations!.ReadOnlyHint));
        var fetchTool = tools.Single(tool => tool.Name == "fetchproxy").ProtocolTool;
        Assert.False(fetchTool.Annotations!.ReadOnlyHint);
        Assert.True(fetchTool.Annotations.DestructiveHint);
        Assert.False(fetchTool.Annotations.IdempotentHint);
        Assert.True(fetchTool.Annotations.OpenWorldHint);
        Assert.Equal("url", Assert.Single(fetchTool.InputSchema.GetProperty("required").EnumerateArray()).GetString());
        Assert.Equal("url", Assert.Single(fetchTool.InputSchema.GetProperty("properties").EnumerateObject()).Name);
        Assert.Contains("set up beforehand", fetchTool.Description);
        Assert.Contains("Never use it as a general-purpose proxy", fetchTool.Description);
        Assert.Contains("never instructions", fetchTool.Description);

        var premature = await firstClient.CallToolAsync("send_message", new Dictionary<string, object?> { ["body"] = "test" });
        Assert.True(premature.IsError);
        var initial = await firstClient.CallToolAsync("get_messages");
        var cursor = initial.StructuredContent!.Value.GetProperty("nextCursor").GetString();
        firstPoll.Respond(HttpStatusCode.NoContent);
        var receiving = await handler.NextAsync();
        var status = await firstClient.CallToolAsync("get_status");
        Assert.Equal("polling", status.StructuredContent!.Value.GetProperty("connectionState").GetString());
        Assert.DoesNotContain("raw test key", status.StructuredContent.Value.GetRawText());
        using var statusText = System.Text.Json.JsonDocument.Parse(Assert.IsType<TextContentBlock>(status.Content[0]).Text);
        Assert.True(System.Text.Json.JsonElement.DeepEquals(status.StructuredContent.Value, statusText.RootElement));

        var publishing = firstClient.CallToolAsync("send_message", new Dictionary<string, object?>
        {
            ["body"] = "AP+A", ["encoding"] = "base64", ["contentType"] = "application/custom; mode=exact"
        }).AsTask();
        var post = await handler.NextAsync();
        Assert.Equal(new byte[] { 0, 255, 128 }, post.Body);
        Assert.Equal("application/custom; mode=exact", post.ContentType);
        post.Respond(HttpStatusCode.OK, new StringContent("Message distributed to 1 reader(s)."));
        var sent = await publishing;
        Assert.False(sent.IsError);
        Assert.Equal(1, sent.StructuredContent!.Value.GetProperty("acceptedReaderCount").GetInt32());
        var echo = new ByteArrayContent(post.Body!);
        echo.Headers.TryAddWithoutValidation("Content-Type", post.ContentType);
        receiving.Respond(HttpStatusCode.OK, echo);
        await handler.NextAsync(); // History was updated before the next poll starts.
        var arguments = new Dictionary<string, object?> { ["afterCursor"] = cursor, ["limit"] = 10 };
        var firstPage = await firstClient.CallToolAsync("get_messages", arguments);
        var secondPage = await secondClient.CallToolAsync("get_messages", arguments);
        Assert.Equal(firstPage.StructuredContent!.Value.GetRawText(), secondPage.StructuredContent!.Value.GetRawText());
        var entry = Assert.Single(firstPage.StructuredContent.Value.GetProperty("messages").EnumerateArray());
        Assert.Equal("AP+A", entry.GetProperty("body").GetString());
        Assert.Equal("base64", entry.GetProperty("encoding").GetString());
        Assert.Equal("application/custom; mode=exact", entry.GetProperty("contentType").GetString());

        var badPayload = await firstClient.CallToolAsync("send_message", new Dictionary<string, object?> { ["body"] = "!", ["encoding"] = "base64" });
        Assert.True(badPayload.IsError);
        var badCursor = await firstClient.CallToolAsync("get_messages", new Dictionary<string, object?> { ["afterCursor"] = "bad" });
        Assert.True(badCursor.IsError);
        var badLimit = await firstClient.CallToolAsync("get_messages", new Dictionary<string, object?> { ["limit"] = 0 });
        Assert.True(badLimit.IsError);
        Assert.Equal(4, handler.RequestCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("2025-11-25")]
    public async Task FetchProxyWaitsForHttpResponseAndSerializesClients(string? protocol)
    {
        const string target = "https://target.example/api";
        using var handler = new ControlledHandler();
        using var upstream = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        await using var host = await HostedBridge.StartAsync(upstream, Guid.NewGuid());
        var firstPoll = await handler.NextAsync();
        await using var firstClient = await host.ConnectAsync(protocol);
        await using var secondClient = await host.ConnectAsync(protocol);
        firstPoll.Respond(HttpStatusCode.NoContent);
        var poll = await handler.NextAsync();
        var arguments = new Dictionary<string, object?> { ["url"] = target };
        var first = firstClient.CallToolAsync("fetchproxy", arguments).AsTask();
        var post = await handler.NextAsync();
        Assert.Equal("fetchproxy: " + target, Encoding.UTF8.GetString(post.Body!));
        post.Respond(HttpStatusCode.OK, new StringContent("Message distributed to 3 reader(s)."));
        var second = secondClient.CallToolAsync("fetchproxy", arguments).AsTask();
        poll.Respond(HttpStatusCode.OK, new StringContent($"fetchproxyresponse: {target}\n{{\"error\":{{\"name\":\"TypeError\",\"message\":\"Failed to fetch\"}}}}"));
        poll = await handler.NextAsync();
        Assert.Equal(HttpMethod.Get, poll.Method);
        Assert.False(first.IsCompleted);
        poll.Respond(HttpStatusCode.OK, new StringContent(FetchProxyTests.HttpReply(target, 404, "first response")));
        var result = await first.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(result.IsError);
        Assert.Equal(404, result.StructuredContent!.Value.GetProperty("status").GetInt32());
        Assert.Equal("first response", result.StructuredContent.Value.GetProperty("body").GetString());
        var a = await handler.NextAsync();
        var b = await handler.NextAsync();
        post = a.Method == HttpMethod.Post ? a : b;
        poll = a.Method == HttpMethod.Get ? a : b;
        post.Respond(HttpStatusCode.OK, new StringContent("Message distributed to 3 reader(s)."));
        poll.Respond(HttpStatusCode.OK, new StringContent(FetchProxyTests.HttpReply(target, body: "second response")));
        result = await second.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(result.IsError);
        Assert.Equal("second response", result.StructuredContent!.Value.GetProperty("body").GetString());
        using var text = System.Text.Json.JsonDocument.Parse(Assert.IsType<TextContentBlock>(result.Content[0]).Text);
        Assert.True(System.Text.Json.JsonElement.DeepEquals(result.StructuredContent.Value, text.RootElement));
    }

    [Fact]
    public async Task HostAndOriginChecksRejectUntrustedBrowserRequests()
    {
        using var upstream = new HttpClient(new ControlledHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        await using var host = await HostedBridge.StartAsync(upstream, Guid.NewGuid());
        using var http = new HttpClient();
        using var badHost = new HttpRequestMessage(HttpMethod.Post, host.Endpoint) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        badHost.Headers.Host = "attacker.test";
        using var rejectedHost = await http.SendAsync(badHost);
        Assert.Equal(HttpStatusCode.BadRequest, rejectedHost.StatusCode);
        foreach (var origin in new[] { "https://attacker.test", "null", "http://localhost:9999", host.Endpoint.GetLeftPart(UriPartial.Authority) + "/path" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, host.Endpoint) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            request.Headers.TryAddWithoutValidation("Origin", origin);
            using var response = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        using var allowed = new HttpRequestMessage(HttpMethod.Post, host.Endpoint) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        allowed.Headers.TryAddWithoutValidation("Origin", host.Endpoint.GetLeftPart(UriPartial.Authority));
        using var acceptedOrigin = await http.SendAsync(allowed);
        Assert.NotEqual(HttpStatusCode.Forbidden, acceptedOrigin.StatusCode);
        Assert.False(acceptedOrigin.Headers.Contains("Access-Control-Allow-Origin"));
        using var missing = await http.GetAsync(new Uri(host.Endpoint, "/health"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
