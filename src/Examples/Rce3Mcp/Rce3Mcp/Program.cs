using System.Net;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;

namespace Rce3Mcp;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            await using var app = CreateApplication(args);
            await app.RunAsync();
            return 0;
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("Rce3Mcp could not start. Check listener configuration and access to the readerid file.");
            return 1;
        }
    }

    internal static WebApplication CreateApplication(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);
        var connection = FeedConnection.Load(builder.Configuration);
        var options = McpOptions.Load(builder.Configuration);
        var urls = builder.Configuration["urls"] ?? "http://localhost:5138";
        foreach (var url in urls.Split(';'))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || !uri.IsLoopback || uri.Host is not ("localhost" or "127.0.0.1" or "[::1]")
                || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/"
                || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            {
                throw new ArgumentException("MCP --urls must contain only loopback HTTP(S) listener URLs.");
            }
        }
        if (builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren().Any())
            throw new ArgumentException("Configure the local MCP listener with --urls; explicit Kestrel endpoints are not supported.");
        builder.WebHost.UseUrls(urls);
        builder.Configuration["AllowedHosts"] = "localhost;127.0.0.1;[::1]";
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 6L * options.MaxMessageSizeBytes + 65536);
        builder.Logging.AddFilter("ModelContextProtocol", LogLevel.Warning);
        builder.Services.AddSingleton(connection);
        builder.Services.AddSingleton(options);
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton(_ => new ReaderIdentity(ReaderIdentity.LoadOrCreate(AppContext.BaseDirectory)));
        builder.Services.TryAddSingleton(_ => new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip,
            ConnectTimeout = TimeSpan.FromSeconds(15)
        }) { Timeout = Timeout.InfiniteTimeSpan });
        builder.Services.AddSingleton<MessageHistory>();
        builder.Services.AddSingleton<BridgeState>();
        builder.Services.AddSingleton<FeedBridge>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<FeedBridge>());
        builder.Services.AddMcpServer(server =>
        {
            server.ServerInfo = new Implementation { Name = "Rce3Mcp", Version = "1.0.0" };
        }).WithHttpTransport(transport => transport.SessionMode = HttpServerSessionMode.Stateless)
            .WithTools<FeedTools>();

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            var origins = context.Request.Headers.Origin;
            if (origins.Count != 0 && (origins.Count != 1
                || !Uri.TryCreate(origins[0], UriKind.Absolute, out var origin)
                || origin.UserInfo.Length != 0 || origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0
                || !string.Equals(origin.Scheme, context.Request.Scheme, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(origin.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase)))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            await next(context);
        });
        app.MapMcp("/mcp");
        // Resolve before starting either HTTP or the worker: an invalid retained identity fails startup.
        _ = app.Services.GetRequiredService<ReaderIdentity>();
        return app;
    }
}
