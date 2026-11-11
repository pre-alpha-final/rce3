using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace RequestProxy.Tests;

internal sealed class TestDirectory : IAsyncDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("Request Proxy Tests ");
    public string Path => _directory.FullName;

    public async Task<string> WriteHarAsync(string fileName, params object[] requests)
    {
        var path = System.IO.Path.Combine(Path, fileName);
        var document = new { log = new { entries = requests.Select(request => new { request }) } };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(document), new UTF8Encoding(true));
        return path;
    }

    public static object Request(string url, object[]? headers = null, object[]? cookies = null) =>
        new { url, headers, cookies };

    public static object Header(string name, string value) => new { name, value };

    public async ValueTask DisposeAsync()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                _directory.Delete(recursive: true);
                return;
            }
            catch (Exception exception) when (attempt < 10 && exception is IOException or UnauthorizedAccessException)
            {
                // Windows can briefly retain file handles after a test process exits.
                await Task.Delay(100);
            }
        }
    }
}

internal sealed class LoopbackTarget(WebApplication app) : IAsyncDisposable
{
    public string Url => app.Urls.Single();

    public static async Task<LoopbackTarget> StartAsync(RequestDelegate handler, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.Run(handler);
        await app.StartAsync(cancellationToken);
        return new(app);
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }
}
