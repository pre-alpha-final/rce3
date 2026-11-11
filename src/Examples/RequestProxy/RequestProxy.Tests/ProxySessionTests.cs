using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using static RequestProxy.Tests.TestDirectory;

namespace RequestProxy.Tests;

public sealed class ProxySessionTests
{
    [Fact]
    public async Task SelectsMostSpecificHeadersAndUsesExactOrigin()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = deadline.Token;
        await using var files = new TestDirectory();
        var requests = 0;
        await using var target = await LoopbackTarget.StartAsync(async context =>
        {
            Interlocked.Increment(ref requests);
            context.Response.StatusCode = 404;
            context.Response.Headers.Append("X-Example", "one");
            context.Response.Headers.Append("X-Example", "two");
            await context.Response.WriteAsync(string.Join("|",
                context.Request.Method, context.Request.Headers.Authorization,
                context.Request.Headers["X-Api-Key"], context.Request.Headers["X-Hop"],
                context.Request.Headers.Referer, context.Request.Headers.IfNoneMatch));
        }, token);
        var origin = target.Url;
        var path = await files.WriteHarAsync("headers.har",
            Request(origin + "/api/status?v=1", [
                Header("Authorization", "Bearer exact"),
                Header("X-Api-Key", "custom"),
                Header("Connection", "X-Hop"),
                Header("X-Hop", "discard"),
                Header("Referer", "https://example.test/"),
                Header("If-None-Match", "\"old\"")
            ]),
            Request(origin + "/api/status?v=2", [Header("Authorization", "Bearer same-path")]),
            Request(origin + "/api/v1/old", [Header("Authorization", "Bearer older")]),
            Request(origin + "/api/v1/list", [Header("Authorization", "Bearer prefix")]),
            Request(origin + "/api/v11/list", [Header("Authorization", "Bearer different-segment")]));
        using var sessions = new ProxySessions(TimeSpan.FromSeconds(5));
        Assert.Equal(1, await sessions.ImportAsync(path, token));

        var exact = await GetAsync(sessions, origin + "/api/status?v=1", token);
        Assert.Equal("GET|Bearer exact|custom|||", exact.GetProperty("body").GetString());
        Assert.Equal(404, exact.GetProperty("status").GetInt32());
        Assert.Equal("Not Found", exact.GetProperty("statusText").GetString());
        Assert.False(exact.GetProperty("ok").GetBoolean());
        Assert.Equal(new[] { "one", "two" }, exact.GetProperty("headers").GetProperty("x-example").Deserialize<string[]>());
        Assert.Equal("GET|Bearer same-path||||", (await GetAsync(sessions, origin + "/api/status?v=3", token))
            .GetProperty("body").GetString());
        Assert.Equal("GET|Bearer prefix||||", (await GetAsync(sessions, origin + "/api/v1/detail", token))
            .GetProperty("body").GetString());

        var differentScheme = new UriBuilder(origin) { Scheme = "https" }.Uri;
        var differentHost = new UriBuilder(origin) { Host = "localhost" }.Uri;
        var differentPort = new UriBuilder(origin) { Port = 1 }.Uri;
        foreach (var address in new[] { differentScheme, differentHost, differentPort })
        {
            var missing = await GetAsync(sessions, address.AbsoluteUri, token);
            Assert.Equal("OriginNotImported", missing.GetProperty("error").GetProperty("name").GetString());
        }

        Assert.Equal(3, requests);
    }

    [Fact]
    public async Task CookiesRotateAndNewImportsReplaceOnlyTheirOrigins()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = deadline.Token;
        await using var files = new TestDirectory();
        await using var first = await LoopbackTarget.StartAsync(async context =>
        {
            if (context.Request.Path == "/rotate")
            {
                context.Response.Headers.Append("Set-Cookie", "sid=rotated; Path=/");
            }

            await context.Response.WriteAsync(context.Request.Headers.Cookie.ToString());
        }, token);
        await using var second = await LoopbackTarget.StartAsync(
            context => context.Response.WriteAsync(context.Request.Headers.Cookie.ToString()), token);
        var path = await files.WriteHarAsync("initial sessions.har",
            Request(first.Url + "/private/start", cookies: [new { name = "private", value = "yes", path = "/private" }]),
            Request(first.Url + "/rotate", [Header("Cookie", "sid=initial=a")]),
            Request(second.Url + "/start", [Header("Cookie", "sid=other")]));
        using var sessions = new ProxySessions(TimeSpan.FromSeconds(5));
        Assert.Equal(2, await sessions.ImportAsync(path, token));

        var rotation = await GetAsync(sessions, first.Url + "/rotate", token);
        Assert.Equal("sid=initial=a", rotation.GetProperty("body").GetString());
        Assert.False(rotation.GetProperty("headers").TryGetProperty("set-cookie", out _));
        var privateBody = (await GetAsync(sessions, first.Url + "/private/read", token)).GetProperty("body").GetString()!;
        Assert.Contains("sid=rotated", privateBody);
        Assert.Contains("private=yes", privateBody);
        Assert.Equal("sid=rotated", (await GetAsync(sessions, first.Url + "/public", token)).GetProperty("body").GetString());

        var malformed = Path.Combine(files.Path, "broken.har");
        await File.WriteAllTextAsync(malformed, """{"log": {"entries": [""", token);
        await Assert.ThrowsAsync<JsonException>(() => sessions.ImportAsync(malformed, token));
        var empty = await files.WriteHarAsync("no origins.har", Request("file:///example"));
        await Assert.ThrowsAsync<InvalidDataException>(() => sessions.ImportAsync(empty, token));
        Assert.Equal("sid=rotated", (await GetAsync(sessions, first.Url + "/public", token)).GetProperty("body").GetString());

        var refresh = await files.WriteHarAsync("replacement.har", Request(first.Url + "/new", cookies:
            [new { name = "sid", value = "fresh" }]));
        Assert.Equal(1, await sessions.ImportAsync(refresh, token));
        Assert.Equal("sid=fresh", (await GetAsync(sessions, first.Url + "/private/read", token)).GetProperty("body").GetString());
        Assert.Equal("sid=other", (await GetAsync(sessions, second.Url + "/new", token)).GetProperty("body").GetString());
    }

    [Fact]
    public async Task ReturnsRedirectWithoutFollowingItAndRejectsOversizedBodies()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = deadline.Token;
        await using var files = new TestDirectory();
        var followed = 0;
        await using var target = await LoopbackTarget.StartAsync(async context =>
        {
            if (context.Request.Path == "/redirect")
            {
                context.Response.Redirect("/destination");
            }
            else if (context.Request.Path == "/large")
            {
                await context.Response.Body.WriteAsync(new byte[ProxyProtocol.MaxReplyBytes + 1], context.RequestAborted);
            }
            else
            {
                Interlocked.Increment(ref followed);
            }
        }, token);
        using var sessions = new ProxySessions(TimeSpan.FromSeconds(5));
        await sessions.ImportAsync(await files.WriteHarAsync("redirect.har", Request(target.Url + "/redirect")), token);
        var redirect = await GetAsync(sessions, target.Url + "/redirect", token);
        Assert.Equal((int)HttpStatusCode.Found, redirect.GetProperty("status").GetInt32());
        Assert.Equal("/destination", redirect.GetProperty("headers").GetProperty("location")[0].GetString());
        Assert.Equal(0, followed);
        var large = await GetAsync(sessions, target.Url + "/large", token);
        Assert.Equal("ResponseTooLarge", large.GetProperty("error").GetProperty("name").GetString());
    }

    [Fact]
    public async Task DeadlineCoversTheResponseBody()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = deadline.Token;
        await using var files = new TestDirectory();
        var bodyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var target = await LoopbackTarget.StartAsync(async context =>
        {
            await context.Response.WriteAsync("partial");
            await context.Response.Body.FlushAsync(context.RequestAborted);
            bodyStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        }, token);
        using var sessions = new ProxySessions(TimeSpan.FromSeconds(2));
        await sessions.ImportAsync(await files.WriteHarAsync("slow.har", Request(target.Url + "/slow")), token);
        var pending = GetAsync(sessions, target.Url + "/slow", token);
        await bodyStarted.Task.WaitAsync(token);
        var response = await pending;
        Assert.Equal("TimeoutError", response.GetProperty("error").GetProperty("name").GetString());
    }

    private static async Task<JsonElement> GetAsync(ProxySessions sessions, string url, CancellationToken cancellationToken) =>
        JsonSerializer.SerializeToElement(await sessions.GetAsync(url, cancellationToken));
}
