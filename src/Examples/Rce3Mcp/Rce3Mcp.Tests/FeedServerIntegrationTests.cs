using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FeedServer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace Rce3Mcp.Tests;

public sealed class FeedServerIntegrationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("raw integration test key")]
    public async Task RealFeedRegistersBeforePublishEchoesOnceAndRetainsOfflineQueue(string? authorization)
    {
        using var factory = new WebApplicationFactory<FeedServer.Program>().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(FeedServerDirectory());
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FeedServer:PollTimeout"] = "00:00:00.050"
            }));
        });
        factory.UseKestrel(0);
        using var feedClient = factory.CreateClient(new() { AllowAutoRedirect = false });
        feedClient.BaseAddress = new UriBuilder(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) { Host = "127.0.0.1" }.Uri;
        var feedId = Guid.NewGuid();
        var readerId = Guid.NewGuid();
        var feedUrl = new Uri(feedClient.BaseAddress!, feedId.ToString("D")).AbsoluteUri;
        var store = factory.Services.GetRequiredService<FeedStore>();
        Assert.Null(store.AuthorizeExisting(feedId, null));
        string oldCursor;
        using (var observed = new ForwardingHandler(new SocketsHttpHandler { AllowAutoRedirect = false }))
        using (var upstream = new HttpClient(observed) { Timeout = Timeout.InfiniteTimeSpan })
        {
            await using var bridge = await HostedBridge.StartAsync(upstream, readerId, feedUrl, authorization);
            await observed.NextPollAsync();
            await observed.NextPollAsync(); // A completed initial poll established the reader.
            Assert.True(bridge.App.Services.GetRequiredService<BridgeState>().CanSend, bridge.App.Services.GetRequiredService<BridgeState>().Snapshot().LastError);
            await using var mcp = await bridge.ConnectAsync();
            var before = await mcp.CallToolAsync("get_messages");
            oldCursor = before.StructuredContent!.Value.GetProperty("nextCursor").GetString()!;
            var sent = await mcp.CallToolAsync("send_message", new Dictionary<string, object?>
            {
                ["body"] = "hello\r\n世界", ["contentType"] = "text/custom;charset=utf-8;mode=exact"
            });
            Assert.False(sent.IsError, string.Join("; ", sent.Content.OfType<TextContentBlock>().Select(block => block.Text)));
            Assert.Equal(1, sent.StructuredContent!.Value.GetProperty("acceptedReaderCount").GetInt32());
            // Drain request signals until the received echo has been stored; each signal represents real completed work.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (bridge.App.Services.GetRequiredService<MessageHistory>().Statistics().Count == 0)
                await observed.NextPollAsync(deadline.Token);
            var page = await mcp.CallToolAsync("get_messages", new Dictionary<string, object?> { ["afterCursor"] = oldCursor });
            var entry = Assert.Single(page.StructuredContent!.Value.GetProperty("messages").EnumerateArray());
            Assert.Equal("hello\r\n世界", entry.GetProperty("body").GetString());
            Assert.Equal("text/custom;charset=utf-8;mode=exact", entry.GetProperty("contentType").GetString());
        }
        using (var post = new HttpRequestMessage(HttpMethod.Post, feedUrl) { Content = new ByteArrayContent([0, 255, 128]) })
        {
            if (authorization is not null) post.Headers.TryAddWithoutValidation("Authorization", authorization);
            using var accepted = await feedClient.SendAsync(post);
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            Assert.Equal("Message distributed to 1 reader(s).", await accepted.Content.ReadAsStringAsync());
        }
        using var restartedObserver = new ForwardingHandler(new SocketsHttpHandler { AllowAutoRedirect = false });
        using var restartedClient = new HttpClient(restartedObserver) { Timeout = Timeout.InfiniteTimeSpan };
        await using var restarted = await HostedBridge.StartAsync(restartedClient, readerId, feedUrl, authorization);
        await restartedObserver.NextPollAsync();
        await restartedObserver.NextPollAsync();
        await using var restartedMcp = await restarted.ConnectAsync();
        var retained = await restartedMcp.CallToolAsync("get_messages");
        var offlineEntry = Assert.Single(retained.StructuredContent!.Value.GetProperty("messages").EnumerateArray());
        Assert.Equal("AP+A", offlineEntry.GetProperty("body").GetString());
        var stale = await restartedMcp.CallToolAsync("get_messages", new Dictionary<string, object?> { ["afterCursor"] = oldCursor });
        Assert.True(stale.IsError);
        Assert.Contains("restarted", Assert.IsType<TextContentBlock>(stale.Content[0]).Text);
        var status = await restartedMcp.CallToolAsync("get_status");
        Assert.Equal(readerId.ToString(), status.StructuredContent!.Value.GetProperty("readerId").GetString());
        Assert.Equal(0, status.StructuredContent.Value.GetProperty("resetCount").GetInt64());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("raw integration test key")]
    public async Task FetchProxyUsesRealFeedAndIgnoresOtherProxysFailure(string? authorization)
    {
        const string target = "https://target.example/api";
        using var factory = new WebApplicationFactory<FeedServer.Program>().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(FeedServerDirectory());
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FeedServer:PollTimeout"] = "00:00:00.050"
            }));
        });
        factory.UseKestrel(0);
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.BaseAddress = new UriBuilder(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) { Host = "127.0.0.1" }.Uri;
        if (authorization is not null) client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authorization);
        var feed = new Uri(client.BaseAddress!, Guid.NewGuid().ToString("D"));
        var firstReader = new Uri(feed.AbsoluteUri + "/" + Guid.NewGuid().ToString("D"));
        var secondReader = new Uri(feed.AbsoluteUri + "/" + Guid.NewGuid().ToString("D"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // Confirm both simulated proxies exist before any command can be published.
        foreach (var reader in new[] { firstReader, secondReader })
        {
            using var registered = await client.GetAsync(reader, deadline.Token);
            Assert.Equal(HttpStatusCode.NoContent, registered.StatusCode);
        }
        using var upstream = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        await using var bridge = await HostedBridge.StartAsync(upstream, Guid.NewGuid(), feed.AbsoluteUri, authorization);
        await using var mcp = await bridge.ConnectAsync();
        var failurePublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failingProxy = AnswerAsync(firstReader,
            $"fetchproxyresponse: {target}\n{{\"error\":{{\"name\":\"TypeError\",\"message\":\"Failed to fetch\"}}}}", false);
        var successfulProxy = AnswerAsync(secondReader, FetchProxyTests.HttpReply(target, 404, "actual target response"), true);
        var result = await mcp.CallToolAsync("fetchproxy", new Dictionary<string, object?> { ["url"] = target }, cancellationToken: deadline.Token);
        await Task.WhenAll(failingProxy, successfulProxy);
        Assert.False(result.IsError, string.Join("; ", result.Content.OfType<TextContentBlock>().Select(block => block.Text)));
        Assert.Equal(404, result.StructuredContent!.Value.GetProperty("status").GetInt32());
        Assert.Equal("actual target response", result.StructuredContent.Value.GetProperty("body").GetString());
        var history = bridge.App.Services.GetRequiredService<MessageHistory>().Read();
        Assert.Equal(3, history.Messages.Count);
        Assert.Equal("fetchproxy: " + target, history.Messages[0].Body);
        Assert.Contains("Failed to fetch", history.Messages[1].Body);
        Assert.Contains("actual target response", history.Messages[2].Body);

        async Task AnswerAsync(Uri reader, string reply, bool waitForFailure)
        {
            while (true)
            {
                using var message = await client.GetAsync(reader, deadline.Token);
                if (message.StatusCode == HttpStatusCode.NoContent) continue;
                Assert.Equal(HttpStatusCode.OK, message.StatusCode);
                Assert.Equal("fetchproxy: " + target, await message.Content.ReadAsStringAsync(deadline.Token));
                break;
            }
            if (waitForFailure) await failurePublished.Task.WaitAsync(deadline.Token);
            using var posted = await client.PostAsync(feed, new StringContent(reply), deadline.Token);
            Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
            if (!waitForFailure) failurePublished.SetResult();
        }
    }

    private static string FeedServerDirectory([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../../FeedServer/FeedServer"));

    private sealed class ForwardingHandler(HttpMessageHandler target) : DelegatingHandler(target)
    {
        private readonly Channel<Uri> _polls = Channel.CreateUnbounded<Uri>();
        public async Task<Uri> NextPollAsync(CancellationToken cancellationToken = default) =>
            await _polls.Reader.ReadAsync(cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                Assert.DoesNotContain("/reset", request.RequestUri!.AbsolutePath);
                _polls.Writer.TryWrite(request.RequestUri);
            }
            return base.SendAsync(request, cancellationToken);
        }
    }
}
