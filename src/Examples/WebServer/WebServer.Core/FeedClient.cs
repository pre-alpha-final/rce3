using System.Net;
using System.Net.Http.Headers;

namespace WebServer;

internal sealed record FeedOptions(Uri Url, string? Authorization)
{
    public static FeedOptions Load(string? value, string? authorization)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || !Guid.TryParse(Uri.UnescapeDataString(uri.AbsolutePath.TrimEnd('/').Split('/')[^1]), out _))
        {
            throw new ArgumentException("RCE3_FEED must be an absolute HTTP(S) feed URL ending in a GUID, without credentials, a query, or a fragment.");
        }

        if (!string.IsNullOrEmpty(authorization) && (string.IsNullOrWhiteSpace(authorization)
            || authorization.Any(character => char.IsControl(character) && character != '\t')))
        {
            throw new ArgumentException("RCE3_AUTH must be empty or one non-blank, single-line Authorization value.");
        }

        return new(new Uri(uri.AbsoluteUri.TrimEnd('/')), string.IsNullOrEmpty(authorization) ? null : authorization);
    }
}

internal sealed class FeedClient(HttpClient http, FeedOptions options, Guid readerId)
{
    public static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(90);
    private readonly Uri _reader = new($"{options.Url.AbsoluteUri}/{readerId:D}");
    private readonly Uri _reset = new($"{options.Url.AbsoluteUri}/{readerId:D}/reset");

    public static HttpClient CreateHttpClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip,
        ConnectTimeout = WriteTimeout,
        UseCookies = false
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(WriteTimeout);
        using var request = CreateRequest(HttpMethod.Get, _reset);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        RequireStatus(response, HttpStatusCode.Found, "reset");
    }

    public async Task<FeedPoll> PollAsync(string mediaType, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(PollTimeout);
        using var request = CreateRequest(HttpMethod.Get, _reader);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (response.StatusCode == HttpStatusCode.Gone)
            return new(Overflow: true, Message: null);
        if (response.StatusCode == HttpStatusCode.NoContent)
            return default;

        RequireStatus(response, HttpStatusCode.OK, "poll");
        if (!string.Equals(response.Content.Headers.ContentType?.MediaType, mediaType, StringComparison.OrdinalIgnoreCase)
            || response.Content.Headers.ContentLength > RelayProtocol.MaxMessageBytes)
        {
            return default;
        }

        using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        var message = await RelayProtocol.ReadBoundedAsync(stream, RelayProtocol.MaxMessageBytes, deadline.Token);
        return new(Overflow: false, message);
    }

    public async Task PublishAsync(string mediaType, byte[] message, CancellationToken cancellationToken)
    {
        if (message.Length > RelayProtocol.MaxMessageBytes)
            throw new FeedStatusException("publish", HttpStatusCode.RequestEntityTooLarge);

        using var request = CreateRequest(HttpMethod.Post, options.Url);
        request.Content = new ByteArrayContent(message);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        RequireStatus(response, HttpStatusCode.OK, "publish");
    }

    public static bool IsTransportFailure(Exception exception) =>
        exception is HttpRequestException or IOException or InvalidDataException or OperationCanceledException;

    private HttpRequestMessage CreateRequest(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        if (options.Authorization is not null)
            request.Headers.TryAddWithoutValidation("Authorization", options.Authorization);

        return request;
    }

    private static void RequireStatus(HttpResponseMessage response, HttpStatusCode expected, string operation)
    {
        if (response.StatusCode != expected)
            throw new FeedStatusException(operation, response.StatusCode);
    }
}

internal readonly record struct FeedPoll(bool Overflow, byte[]? Message);

internal sealed class FeedStatusException(string operation, HttpStatusCode status)
    : Exception($"Feed {operation} returned HTTP {(int)status}."
        + (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? " Check RCE3_FEED and RCE3_AUTH." : ""))
{
    public bool IsTransient => status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;
    public bool IsAuthorizationFailure => status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
}
