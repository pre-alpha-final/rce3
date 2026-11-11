using System.Net;
using System.Net.Http.Headers;

namespace RequestProxy;

internal sealed record FeedOptions(Uri Uri, string? Authorization)
{
    public static FeedOptions Load()
    {
        var feed = Environment.GetEnvironmentVariable("RCE3_FEED")?.Trim() ?? "";
        if (!ProxyProtocol.TryParseUrl(feed, out var uri) || uri.Query.Length != 0
            || !Guid.TryParse(Uri.UnescapeDataString(uri.AbsolutePath.TrimEnd('/').Split('/')[^1]), out _))
        {
            throw new ArgumentException("RCE3_FEED must be an absolute HTTP(S) feed URL ending in a GUID, without credentials, a query, or a fragment.");
        }

        var authorization = Environment.GetEnvironmentVariable("RCE3_AUTH");
        if (!string.IsNullOrEmpty(authorization)
            && (string.IsNullOrWhiteSpace(authorization)
                || authorization.Any(character => char.IsControl(character) && character != '\t')))
        {
            throw new ArgumentException("RCE3_AUTH must be empty or one non-blank, single-line Authorization value.");
        }

        return new(new Uri(uri.AbsoluteUri.TrimEnd('/')), string.IsNullOrEmpty(authorization) ? null : authorization);
    }
}

internal sealed class FeedConnection(FeedOptions options, Guid readerId, ProxySessions sessions, TimeSpan retryDelay)
    : IDisposable
{
    private static readonly TimeSpan FeedWriteTimeout = TimeSpan.FromSeconds(10);
    private readonly Uri _reader = new($"{options.Uri.AbsoluteUri}/{readerId:D}");
    private readonly Uri _reset = new($"{options.Uri.AbsoluteUri}/{readerId:D}/reset");
    private readonly HttpClient _client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.GZip,
        ConnectTimeout = FeedWriteTimeout
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await ResetAsync(cancellationToken);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var command = await PollAsync(cancellationToken);
            if (command is not null)
            {
                var result = await sessions.GetAsync(command, cancellationToken);
                await PublishAsync(ProxyProtocol.Reply(command, result), cancellationToken);
            }
        }
    }

    private async Task<string?> PollAsync(CancellationToken cancellationToken)
    {
        HttpStatusCode status;
        try
        {
            using var request = CreateRequest(HttpMethod.Get, _reader);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            status = response.StatusCode;
            if (status == HttpStatusCode.OK)
            {
                var message = await ProxyProtocol.ReadBoundedAsync(response.Content, ProxyProtocol.MaxCommandBytes, cancellationToken);
                return message is null ? null : ProxyProtocol.ReadCommand(message);
            }
        }
        catch (Exception exception) when (IsFeedIoFailure(exception, cancellationToken))
        {
            Console.Error.WriteLine("Feed poll failed. Retrying.");
            await Task.Delay(retryDelay, cancellationToken);
            return null;
        }

        if (status == HttpStatusCode.Gone)
        {
            Console.Error.WriteLine("Reader queue overflowed. Resetting.");
            await ResetAsync(cancellationToken);
        }
        else if (IsTransient(status))
        {
            Console.Error.WriteLine($"Feed poll returned HTTP {(int)status}. Retrying.");
            await Task.Delay(retryDelay, cancellationToken);
        }
        else if (status != HttpStatusCode.NoContent)
        {
            throw new FeedProtocolException("poll", status);
        }

        return null;
    }

    private async Task ResetAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(FeedWriteTimeout);
            try
            {
                using var request = CreateRequest(HttpMethod.Get, _reset);
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                var status = response.StatusCode;
                if (status == HttpStatusCode.Found)
                {
                    Console.WriteLine("Reader ready.");
                    return;
                }

                if (!IsTransient(status))
                {
                    throw new FeedProtocolException("reset", status);
                }

                Console.Error.WriteLine($"Feed reset returned HTTP {(int)status}. Retrying.");
            }
            catch (Exception exception) when (IsFeedIoFailure(exception, cancellationToken))
            {
                Console.Error.WriteLine("Feed reset failed. Retrying.");
            }

            await Task.Delay(retryDelay, cancellationToken);
        }
    }

    private async Task PublishAsync(byte[] body, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(FeedWriteTimeout);
        try
        {
            using var request = CreateRequest(HttpMethod.Post, options.Uri);
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain") { CharSet = "utf-8" };
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new FeedProtocolException("response publish", response.StatusCode);
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                Console.Error.WriteLine($"Response publish failed with HTTP {(int)response.StatusCode}.");
            }
        }
        catch (Exception exception) when (IsFeedIoFailure(exception, cancellationToken))
        {
            Console.Error.WriteLine("Response publish failed. Delivery is uncertain.");
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        if (options.Authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", options.Authorization);
        }

        return request;
    }

    private static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;

    private static bool IsFeedIoFailure(Exception exception, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested
        && exception is HttpRequestException or IOException or OperationCanceledException;

    public void Dispose() => _client.Dispose();
}

internal sealed class FeedProtocolException(string operation, HttpStatusCode status)
    : Exception($"Feed {operation} failed with HTTP {(int)status}.");
