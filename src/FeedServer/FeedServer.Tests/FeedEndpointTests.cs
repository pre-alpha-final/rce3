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

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        var feedPath = response.Headers.Location!.OriginalString.TrimStart('/');
        Assert.True(Guid.TryParse(feedPath, out _));
    }

    [Fact]
    public async Task MalformedRoutes_ReturnBadRequest()
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);
        var feedId = Guid.NewGuid();
        var readerId = Guid.NewGuid();

        await AssertStatusAsync(client.GetAsync("/not-a-guid"), HttpStatusCode.BadRequest);
        await AssertStatusAsync(client.GetAsync($"/{feedId}/not-a-guid"), HttpStatusCode.BadRequest);
        await AssertStatusAsync(client.GetAsync($"/{feedId}/not-a-guid/reset"), HttpStatusCode.BadRequest);
        await AssertStatusAsync(client.GetAsync($"/{feedId}/{readerId}/extra"), HttpStatusCode.BadRequest);
        await AssertStatusAsync(client.GetAsync($"/{feedId}/{readerId}/reset/extra"), HttpStatusCode.BadRequest);
        await AssertStatusAsync(client.DeleteAsync($"/{feedId}"), HttpStatusCode.BadRequest);
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

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/x-rce-test");

        await AssertStatusAsync(client.PostAsync($"/{feedId}", content), HttpStatusCode.OK);
        var response = await readerTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-rce-test", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(body, await response.Content.ReadAsByteArrayAsync());
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
