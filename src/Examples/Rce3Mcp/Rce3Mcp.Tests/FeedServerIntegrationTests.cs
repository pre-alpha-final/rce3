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
