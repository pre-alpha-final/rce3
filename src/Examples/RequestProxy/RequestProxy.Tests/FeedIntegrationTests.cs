using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static RequestProxy.Tests.TestDirectory;

namespace RequestProxy.Tests;

public sealed class FeedIntegrationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("raw integration test key")]
    public async Task ExecutableResetsRetainedRequestsAndAcceptsLaterHarDrops(string? authorization)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var token = deadline.Token;
        await using var files = new TestDirectory();
        var observed = new ConcurrentQueue<string>();
        var holdStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var target = await LoopbackTarget.StartAsync(async context =>
        {
            observed.Enqueue(context.Request.Path.ToString());
            if (context.Request.Path == "/hold")
            {
                holdStarted.TrySetResult();
                await releaseHold.Task.WaitAsync(context.RequestAborted);
            }

            await context.Response.WriteAsync(context.Request.Headers.Authorization.ToString());
        }, token);

        using var factory = CreateFeed();
        var address = factory.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(address)
        };
        if (authorization is not null)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authorization);
        }

        var feed = new Uri(client.BaseAddress, Guid.NewGuid().ToString("D"));
        var readerId = Guid.NewGuid();
        var reader = new Uri($"{feed}/{readerId:D}");
        var observer = new Uri($"{feed}/{Guid.NewGuid():D}");
        await ResetAsync(client, reader, token);
        await PostAsync(client, feed, $"requestproxy: {target.Url}/queued-while-offline", token);
        await ResetAsync(client, observer, token);

        var firstHar = await files.WriteHarAsync("initial login.har",
            Request(target.Url + "/old", [Header("Authorization", "Bearer original")]));
        var secondHar = await files.WriteHarAsync("refreshed login.har",
            Request(target.Url + "/new", [Header("Authorization", "Bearer refreshed")]));
        var extraHar = await files.WriteHarAsync("additional origin.har",
            Request("http://example.test/", [Header("Authorization", "Bearer additional")]));
        var readerFile = Path.Combine(files.Path, "readerid");
        await File.WriteAllTextAsync(readerFile, readerId.ToString("D"), token);
        var executable = CopyExecutable(files.Path);
        await using var primary = RunningProxy.Start(executable, feed.AbsoluteUri, authorization, files.Path, [firstHar]);
        await primary.WaitForLinesAsync(token, "Reader ready.");

        await RequestAndAssertReplyAsync(client, feed, observer, target.Url + "/first", "Bearer original", token);
        Assert.Equal(readerId.ToString("D"), await File.ReadAllTextAsync(readerFile, token));
        Assert.Equal(new[] { "/first" }, observed);

        try
        {
            var held = target.Url + "/hold";
            await PostAsync(client, feed, $"requestproxy: {held}", token);
            await holdStarted.Task.WaitAsync(token);

            // A later launch can queue the paths while the old session is in use.
            // Only the primary reads the feed configuration.
            var workingDirectory = Path.GetDirectoryName(files.Path)!;
            await using (var secondary = RunningProxy.Start(executable, "invalid", null, workingDirectory,
                [Path.GetRelativePath(workingDirectory, secondHar), Path.GetRelativePath(workingDirectory, extraHar)]))
            {
                await secondary.WaitForLinesAsync(token, "HAR files queued in the running Request Proxy.");
                Assert.Equal(0, await secondary.WaitForExitAsync(token));
            }

            Assert.False(primary.HasExited);
            // The primary cannot drain its two-slot queue until the held GET completes.
            for (var index = 0; index < 3; index++)
            {
                await PostAsync(client, feed, "unrelated message", token);
            }

            await ResetAsync(client, observer, token);
            releaseHold.SetResult();
            await primary.WaitForLinesAsync(token, "Reader ready.", "Imported 1 origin(s) from HAR file 2.");
            await AssertReplyAsync(client, observer, held, "Bearer original", token);
            await RequestAndAssertReplyAsync(client, feed, observer, target.Url + "/after-overflow", "Bearer refreshed", token);
        }
        finally
        {
            releaseHold.TrySetResult();
        }

        Assert.Equal(new[] { "/first", "/hold", "/after-overflow" }, observed);
        Assert.Equal(readerId.ToString("D"), await File.ReadAllTextAsync(readerFile, token));
    }

    private static WebApplicationFactory<FeedServer.Program> CreateFeed()
    {
        var factory = new WebApplicationFactory<FeedServer.Program>().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(FeedServerDirectory());
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FeedServer:PollTimeout"] = "00:00:00.150",
                ["FeedServer:MaxQueuedMessagesPerReader"] = "2"
            }));
        });
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        return factory;
    }

    private static string FeedServerDirectory([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../../FeedServer/FeedServer"));

    private static async Task ResetAsync(HttpClient client, Uri reader, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync($"{reader}/reset", cancellationToken);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
    }

    private static async Task PostAsync(HttpClient client, Uri feed, string text, CancellationToken cancellationToken)
    {
        using var body = new StringContent(text, Encoding.UTF8);
        using var response = await client.PostAsync(feed, body, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task RequestAndAssertReplyAsync(HttpClient client, Uri feed, Uri observer, string url,
        string expectedBody, CancellationToken cancellationToken)
    {
        var command = $"requestproxy: {url}";
        await PostAsync(client, feed, command, cancellationToken);
        using (var echoedCommand = await client.GetAsync(observer, cancellationToken))
        {
            Assert.Equal(command, await echoedCommand.Content.ReadAsStringAsync(cancellationToken));
        }

        await AssertReplyAsync(client, observer, url, expectedBody, cancellationToken);
        using var empty = await client.GetAsync(observer, cancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, empty.StatusCode);
    }

    private static async Task AssertReplyAsync(HttpClient client, Uri observer, string url, string expectedBody,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            using var response = await client.GetAsync(observer, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                continue;
            }

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(new MediaTypeHeaderValue("text/plain") { CharSet = "utf-8" }, response.Content.Headers.ContentType);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            var prefix = $"requestproxy_response: {url}\n";
            Assert.StartsWith(prefix, text);
            using var json = JsonDocument.Parse(text[prefix.Length..]);
            Assert.Equal(200, json.RootElement.GetProperty("status").GetInt32());
            Assert.Equal(expectedBody, json.RootElement.GetProperty("body").GetString());
            return;
        }
    }

    private static string CopyExecutable(string destination)
    {
        foreach (var name in new[] { "RequestProxy.exe", "RequestProxy.dll", "RequestProxy.deps.json", "RequestProxy.runtimeconfig.json" })
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, name), Path.Combine(destination, name));
        }

        return Path.Combine(destination, "RequestProxy.exe");
    }

    private sealed class RunningProxy : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _errors;
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();

        private RunningProxy(Process process)
        {
            _process = process;
            _errors = process.StandardError.ReadToEndAsync();
            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data is { } line)
                {
                    _lines.Writer.TryWrite(line);
                }
                else
                {
                    _lines.Writer.TryComplete();
                }
            };
            process.BeginOutputReadLine();
        }

        public bool HasExited => _process.HasExited;

        public static RunningProxy Start(string executable, string feed, string? authorization, string workingDirectory,
            string[] paths)
        {
            var info = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDirectory
            };
            info.Environment["RCE3_FEED"] = feed;
            if (authorization is null)
            {
                info.Environment.Remove("RCE3_AUTH");
            }
            else
            {
                info.Environment["RCE3_AUTH"] = authorization;
            }

            foreach (var path in paths)
            {
                info.ArgumentList.Add(path);
            }

            return new(Process.Start(info)!);
        }

        public async Task WaitForLinesAsync(CancellationToken cancellationToken, params string[] expected)
        {
            var pending = expected.ToHashSet();
            await foreach (var line in _lines.Reader.ReadAllAsync(cancellationToken))
            {
                if (pending.Remove(line) && pending.Count == 0)
                {
                    return;
                }
            }

            throw new InvalidOperationException($"Request Proxy exited before reporting {string.Join(", ", pending)}. {await _errors}");
        }

        public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
        {
            await _process.WaitForExitAsync(cancellationToken);
            return _process.ExitCode;
        }

        public async ValueTask DisposeAsync()
        {
            using (_process)
            {
                if (!_process.HasExited)
                {
                    _process.Kill();
                }

                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }
}
