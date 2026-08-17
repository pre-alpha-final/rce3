using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace FeedServer.Tests;

public class FeedEndpointTests
{
    [Fact]
    public async Task Root_CreatesRandomFeedAndRedirectsToIt()
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);

        using var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        var feedPath = response.Headers.Location!.OriginalString.TrimStart('/');
        Assert.True(Guid.TryParse(feedPath, out _));

        await AssertStatusAsync(client.GetAsync(response.Headers.Location), HttpStatusCode.OK);
        await AssertStatusAsync(
            SendAsync(client, HttpMethod.Get, response.Headers.Location!.OriginalString, "unexpected-key"),
            HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Root_WithAuthorizationCreatesProtectedFeed()
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);

        using var response = await SendAsync(client, HttpMethod.Get, "/", "secret");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        await AssertStatusAsync(client.GetAsync(response.Headers.Location), HttpStatusCode.Unauthorized);
        await AssertStatusAsync(
            SendAsync(client, HttpMethod.Get, response.Headers.Location!.OriginalString, "secret"),
            HttpStatusCode.OK);
    }

    [Fact]
    public async Task MalformedRoutes_ReturnBadRequest()
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);
        var feedId = Guid.NewGuid();
        var readerId = Guid.NewGuid();

        await AssertStatusAsync(client.GetAsync("/not-a-guid"), HttpStatusCode.BadRequest);
        await AssertStatusAsync(client.GetAsync("/not-a-guid/admin"), HttpStatusCode.BadRequest);
        await AssertStatusAsync(client.GetAsync($"/{feedId}/not-a-guid"), HttpStatusCode.BadRequest);
        await AssertStatusAsync(client.GetAsync($"/{feedId}/not-a-guid/reset"), HttpStatusCode.BadRequest);
        await AssertStatusAsync(client.GetAsync($"/{feedId}/{readerId}/extra"), HttpStatusCode.BadRequest);
        await AssertStatusAsync(client.GetAsync($"/{feedId}/{readerId}/reset/extra"), HttpStatusCode.BadRequest);
        await AssertStatusAsync(client.DeleteAsync($"/{feedId}"), HttpStatusCode.BadRequest);
        await AssertStatusAsync(client.PostAsync($"/{feedId}/admin", Text("body")), HttpStatusCode.BadRequest);
        await AssertStatusAsync(client.GetAsync("/openapi/v1.json"), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task FeedAdmin_ReturnsUncachedHtmlWithCanonicalIds()
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);
        var feedId = Guid.NewGuid();

        using var firstResponse = await client.GetAsync($"/{feedId:N}/admin");
        using var secondResponse = await client.GetAsync($"/{feedId:N}/admin");
        var firstHtml = await firstResponse.Content.ReadAsStringAsync();
        var secondHtml = await secondResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal("text/html; charset=utf-8", firstResponse.Content.Headers.ContentType?.ToString());
        Assert.True(firstResponse.Headers.CacheControl?.NoStore);
        Assert.Contains($"data-feed-id=\"{feedId:D}\"", firstHtml, StringComparison.Ordinal);
        Assert.Contains("RCE3 feed admin", firstHtml, StringComparison.Ordinal);
        Assert.Contains("Authorization value", firstHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("__FEED_ID__", firstHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("__READER_ID__", firstHtml, StringComparison.Ordinal);
        Assert.NotEqual(firstHtml, secondHtml);
    }

    [Fact]
    public async Task FeedAdmin_DoesNotCreateFeedOrReadAuthorization()
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);
        var feedId = Guid.NewGuid();

        await AssertStatusAsync(
            SendAsync(client, HttpMethod.Get, $"/{feedId}/admin", "ignored-key"),
            HttpStatusCode.OK);
        await AssertStatusAsync(
            SendAsync(client, HttpMethod.Get, $"/{feedId}", "protected-key"),
            HttpStatusCode.OK);
        await AssertStatusAsync(client.GetAsync($"/{feedId}"), HttpStatusCode.Unauthorized);

        await AssertStatusAsync(client.GetAsync($"/{feedId}/admin"), HttpStatusCode.OK);
        await AssertStatusAsync(
            SendAsync(client, HttpMethod.Get, $"/{feedId}/admin", "wrong-key"),
            HttpStatusCode.OK);

        using var invalidAuthorizationRequest = new HttpRequestMessage(HttpMethod.Get, $"/{feedId}/admin");
        invalidAuthorizationRequest.Headers.TryAddWithoutValidation("Authorization", ["first", "second"]);
        using var invalidAuthorizationResponse = await client.SendAsync(invalidAuthorizationRequest);
        Assert.Equal(HttpStatusCode.OK, invalidAuthorizationResponse.StatusCode);

        var openFeedId = Guid.NewGuid();
        await AssertStatusAsync(client.GetAsync($"/{openFeedId}"), HttpStatusCode.OK);
        var store = (FeedStore)factory.Services.GetService(typeof(FeedStore))!;
        var activityBeforeAdmin = store.AuthorizeExisting(openFeedId, key: null)!.Feed!.LastActivityAt;

        await AssertStatusAsync(client.GetAsync($"/{openFeedId}/admin"), HttpStatusCode.OK);

        var activityAfterAdmin = store.AuthorizeExisting(openFeedId, key: null)!.Feed!.LastActivityAt;
        Assert.Equal(activityBeforeAdmin, activityAfterAdmin);
    }

    [Fact]
    public async Task OpenFeed_RejectsAuthorizationOnEveryEndpointShape()
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);
        var feedId = Guid.NewGuid();
        var readerId = Guid.NewGuid();

        await AssertStatusAsync(client.GetAsync($"/{feedId}"), HttpStatusCode.OK);

        await AssertStatusAsync(SendAsync(client, HttpMethod.Get, $"/{feedId}", "open-key"), HttpStatusCode.Forbidden);
        await AssertStatusAsync(SendAsync(client, HttpMethod.Post, $"/{feedId}", "open-key", Text("body")), HttpStatusCode.Forbidden);
        await AssertStatusAsync(SendAsync(client, HttpMethod.Get, $"/{feedId}/{readerId}", "open-key"), HttpStatusCode.Forbidden);
        await AssertStatusAsync(SendAsync(client, HttpMethod.Get, $"/{feedId}/{readerId}/reset", "open-key"), HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ProtectedFeed_RequiresMatchingAuthorizationOnEveryEndpointShape()
    {
        using var factory = CreateFactory(("FeedServer:PollTimeout", "00:00:00.010"));
        using var client = CreateClient(factory);
        var feedId = Guid.NewGuid();
        var readerId = Guid.NewGuid();

        await AssertStatusAsync(SendAsync(client, HttpMethod.Get, $"/{feedId}", "secret"), HttpStatusCode.OK);

        await AssertStatusAsync(client.GetAsync($"/{feedId}"), HttpStatusCode.Unauthorized);
        await AssertStatusAsync(SendAsync(client, HttpMethod.Get, $"/{feedId}", "wrong"), HttpStatusCode.Unauthorized);
        await AssertStatusAsync(SendAsync(client, HttpMethod.Post, $"/{feedId}", null, Text("body")), HttpStatusCode.Unauthorized);
        await AssertStatusAsync(SendAsync(client, HttpMethod.Post, $"/{feedId}", "wrong", Text("body")), HttpStatusCode.Unauthorized);
        await AssertStatusAsync(SendAsync(client, HttpMethod.Get, $"/{feedId}/{readerId}", null), HttpStatusCode.Unauthorized);
        await AssertStatusAsync(SendAsync(client, HttpMethod.Get, $"/{feedId}/{readerId}", "wrong"), HttpStatusCode.Unauthorized);
        await AssertStatusAsync(SendAsync(client, HttpMethod.Get, $"/{feedId}/{readerId}/reset", null), HttpStatusCode.Unauthorized);
        await AssertStatusAsync(SendAsync(client, HttpMethod.Get, $"/{feedId}/{readerId}/reset", "wrong"), HttpStatusCode.Unauthorized);

        await AssertRedirectAsync(SendAsync(client, HttpMethod.Get, $"/{feedId}/{readerId}/reset", "secret"), $"/{feedId:D}/{readerId:D}");
        await AssertStatusAsync(SendAsync(client, HttpMethod.Get, $"/{feedId}/{readerId}", "secret"), HttpStatusCode.NoContent);
        await AssertStatusAsync(SendAsync(client, HttpMethod.Post, $"/{feedId}", "secret", Text("body")), HttpStatusCode.OK);
        Assert.Equal("body", await ReadStringAsync(client, feedId, readerId, "secret"));
    }

    [Fact]
    public async Task InvalidAuthorizationHeader_ReturnsUnauthorized()
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);
        var feedId = Guid.NewGuid();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/{feedId}");
        request.Headers.TryAddWithoutValidation("Authorization", ["first", "second"]);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertStatusAsync(client.GetAsync($"/{feedId}"), HttpStatusCode.OK);
    }

    [Fact]
    public async Task WhitespaceOnlyAuthorizationHeader_ReturnsUnauthorizedWithoutCreatingFeed()
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);
        var feedId = Guid.NewGuid();

        await AssertStatusAsync(
            SendAsync(client, HttpMethod.Get, $"/{feedId}", "   "),
            HttpStatusCode.Unauthorized);
        await AssertStatusAsync(client.GetAsync($"/{feedId}"), HttpStatusCode.OK);
    }

    [Fact]
    public async Task FeedHelp_ReportsCanonicalIdModeAndConfiguredLimits()
    {
        using var factory = CreateFactory(
            ("FeedServer:PollTimeout", "00:00:03"),
            ("FeedServer:FeedTtl", "02:00:00"),
            ("FeedServer:MaxMessageSizeBytes", "123"),
            ("FeedServer:MaxQueuedMessagesPerReader", "7"));
        using var client = CreateClient(factory);
        var feedId = Guid.NewGuid();

        using var response = await SendAsync(client, HttpMethod.Get, $"/{feedId:N}", "secret");
        var help = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains($"FeedId: {feedId:D}", help, StringComparison.Ordinal);
        Assert.Contains("Mode: protected", help, StringComparison.Ordinal);
        Assert.Contains("GET  /{FeedId}/admin", help, StringComparison.Ordinal);
        Assert.Contains("Max body: 123 bytes", help, StringComparison.Ordinal);
        Assert.Contains("after 00:00:03", help, StringComparison.Ordinal);
        Assert.Contains("exceeding 7 queued messages", help, StringComparison.Ordinal);
        Assert.Contains("expires after 2 hours", help, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedPostToProtectedFeed_EnforcesAuthorizationBeforePayloadLimit()
    {
        using var factory = CreateFactory(("FeedServer:MaxMessageSizeBytes", "5"));
        using var client = CreateClient(factory);
        var feedId = Guid.NewGuid();

        await AssertStatusAsync(SendAsync(client, HttpMethod.Get, $"/{feedId}", "secret"), HttpStatusCode.OK);

        await AssertStatusAsync(
            SendAsync(client, HttpMethod.Post, $"/{feedId}", "wrong", Text("123456")),
            HttpStatusCode.Unauthorized);
        await AssertStatusAsync(
            SendAsync(client, HttpMethod.Post, $"/{feedId}", null, Text("123456")),
            HttpStatusCode.Unauthorized);
        await AssertStatusAsync(
            SendAsync(client, HttpMethod.Post, $"/{feedId}", "secret", Text("123456")),
            HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task OversizedPostToOpenFeed_RejectsAuthorizationBeforePayloadLimit()
    {
        using var factory = CreateFactory(("FeedServer:MaxMessageSizeBytes", "5"));
        using var client = CreateClient(factory);
        var feedId = Guid.NewGuid();

        await AssertStatusAsync(client.GetAsync($"/{feedId}"), HttpStatusCode.OK);

        await AssertStatusAsync(
            SendAsync(client, HttpMethod.Post, $"/{feedId}", "key", Text("123456")),
            HttpStatusCode.Forbidden);
        await AssertStatusAsync(
            SendAsync(client, HttpMethod.Post, $"/{feedId}", null, Text("123456")),
            HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task PostedBody_IsDeliveredWithOriginalContentType()
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);
        var feedId = Guid.NewGuid();
        var readerId = Guid.NewGuid();
        var readerTask = client.GetAsync($"/{feedId}/{readerId}");
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        var body = new byte[] { 0, 1, 2, 3, 255 };
        using var content = new ByteArrayContent(body);
        const string contentType = "application/x-rce-test; version=3";
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);

        await AssertStatusAsync(client.PostAsync($"/{feedId}", content), HttpStatusCode.OK);
        using var response = await readerTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(contentType, response.Content.Headers.ContentType?.ToString());
        Assert.Equal(body, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task PostedBody_WithoutContentTypeUsesBinaryDefault()
    {
        using var factory = CreateFactory(("FeedServer:PollTimeout", "00:00:00.010"));
        using var client = CreateClient(factory);
        var feedId = Guid.NewGuid();
        var readerId = Guid.NewGuid();

        await AssertRedirectAsync(client.GetAsync($"/{feedId}/{readerId}/reset"), $"/{feedId:D}/{readerId:D}");
        using var content = new ByteArrayContent([0, 1, 2]);
        await AssertStatusAsync(client.PostAsync($"/{feedId}", content), HttpStatusCode.OK);
        using var response = await client.GetAsync($"/{feedId}/{readerId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(new byte[] { 0, 1, 2 }, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Readers_ReceiveIndependentQueuesOneMessageAtATime()
    {
        using var factory = CreateFactory(("FeedServer:PollTimeout", "00:00:00.010"));
        using var client = CreateClient(factory);
        var feedId = Guid.NewGuid();
        var firstReaderId = Guid.NewGuid();
        var secondReaderId = Guid.NewGuid();

        await AssertStatusAsync(client.GetAsync($"/{feedId}/{firstReaderId}"), HttpStatusCode.NoContent);
        await AssertStatusAsync(client.GetAsync($"/{feedId}/{secondReaderId}"), HttpStatusCode.NoContent);

        await AssertStatusAsync(client.PostAsync($"/{feedId}", Text("first")), HttpStatusCode.OK);
        await AssertStatusAsync(client.PostAsync($"/{feedId}", Text("second")), HttpStatusCode.OK);

        Assert.Equal("first", await ReadStringAsync(client, feedId, firstReaderId));
        Assert.Equal("second", await ReadStringAsync(client, feedId, firstReaderId));
        Assert.Equal("first", await ReadStringAsync(client, feedId, secondReaderId));
        Assert.Equal("second", await ReadStringAsync(client, feedId, secondReaderId));
    }

    [Fact]
    public async Task ReaderLongPoll_TimesOutWithNoContent()
    {
        using var factory = CreateFactory(("FeedServer:PollTimeout", "00:00:00.010"));
        using var client = CreateClient(factory);

        var response = await client.GetAsync($"/{Guid.NewGuid()}/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task BackgroundExpiration_CompletesPendingPollAndAllowsNewAccessMode()
    {
        using var factory = CreateFactory(
            ("FeedServer:PollTimeout", "00:00:05"),
            ("FeedServer:FeedTtl", "00:00:00.100"));
        using var client = CreateClient(factory);
        var feedId = Guid.NewGuid();
        var readerId = Guid.NewGuid();

        using var response = await SendAsync(
            client,
            HttpMethod.Get,
            $"/{feedId}/{readerId}",
            "secret").WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await AssertStatusAsync(client.GetAsync($"/{feedId}"), HttpStatusCode.OK);
    }

    [Fact]
    public async Task PayloadOverConfiguredLimit_ReturnsPayloadTooLarge()
    {
        using var factory = CreateFactory(("FeedServer:MaxMessageSizeBytes", "5"));
        using var client = CreateClient(factory);
        var feedId = Guid.NewGuid();

        await AssertStatusAsync(client.PostAsync($"/{feedId}", Text("12345")), HttpStatusCode.OK);
        await AssertStatusAsync(client.PostAsync($"/{feedId}", Text("123456")), HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task StreamingPayloadOverConfiguredLimit_DoesNotCreateFeed()
    {
        using var factory = CreateFactory(("FeedServer:MaxMessageSizeBytes", "5"));
        using var client = CreateClient(factory);
        var feedId = Guid.NewGuid();

        await AssertStatusAsync(
            SendAsync(client, HttpMethod.Post, $"/{feedId}", "secret", new UnknownLengthContent("123456")),
            HttpStatusCode.RequestEntityTooLarge);

        await AssertStatusAsync(client.GetAsync($"/{feedId}"), HttpStatusCode.OK);
    }

    [Fact]
    public async Task ReaderQueueOverflow_MarksReaderUnusable()
    {
        using var factory = CreateFactory(
            ("FeedServer:PollTimeout", "00:00:00.010"),
            ("FeedServer:MaxQueuedMessagesPerReader", "1"));
        using var client = CreateClient(factory);
        var feedId = Guid.NewGuid();
        var readerId = Guid.NewGuid();

        await AssertStatusAsync(client.GetAsync($"/{feedId}/{readerId}"), HttpStatusCode.NoContent);
        await AssertStatusAsync(client.PostAsync($"/{feedId}", Text("first")), HttpStatusCode.OK);
        await AssertStatusAsync(client.PostAsync($"/{feedId}", Text("second")), HttpStatusCode.OK);

        var response = await client.GetAsync($"/{feedId}/{readerId}");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var problem = await response.Content.ReadAsStringAsync();
        Assert.Contains("Reader queue exceeded", problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResetReader_ClearsQueuedMessages()
    {
        using var factory = CreateFactory(("FeedServer:PollTimeout", "00:00:00.010"));
        using var client = CreateClient(factory);
        var feedId = Guid.NewGuid();
        var readerId = Guid.NewGuid();

        await AssertStatusAsync(client.GetAsync($"/{feedId}/{readerId}"), HttpStatusCode.NoContent);
        await AssertStatusAsync(client.PostAsync($"/{feedId}", Text("before-reset")), HttpStatusCode.OK);

        await AssertRedirectAsync(client.GetAsync($"/{feedId}/{readerId}/reset"), $"/{feedId:D}/{readerId:D}");

        await AssertStatusAsync(client.GetAsync($"/{feedId}/{readerId}"), HttpStatusCode.NoContent);
        await AssertStatusAsync(client.PostAsync($"/{feedId}", Text("after-reset")), HttpStatusCode.OK);
        Assert.Equal("after-reset", await ReadStringAsync(client, feedId, readerId));
    }

    [Fact]
    public async Task ResetReader_CreatesMissingReader()
    {
        using var factory = CreateFactory(("FeedServer:PollTimeout", "00:00:00.010"));
        using var client = CreateClient(factory);
        var feedId = Guid.NewGuid();
        var readerId = Guid.NewGuid();

        await AssertRedirectAsync(client.GetAsync($"/{feedId}/{readerId}/reset"), $"/{feedId:D}/{readerId:D}");
        await AssertStatusAsync(client.PostAsync($"/{feedId}", Text("queued")), HttpStatusCode.OK);

        Assert.Equal("queued", await ReadStringAsync(client, feedId, readerId));
    }

    [Fact]
    public async Task ResetReader_MakesUnusableQueueUsableAgain()
    {
        using var factory = CreateFactory(
            ("FeedServer:PollTimeout", "00:00:00.010"),
            ("FeedServer:MaxQueuedMessagesPerReader", "1"));
        using var client = CreateClient(factory);
        var feedId = Guid.NewGuid();
        var readerId = Guid.NewGuid();

        await AssertStatusAsync(client.GetAsync($"/{feedId}/{readerId}"), HttpStatusCode.NoContent);
        await AssertStatusAsync(client.PostAsync($"/{feedId}", Text("first")), HttpStatusCode.OK);
        await AssertStatusAsync(client.PostAsync($"/{feedId}", Text("second")), HttpStatusCode.OK);
        await AssertStatusAsync(client.GetAsync($"/{feedId}/{readerId}"), HttpStatusCode.InternalServerError);

        await AssertRedirectAsync(client.GetAsync($"/{feedId}/{readerId}/reset"), $"/{feedId:D}/{readerId:D}");

        await AssertStatusAsync(client.GetAsync($"/{feedId}/{readerId}"), HttpStatusCode.NoContent);
        await AssertStatusAsync(client.PostAsync($"/{feedId}", Text("third")), HttpStatusCode.OK);
        Assert.Equal("third", await ReadStringAsync(client, feedId, readerId));
    }

    private static WebApplicationFactory<Program> CreateFactory(params (string Key, string Value)[] overrides)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["FeedServer:PollTimeout"] = "00:00:00.250",
                    ["FeedServer:FeedTtl"] = "1.00:00:00",
                    ["FeedServer:MaxMessageSizeBytes"] = "1048576",
                    ["FeedServer:MaxQueuedMessagesPerReader"] = "1024"
                };

                foreach (var (key, value) in overrides)
                {
                    values[key] = value;
                }

                configuration.AddInMemoryCollection(values);
            });
        });
    }

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory)
    {
        return factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    private static async Task AssertStatusAsync(Task<HttpResponseMessage> responseTask, HttpStatusCode expected)
    {
        using var response = await responseTask;
        Assert.Equal(expected, response.StatusCode);
    }

    private static async Task AssertRedirectAsync(Task<HttpResponseMessage> responseTask, string expectedLocation)
    {
        using var response = await responseTask;
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(expectedLocation, response.Headers.Location?.OriginalString);
    }

    private static Task<string> ReadStringAsync(HttpClient client, Guid feedId, Guid readerId)
    {
        return ReadStringAsync(client, feedId, readerId, authorization: null);
    }

    private static async Task<string> ReadStringAsync(
        HttpClient client,
        Guid feedId,
        Guid readerId,
        string? authorization)
    {
        using var response = await SendAsync(client, HttpMethod.Get, $"/{feedId}/{readerId}", authorization);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string? authorization,
        HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        request.Content = content;
        return await client.SendAsync(request);
    }

    private static StringContent Text(string value)
    {
        return new StringContent(value, Encoding.UTF8, "text/plain");
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly byte[] body;

        public UnknownLengthContent(string value)
        {
            body = Encoding.UTF8.GetBytes(value);
            Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            return stream.WriteAsync(body).AsTask();
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
