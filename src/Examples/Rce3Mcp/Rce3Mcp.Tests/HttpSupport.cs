using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Rce3Mcp.Tests;

internal sealed class HostedBridge(WebApplication app) : IAsyncDisposable
{
    public WebApplication App { get; } = app;
    public Uri Endpoint => new(App.Urls.Single() + "/mcp");
    public static async Task<HostedBridge> StartAsync(HttpClient upstream, Guid readerId, string? feed = null, string? authorization = null)
    {
        var app = Program.CreateApplication([], builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RCE3_FEED"] = feed ?? "http://feed.test/00000000-0000-0000-0000-000000000000",
                ["RCE3_AUTH"] = authorization ?? "",
                ["urls"] = "http://127.0.0.1:0"
            });
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(upstream);
            builder.Services.AddSingleton(new ReaderIdentity(readerId));
        });
        try
        {
            await app.StartAsync();
            return new(app);
        }
        catch { await app.DisposeAsync(); throw; }
    }
    public async Task<McpClient> ConnectAsync(string? protocol = null)
    {
        var transport = new HttpClientTransport(new() { Endpoint = Endpoint, TransportMode = HttpTransportMode.StreamableHttp });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        return await McpClient.CreateAsync(transport, new() { ProtocolVersion = protocol }, cancellationToken: deadline.Token);
    }
    public async ValueTask DisposeAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await App.StopAsync(deadline.Token);
        await App.DisposeAsync();
    }
}
