using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FeedServer.Tests;

public sealed class FeedCompressionTests
{
    private const int TransferLimit = 1024;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GzipUploadCanDecodeToMoreThanTransferLimit(bool useKestrel)
    {
        using var factory = CreateFactory(useKestrel);
        using var client = CreateClient(factory, useKestrel);
        var feed = $"/{Guid.NewGuid():D}";
        var plainReader = await CreateReaderAsync(client, feed);
        var gzipReader = await CreateReaderAsync(client, feed);
        const string contentType = "text/plain; charset=utf-8; mode=exact";
        var body = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("gzip round trip\r\n", 8192)));
        var uploaded = Gzip(body, CompressionLevel.SmallestSize);
        Assert.True(body.Length > TransferLimit);
        Assert.True(uploaded.Length <= TransferLimit);

        using var accepted = await client.PostAsync(feed, Upload(uploaded, "GZip", contentType, unknownLength: useKestrel));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("Message distributed to 2 reader(s).", await accepted.Content.ReadAsStringAsync());

        using var plain = await client.GetAsync(plainReader);
        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        Assert.Empty(plain.Content.Headers.ContentEncoding);
        Assert.Contains("Accept-Encoding", plain.Headers.Vary);
        Assert.Equal(contentType, plain.Content.Headers.ContentType?.ToString());
        Assert.Equal(body.LongLength, plain.Content.Headers.ContentLength);
        Assert.Equal(body, await plain.Content.ReadAsByteArrayAsync());

        using var compressed = await PollAsync(client, gzipReader, "gzip");
        Assert.Equal(HttpStatusCode.OK, compressed.StatusCode);
        Assert.Equal("gzip", Assert.Single(compressed.Content.Headers.ContentEncoding));
        Assert.Contains("Accept-Encoding", compressed.Headers.Vary);
        Assert.Equal(contentType, compressed.Content.Headers.ContentType?.ToString());
        using var decoded = new MemoryStream();
        await using var gzip = new GZipStream(await compressed.Content.ReadAsStreamAsync(), CompressionMode.Decompress);
        await gzip.CopyToAsync(decoded);
        Assert.Equal(body, decoded.ToArray());

        using var emptyPoll = await PollAsync(client, gzipReader, "gzip");
        Assert.Equal(HttpStatusCode.NoContent, emptyPoll.StatusCode);
        Assert.Empty(emptyPoll.Content.Headers.ContentEncoding);
    }

    [Theory]
    [InlineData(null, "identity", "gzip", true)]
    [InlineData("application/x-example", "gzip", "gzip", false)]
    [InlineData("text/plain", "gzip", "gzip;q=0, *;q=1", false)]
    public async Task PollHonorsMediaTypeAndGzipPreference(
        string? contentType, string uploadEncoding, string acceptEncoding, bool expectGzip)
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);
        var feed = $"/{Guid.NewGuid():D}";
        var reader = await CreateReaderAsync(client, feed);
        var body = new byte[256];

        var uploaded = uploadEncoding == "gzip" ? Gzip(body) : body;
        using var accepted = await client.PostAsync(feed, Upload(uploaded, uploadEncoding, contentType));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        using var response = await PollAsync(client, reader, acceptEncoding);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(contentType ?? "application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        if (expectGzip)
        {
            Assert.Equal("gzip", Assert.Single(response.Content.Headers.ContentEncoding));
        }
        else
        {
            Assert.Empty(response.Content.Headers.ContentEncoding);
            Assert.Equal(body, await response.Content.ReadAsByteArrayAsync());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GzipOfEmptyMessagePublishesAnEmptyMessage(bool acceptsGzip)
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);
        var feed = $"/{Guid.NewGuid():D}";
        var reader = await CreateReaderAsync(client, feed);

        // A complete gzip member with an empty DEFLATE payload.
        var wire = Convert.FromBase64String("H4sIAAAAAAACAAMAAAAAAAAAAAA=");
        using var accepted = await client.PostAsync(feed, Upload(wire, "gzip"));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        using var response = acceptsGzip ? await PollAsync(client, reader, "gzip") : await client.GetAsync(reader);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("truncated")]
    public async Task InvalidGzipReturnsBadRequestWithoutCreatingFeed(string invalidity)
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);
        var feed = Guid.NewGuid();
        var body = Gzip("test"u8.ToArray());
        body[^8] ^= 1; // Corrupt the CRC for the corrupt case.
        body = invalidity switch
        {
            "missing" => [],
            "truncated" => Gzip("test"u8.ToArray())[..^3],
            _ => body
        };

        using var rejected = await client.PostAsync($"/{feed:D}", Upload(body, "gzip"));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
        AssertMissingFeed(factory, feed);
    }

    [Theory]
    [InlineData("br")]
    [InlineData("gzip, gzip")]
    public async Task UnsupportedEncodingReturnsAdvertisedGzipWithoutCreatingFeed(string encoding)
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);
        var feed = Guid.NewGuid();

        using var rejected = await client.PostAsync($"/{feed:D}", Upload([], encoding));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, rejected.StatusCode);
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
        Assert.Equal("gzip", Assert.Single(rejected.Headers.GetValues("Accept-Encoding")));
        AssertMissingFeed(factory, feed);
    }

    [Fact]
    public async Task BlankEncodingReturnsUnsupportedMediaTypeWithoutCreatingFeed()
    {
        using var factory = CreateFactory();
        var feed = Guid.NewGuid();

        var rejected = await factory.Server.SendAsync(context =>
        {
            context.Request.Method = HttpMethods.Post;
            context.Request.Path = $"/{feed:D}";
            context.Request.Headers.ContentEncoding = string.Empty;
        });

        Assert.Equal(StatusCodes.Status415UnsupportedMediaType, rejected.Response.StatusCode);
        Assert.Equal("gzip", rejected.Response.Headers.AcceptEncoding.ToString());
        AssertMissingFeed(factory, feed);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task GzipUploadExceedingTransferLimitIsRejected(bool unknownLength, bool useKestrel)
    {
        using var factory = CreateFactory(useKestrel);
        using var client = CreateClient(factory, useKestrel);
        var feed = Guid.NewGuid();
        var uploaded = Gzip(new byte[TransferLimit], CompressionLevel.NoCompression);
        Assert.True(uploaded.Length > TransferLimit);

        using var rejected = await client.PostAsync($"/{feed:D}", Upload(uploaded, "gzip", unknownLength: unknownLength));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, rejected.StatusCode);
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
        AssertMissingFeed(factory, feed);
    }

    [Fact]
    public async Task InvalidGzipDoesNotTouchExistingFeedAndAuthorizationRunsFirst()
    {
        var time = new ManualTimeProvider();
        using var baseFactory = CreateFactory();
        using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(time)));
        using var client = CreateClient(factory);
        var feedId = Guid.NewGuid();
        var feedPath = $"/{feedId:D}";
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "gzip-test-key");
        var reader = await CreateReaderAsync(client, feedPath);
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "gzip-test-key";
        Assert.True(FeedAuthorizationKey.TryRead(context.Request, out var key, out _));
        var feed = factory.Services.GetRequiredService<FeedStore>().AuthorizeExisting(feedId, key)!.Feed!;
        var activity = feed.LastActivityAt;
        time.Advance(TimeSpan.FromMinutes(1));

        using var malformed = await client.PostAsync(feedPath, Upload([1, 2, 3], "gzip"));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        client.DefaultRequestHeaders.Remove("Authorization");
        using var unauthorized = await client.PostAsync(feedPath, Upload(new byte[TransferLimit + 1], "gzip"));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Equal(activity, feed.LastActivityAt);

        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "gzip-test-key");
        using var empty = await client.GetAsync(reader);
        Assert.Equal(HttpStatusCode.NoContent, empty.StatusCode);
    }

    private static WebApplicationFactory<Program> CreateFactory(bool useKestrel = false)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["FeedServer:PollTimeout"] = "00:00:00.050",
                    ["FeedServer:MaxMessageSizeBytes"] = TransferLimit.ToString(CultureInfo.InvariantCulture)
                })));
        if (useKestrel)
        {
            factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        }

        return factory;
    }

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory, bool useKestrel = false)
    {
        if (!useKestrel)
        {
            return factory.CreateClient(new() { AllowAutoRedirect = false });
        }

        var server = factory.Services.GetRequiredService<IServer>();
        var address = server.Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(address) };
    }

    private static async Task<string> CreateReaderAsync(HttpClient client, string feed)
    {
        var reader = $"{feed}/{Guid.NewGuid():D}";
        using var response = await client.GetAsync($"{reader}/reset");
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        return reader;
    }

    private static async Task<HttpResponseMessage> PollAsync(HttpClient client, string reader, string acceptEncoding)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, reader);
        request.Headers.AcceptEncoding.ParseAdd(acceptEncoding);
        return await client.SendAsync(request);
    }

    private static HttpContent Upload(byte[] body, string encoding, string? contentType = null, bool unknownLength = false)
    {
        HttpContent content = unknownLength ? new UnknownLengthContent(body) : new ByteArrayContent(body);
        content.Headers.TryAddWithoutValidation("Content-Encoding", encoding);
        if (contentType is not null)
        {
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }

        return content;
    }

    private static byte[] Gzip(byte[] body, CompressionLevel level = CompressionLevel.Fastest)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, level, leaveOpen: true))
        {
            gzip.Write(body);
        }

        return output.ToArray();
    }

    private static void AssertMissingFeed(WebApplicationFactory<Program> factory, Guid feed)
    {
        Assert.Null(factory.Services.GetRequiredService<FeedStore>().AuthorizeExisting(feed, key: null));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan duration) => now += duration;
    }

    private sealed class UnknownLengthContent(byte[] body) : HttpContent
    {
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
