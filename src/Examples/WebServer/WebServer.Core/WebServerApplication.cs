using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WebServer;

public static class WebServerApplication
{
    public static Task<int> RunGatewayAsync(string[] args)
    {
        var serviceHome = IsAzureAppService ? Environment.GetEnvironmentVariable("HOME") : null;
        // App Service can mount the published files read-only.
        var readerDirectory = string.IsNullOrEmpty(serviceHome)
            ? Path.Combine(AppContext.BaseDirectory, "gateway")
            : Path.Combine(serviceHome, "data", "WebServer", "gateway");

        return RunAsync(readerDirectory, async feed =>
        {
            await using var gateway = BuildGateway(WebApplication.CreateBuilder(args), feed);
            await gateway.RunAsync();
        });
    }

    public static Task<int> RunHostAsync(string[] args) =>
        RunAsync(Path.Combine(AppContext.BaseDirectory, "host"), async feed =>
        {
            var assets = Path.Combine(AppContext.BaseDirectory, "www");
            using var host = BuildHost(Host.CreateApplicationBuilder(args), feed, assets);
            await host.RunAsync();
        });

    private static bool IsAzureAppService =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME"));

    private static async Task<int> RunAsync(string readerDirectory, Func<FeedClient, Task> run)
    {
        FeedOptions options;
        Guid readerId;
        try
        {
            options = FeedOptions.Load(Environment.GetEnvironmentVariable("RCE3_FEED"),
                Environment.GetEnvironmentVariable("RCE3_AUTH"));
            readerId = ReaderIdentity.LoadOrCreate(readerDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }

        using var http = FeedClient.CreateHttpClient();
        await run(new FeedClient(http, options, readerId));
        return 0;
    }

    internal static WebApplication BuildGateway(WebApplicationBuilder builder, FeedClient feed)
    {
        var configuration = builder.Configuration;
        if (string.IsNullOrEmpty(configuration["urls"])
            && string.IsNullOrEmpty(configuration["http_ports"])
            && string.IsNullOrEmpty(configuration["https_ports"]))
        {
            var defaultUrl = IsAzureAppService
                && int.TryParse(configuration["PORT"], out var port)
                && port is > 0 and <= 65535
                    ? $"http://0.0.0.0:{port}"
                    : "http://127.0.0.1:5139";
            builder.WebHost.UseUrls(defaultUrl);
        }

        var gateway = new GatewayRelay(feed);
        builder.Services.AddHostedService(services => new FeedReader(feed, RelayProtocol.ReplyType,
            gateway.ReceiveReplyAsync, gateway.SetReaderReady, services.GetRequiredService<ILogger<FeedReader>>()));
        var app = builder.Build();
        app.Lifetime.ApplicationStopping.Register(() => gateway.SetReaderReady(false));
        app.Run(gateway.ForwardAsync);
        return app;
    }

    internal static IHost BuildHost(HostApplicationBuilder builder, FeedClient feed, string assets)
    {
        builder.Services.AddHostedService(services => new FeedReader(feed, RelayProtocol.RequestType,
            new SiteHost(feed, assets, services.GetRequiredService<ILogger<SiteHost>>()).ReceiveRequestAsync,
            readerReadyChanged: null, services.GetRequiredService<ILogger<FeedReader>>()));
        return builder.Build();
    }
}
