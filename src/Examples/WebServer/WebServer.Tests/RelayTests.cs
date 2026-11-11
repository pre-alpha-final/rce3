using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WebServer.Tests;

public sealed class RelayTests
{
    private static readonly string Assets = Path.Combine(AppContext.BaseDirectory, "www");

    [Theory]
    [InlineData(null)]
    [InlineData("raw integration test key")]
    public async Task ServesBundledAssetsAndRejectsUnknownPathsAndMethods(string? authorization)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = deadline.Token;
        using var feed = new TestFeed(authorization);
        using var workerClient = FeedClient.CreateHttpClient();
        using var worker = await StartWorkerAsync(feed.Reader(workerClient), token);
        using var gatewayClient = FeedClient.CreateHttpClient();
        await using var gateway = await StartGatewayAsync(feed.Reader(gatewayClient), new ReadySignals(), token);
        using var browser = new HttpClient { BaseAddress = new Uri(gateway.Urls.Single()) };

        foreach (var (path, filename, contentType) in new[]
        {
            ("/", "index.html", "text/html; charset=utf-8"),
            ("/index.html", "index.html", "text/html; charset=utf-8"),
            ("/site.css?v=1", "site.css", "text/css; charset=utf-8"),
            ("/diagram.svg", "diagram.svg", "image/svg+xml")
        })
        {
            var expected = await File.ReadAllBytesAsync(Path.Combine(Assets, filename), token);
            using var response = await browser.GetAsync(path, token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(contentType, response.Content.Headers.ContentType?.ToString());
            Assert.Equal(expected, await response.Content.ReadAsByteArrayAsync(token));

            using var request = new HttpRequestMessage(HttpMethod.Head, path);
            using var head = await browser.SendAsync(request, token);
            Assert.Equal(HttpStatusCode.OK, head.StatusCode);
            Assert.Equal(contentType, head.Content.Headers.ContentType?.ToString());
            Assert.Equal(expected.Length, head.Content.Headers.ContentLength);
            Assert.Empty(await head.Content.ReadAsByteArrayAsync(token));
        }

        using var missing = await browser.GetAsync("/missing", token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var unsupported = await browser.PostAsync("/", new StringContent("ignored"), token);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, unsupported.StatusCode);
        Assert.Equal(new[] { "GET", "HEAD" }, unsupported.Content.Headers.Allow);

        var observer = feed.Reader(gatewayClient);
        await observer.ResetAsync(token);
        foreach (var path in new[] { "/../WebServer.Host.dll", "/.. /WebServer.Host.dll", "/..\\WebServer.Host.dll" })
        {
            var traversal = new RelayRequest(Guid.NewGuid(), path);
            await observer.PublishAsync(RelayProtocol.RequestType, RelayProtocol.Write(traversal), token);
            var denied = await NextMessageAsync<RelayReply>(observer, RelayProtocol.ReplyType, token);
            Assert.Equal(traversal.Id, denied.Id);
            Assert.Equal(404, denied.Status);
            Assert.Empty(denied.Body);
        }

        await gateway.StopAsync(token);
        await worker.StopAsync(token);
    }

    [Fact]
    public async Task ServesFilesAddedUnderWwwWhileTheHostIsRunning()
    {
        var assets = Directory.CreateTempSubdirectory("rce3-webserver-");
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var token = deadline.Token;
            using var feed = new TestFeed();
            using var upstream = FeedClient.CreateHttpClient();
            using var worker = await StartWorkerAsync(feed.Reader(upstream), token, assets.FullName);
            await using var gateway = await StartGatewayAsync(feed.Reader(upstream), new ReadySignals(), token);
            using var browser = new HttpClient { BaseAddress = new Uri(gateway.Urls.Single()) };

            var nested = assets.CreateSubdirectory("static");
            var script = "console.log('nested');"u8.ToArray();
            await File.WriteAllBytesAsync(Path.Combine(nested.FullName, "demo.. app.js"), script, token);
            using var response = await browser.GetAsync("/static/demo..%20app.js?v=2", token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/javascript", response.Content.Headers.ContentType?.ToString());
            Assert.Equal(script, await response.Content.ReadAsByteArrayAsync(token));

            byte[] payload = [0, 128, 255];
            await File.WriteAllBytesAsync(Path.Combine(nested.FullName, "asset.webserver-test"), payload, token);
            using var unknown = await browser.GetAsync("/static/asset.webserver-test", token);
            Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
            Assert.Equal("application/octet-stream", unknown.Content.Headers.ContentType?.ToString());
            Assert.Equal(payload, await unknown.Content.ReadAsByteArrayAsync(token));

            using var directory = await browser.GetAsync("/static", token);
            Assert.Equal(HttpStatusCode.NotFound, directory.StatusCode);
            await gateway.StopAsync(token);
            await worker.StopAsync(token);
        }
        finally
        {
            assets.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task MatchesOutOfOrderRepliesAndIgnoresUnrelatedTraffic()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = deadline.Token;
        using var feed = new TestFeed();
        using var upstream = FeedClient.CreateHttpClient();
        var responder = feed.Reader(upstream);
        await responder.ResetAsync(token);
        await using var gateway = await StartGatewayAsync(feed.Reader(upstream), new ReadySignals(), token);
        using var browser = new HttpClient { BaseAddress = new Uri(gateway.Urls.Single()) };

        var first = browser.GetAsync("/first", token);
        var second = browser.GetAsync("/second", token);
        var request = await NextMessageAsync<RelayRequest>(responder, RelayProtocol.RequestType, token);
        var other = await NextMessageAsync<RelayRequest>(responder, RelayProtocol.RequestType, token);
        Assert.NotEqual(request.Id, other.Id);

        var unrelated = new RelayReply(Guid.NewGuid(), 200, "text/plain", "wrong ID"u8.ToArray());
        await responder.PublishAsync(RelayProtocol.ReplyType, RelayProtocol.Write(unrelated), token);
        var incorrectType = unrelated with { Id = request.Id };
        await responder.PublishAsync("application/json", RelayProtocol.Write(incorrectType), token);
        await responder.PublishAsync(RelayProtocol.ReplyType, "not JSON"u8.ToArray(), token);
        await SendReplyAsync(responder, other, token);
        await SendReplyAsync(responder, request, token);

        using var firstResponse = await first;
        using var secondResponse = await second;
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Equal(new byte[] { 0, 255, 128 }, await firstResponse.Content.ReadAsByteArrayAsync(token));
        Assert.Equal(new byte[] { 128, 0, 255 }, await secondResponse.Content.ReadAsByteArrayAsync(token));
        await gateway.StopAsync(token);

        static Task SendReplyAsync(FeedClient feed, RelayRequest request, CancellationToken token)
        {
            byte[] body = request.Path == "/first" ? [0, 255, 128] : [128, 0, 255];
            return feed.PublishAsync(RelayProtocol.ReplyType, RelayProtocol.Write(
                new RelayReply(request.Id, 200, "application/octet-stream", body)), token);
        }
    }

    [Fact]
    public async Task TimesOutWithoutWorker()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = deadline.Token;
        using var feed = new TestFeed();
        using var upstream = FeedClient.CreateHttpClient();
        await using var gateway = await StartGatewayAsync(feed.Reader(upstream), new ReadySignals(), token);
        using var browser = new HttpClient { BaseAddress = new Uri(gateway.Urls.Single()) };

        using var response = await browser.GetAsync("/", token);
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        await gateway.StopAsync(token);
    }

    [Fact]
    public async Task FailsPendingRequestAndResetsAfterRealFeedOverflow()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = deadline.Token;
        using var feed = new TestFeed(queueLimit: 1);
        using var upstream = FeedClient.CreateHttpClient();
        var observer = feed.Reader(upstream);
        await observer.ResetAsync(token);
        using var hold = new HoldFirstPoll();
        using var heldClient = new HttpClient(hold) { Timeout = Timeout.InfiniteTimeSpan };
        using var workerClient = FeedClient.CreateHttpClient();
        var readiness = new ReadySignals();
        await using var gateway = await StartGatewayAsync(feed.Reader(heldClient), readiness, token);
        using var browser = new HttpClient { BaseAddress = new Uri(gateway.Urls.Single()) };
        await hold.PollStarted.Task.WaitAsync(token);

        var pending = browser.GetAsync("/", token);
        await NextMessageAsync<RelayRequest>(observer, RelayProtocol.RequestType, token);
        await observer.PublishAsync("text/plain", "overflow"u8.ToArray(), token);
        hold.ReleasePoll.SetResult();

        using var failed = await pending;
        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        await readiness.NextAsync(token);
        Assert.Equal(2, hold.ResetCount);

        using var worker = await StartWorkerAsync(feed.Reader(workerClient), token);
        using var recovered = await browser.GetAsync("/", token);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        await gateway.StopAsync(token);
        await worker.StopAsync(token);
    }

    private static async Task<IHost> StartWorkerAsync(FeedClient feed, CancellationToken token, string? assets = null)
    {
        var signals = new ReadySignals();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders().AddProvider(signals);
        var worker = WebServerApplication.BuildHost(builder, feed, assets ?? Assets);
        try
        {
            await worker.StartAsync(token);
            await signals.NextAsync(token);
            return worker;
        }
        catch
        {
            worker.Dispose();
            throw;
        }
    }

    private static async Task<WebApplication> StartGatewayAsync(FeedClient feed, ReadySignals signals, CancellationToken token)
    {
        var builder = WebApplication.CreateBuilder(["--urls", "http://127.0.0.1:0"]);
        builder.Logging.ClearProviders().AddProvider(signals);
        var gateway = WebServerApplication.BuildGateway(builder, feed);
        try
        {
            await gateway.StartAsync(token);
            await signals.NextAsync(token);
            return gateway;
        }
        catch
        {
            await gateway.DisposeAsync();
            throw;
        }
    }

    private static async Task<T> NextMessageAsync<T>(FeedClient reader, string mediaType, CancellationToken token) where T : class
    {
        while (true)
        {
            var poll = await reader.PollAsync(mediaType, token);
            Assert.False(poll.Overflow);
            if (poll.Message is not null)
                return Assert.IsType<T>(RelayProtocol.Read<T>(poll.Message));
        }
    }

    private sealed class TestFeed : IDisposable
    {
        private readonly WebApplicationFactory<FeedServer.Program> _factory;
        private readonly FeedOptions _options;

        public TestFeed(string? authorization = null, int queueLimit = 1024)
        {
            _factory = new WebApplicationFactory<FeedServer.Program>().WithWebHostBuilder(builder =>
            {
                builder.UseContentRoot(FeedServerDirectory());
                builder.ConfigureLogging(logging => logging.ClearProviders());
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["FeedServer:PollTimeout"] = "00:00:00.050",
                        ["FeedServer:MaxQueuedMessagesPerReader"] = queueLimit.ToString()
                    }));
            });
            _factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
            using var client = _factory.CreateClient();
            _options = new(new Uri(client.BaseAddress!, $"{Guid.NewGuid():D}"), authorization);
        }

        public FeedClient Reader(HttpClient client) => new(client, _options, Guid.NewGuid());

        public void Dispose() => _factory.Dispose();

        private static string FeedServerDirectory([CallerFilePath] string source = "") =>
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../../FeedServer/FeedServer"));
    }

    private sealed class ReadySignals : ILoggerProvider, ILogger
    {
        private readonly Channel<bool> _events = Channel.CreateUnbounded<bool>();

        public ValueTask<bool> NextAsync(CancellationToken token) => _events.Reader.ReadAsync(token);

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception) == "Reader ready.")
                _events.Writer.TryWrite(true);
        }

        public void Dispose() => _events.Writer.TryComplete();
    }

    private sealed class HoldFirstPoll() : DelegatingHandler(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        private int _pollCount;
        private int _resetCount;
        public int ResetCount => Volatile.Read(ref _resetCount);
        public TaskCompletionSource PollStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleasePoll { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/reset", StringComparison.Ordinal))
                    Interlocked.Increment(ref _resetCount);
                else if (Interlocked.Increment(ref _pollCount) == 1)
                {
                    PollStarted.SetResult();
                    await ReleasePoll.Task.WaitAsync(cancellationToken);
                }
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }
}
