using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TabletUI.Components;

namespace TabletUI.Tests;

public sealed class DebugPanelTests
{
    [Fact]
    public async Task Debug_renders_received_text_once_as_escaped_text()
    {
        await using var h = new ConnectionHarness();
        const string body = "<script>alert('x')</script>\r\n<img src=x onerror=alert(1)> & \"quoted\"";
        const string type = "text/plain; profile=\"<unsafe>&value\"";
        var poll = await h.ReadyAsync();
        poll.Reply(HttpStatusCode.OK, Encoding.UTF8.GetBytes(body), type);
        await h.Handler.NextAsync();

        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<DebugPanel>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { ["Connection"] = h.Connection }));
            return component.ToHtmlString();
        });
        var escapedBody = HtmlEncoder.Default.Encode(body);
        Assert.Contains("<pre>" + escapedBody + "</pre>", html);
        Assert.Equal(1, CountOccurrences(html, escapedBody));
        Assert.Contains(HtmlEncoder.Default.Encode(type), html);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<img src=x", html);
        Assert.DoesNotContain("&amp;lt;script", html);
        Assert.Contains("Received", html);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = 0; (index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length)
        {
            count++;
        }
        return count;
    }
}
