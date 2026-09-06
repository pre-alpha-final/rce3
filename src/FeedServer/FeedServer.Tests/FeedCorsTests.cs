using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FeedServer.Tests;

public class FeedCorsTests
{
    [Theory]
    [InlineData("POST", "")]
    [InlineData("GET", "")]
    [InlineData("GET", "/admin")]
    [InlineData("GET", "/{reader}")]
    [InlineData("GET", "/{reader}/reset")]
    public async Task Preflight_AllowsExplicitMethodsAndHeadersWithoutCreatingFeed(string method, string suffix)
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);
        var feedId = Guid.NewGuid();
        var path = $"/{feedId}" + suffix.Replace("{reader}", Guid.NewGuid().ToString());

        using var response = await PreflightAsync(client, path, method);

        AssertPreflight(response);
        Assert.Null(factory.Services.GetRequiredService<FeedStore>().AuthorizeExisting(feedId, key: null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Preflight_DoesNotAuthorizeTouchCreateReadOrResetReaders(bool protectedFeed)
    {
        var timeProvider = new ManualTimeProvider();
        using var factory = CreateFactory(timeProvider);
        using var client = CreateClient(factory);
        var feedId = Guid.NewGuid();
        var readerPath = $"/{feedId}/{Guid.NewGuid()}";
        var authorization = protectedFeed ? "cors-test-key" : null;
        using var reset = await SendAsync(client, HttpMethod.Get, readerPath + "/reset", authorization);
        Assert.Equal(HttpStatusCode.Redirect, reset.StatusCode);
        using var post = await SendAsync(client, HttpMethod.Post, $"/{feedId}", authorization, "queued");
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        var store = factory.Services.GetRequiredService<FeedStore>();
        // Inspect the existing state without touching its activity timestamp.
        var feed = store.AuthorizeExisting(feedId, ReadKey(authorization))!.Feed!;
        var activityBefore = feed.LastActivityAt;
        timeProvider.Advance(TimeSpan.FromMinutes(1));

        foreach (var (path, method) in new[]
        {
            ($"/{feedId}", "POST"),
            (readerPath, "GET"),
            (readerPath + "/reset", "GET"),
            ($"/{feedId}/{Guid.NewGuid()}", "GET"),
            ($"/{feedId}/{Guid.NewGuid()}/reset", "GET")
        })
        {
            using var response = await PreflightAsync(client, path, method);
            AssertPreflight(response);
            Assert.Equal(activityBefore, feed.LastActivityAt);
            Assert.Equal(1, feed.ReaderCount);
        }

        using var delivery = await SendAsync(client, HttpMethod.Get, readerPath, authorization);
        Assert.Equal(HttpStatusCode.OK, delivery.StatusCode);
        Assert.Equal("queued", await delivery.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("https://client.example")]
    [InlineData("http://localhost:5173")]
    [InlineData("null")]
    public async Task CrossOriginRequests_IncludeWildcardOnSuccessRedirectAndEmptyPoll(string origin)
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory, origin);
        var feedPath = $"/{Guid.NewGuid()}";
        var readerPath = $"{feedPath}/{Guid.NewGuid()}";
        const string authorization = "cors-test-key";

        foreach (var path in new[] { "/", readerPath + "/reset" })
        {
            using var response = await SendAsync(client, HttpMethod.Get, path, authorization);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            AssertWildcard(response);
        }

        foreach (var path in new[] { feedPath, feedPath + "/admin" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            AssertWildcard(response);
        }

        using var post = await SendAsync(client, HttpMethod.Post, feedPath, authorization, "message");
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        Assert.Equal("Message distributed to 1 reader(s).", await post.Content.ReadAsStringAsync());
        AssertWildcard(post);

        using var delivery = await SendAsync(client, HttpMethod.Get, readerPath, authorization);
        Assert.Equal(HttpStatusCode.OK, delivery.StatusCode);
        Assert.Equal("message", await delivery.Content.ReadAsStringAsync());
        Assert.Equal("application/x-cors-test; version=1", delivery.Content.Headers.ContentType?.ToString());
        AssertWildcard(delivery);

        using var emptyPoll = await SendAsync(client, HttpMethod.Get, readerPath, authorization);
        Assert.Equal(HttpStatusCode.NoContent, emptyPoll.StatusCode);
        AssertWildcard(emptyPoll);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge)]
    [InlineData(HttpStatusCode.Gone)]
    public async Task CrossOriginErrors_IncludeWildcardAndProblemDetails(HttpStatusCode expected)
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);
        var feedPath = $"/{Guid.NewGuid()}";
        var readerPath = $"{feedPath}/{Guid.NewGuid()}";
        var authorization = expected == HttpStatusCode.Unauthorized ? "cors-test-key" : null;
        using var reset = await SendAsync(client, HttpMethod.Get, readerPath + "/reset", authorization);
        Assert.Equal(HttpStatusCode.Redirect, reset.StatusCode);

        if (expected == HttpStatusCode.Gone)
        {
            for (var i = 0; i < 2; i++)
            {
                using var post = await SendAsync(client, HttpMethod.Post, feedPath, body: "queued");
                Assert.Equal(HttpStatusCode.OK, post.StatusCode);
            }
        }

        using var response = expected switch
        {
            HttpStatusCode.BadRequest => await client.GetAsync("/not-a-guid"),
            HttpStatusCode.Unauthorized => await client.GetAsync(readerPath),
            HttpStatusCode.Forbidden => await SendAsync(client, HttpMethod.Post, feedPath, "unexpected-key", "body"),
            HttpStatusCode.RequestEntityTooLarge => await SendAsync(client, HttpMethod.Post, feedPath, body: new string('x', 33)),
            HttpStatusCode.Gone => await client.GetAsync(readerPath),
            _ => throw new InvalidOperationException()
        };

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        AssertWildcard(response);
    }

    [Fact]
    public async Task RequestsWithoutOrigin_DoNotIncludeCorsHeaders()
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);
        client.DefaultRequestHeaders.Remove("Origin");

        using var response = await client.GetAsync($"/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    private static WebApplicationFactory<Program> CreateFactory(TimeProvider? timeProvider = null)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["FeedServer:PollTimeout"] = "00:00:00.010",
                    ["FeedServer:FeedTtl"] = "1.00:00:00",
                    ["FeedServer:MaxMessageSizeBytes"] = "32",
                    ["FeedServer:MaxQueuedMessagesPerReader"] = "1"
                }));
            if (timeProvider is not null)
            {
                builder.ConfigureServices(services => services.AddSingleton(timeProvider));
            }
        });
    }

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory, string origin = "https://client.example")
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", origin);
        return client;
    }

    private static async Task<HttpResponseMessage> PreflightAsync(HttpClient client, string path, string method)
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Access-Control-Request-Method", method);
        request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string path, string? authorization = null, string? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/x-cors-test; version=1");
        }

        return await client.SendAsync(request);
    }

    private static void AssertWildcard(HttpResponseMessage response)
    {
        Assert.Equal("*", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    private static void AssertPreflight(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        AssertWildcard(response);
        Assert.Equal(new[] { "GET", "POST" }, HeaderValues(response, "Access-Control-Allow-Methods"));
        Assert.Equal(new[] { "authorization", "content-type" },
            HeaderValues(response, "Access-Control-Allow-Headers").Select(value => value.ToLowerInvariant()));
    }

    private static string[] HeaderValues(HttpResponseMessage response, string name)
    {
        return string.Join(",", response.Headers.GetValues(name)).Split(',', StringSplitOptions.TrimEntries);
    }

    private static FeedAuthorizationKey? ReadKey(string? value)
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        if (value is not null)
        {
            context.Request.Headers.Authorization = value;
        }

        Assert.True(FeedAuthorizationKey.TryRead(context.Request, out var key, out _));
        return key;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset utcNow = new(2026, 9, 6, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration) => utcNow += duration;
    }
}
