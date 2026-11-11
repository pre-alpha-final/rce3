using System.Net;
using System.Text;

namespace RequestProxy;

internal sealed class ProxySessions(TimeSpan targetTimeout) : IDisposable
{
    private readonly Dictionary<Origin, OriginSession> _sessions = [];
    private readonly SemaphoreSlim _access = new(1);

    public async Task<int> ImportAsync(string path, CancellationToken cancellationToken)
    {
        var captures = await HarArchive.LoadAsync(path, cancellationToken);
        await _access.WaitAsync(cancellationToken);
        try
        {
            foreach (var (origin, capture) in captures)
            {
                var replacement = new OriginSession(capture);
                if (_sessions.Remove(origin, out var old))
                {
                    old.Dispose();
                }

                _sessions.Add(origin, replacement);
            }
        }
        finally
        {
            _access.Release();
        }

        return captures.Count;
    }

    public async Task<object> GetAsync(string url, CancellationToken cancellationToken)
    {
        if (!ProxyProtocol.TryParseUrl(url, out var target))
        {
            return ProxyProtocol.Error("InvalidUrl", "An absolute HTTP(S) URL without credentials or a fragment is required.");
        }

        await _access.WaitAsync(cancellationToken);
        try
        {
            if (!_sessions.TryGetValue(Origin.Of(target), out var session))
            {
                return ProxyProtocol.Error("OriginNotImported", "The URL's origin is absent from the loaded HAR files.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            foreach (var (name, value) in session.SelectCapture(target).Headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(targetTimeout);
            try
            {
                using var response = await session.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                var body = await ProxyProtocol.ReadBoundedAsync(response.Content, ProxyProtocol.MaxReplyBytes, deadline.Token);
                if (body is null)
                {
                    return ProxyProtocol.TooLarge();
                }

                var headers = response.Headers.Concat(response.Content.Headers)
                    .Where(header => !header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)
                        && !header.Key.Equals("Set-Cookie2", StringComparison.OrdinalIgnoreCase))
                    .GroupBy(header => header.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key.ToLowerInvariant(), group => group.SelectMany(header => header.Value).ToArray());

                return new
                {
                    status = (int)response.StatusCode,
                    statusText = response.ReasonPhrase ?? "",
                    ok = response.IsSuccessStatusCode,
                    headers,
                    body = Encoding.UTF8.GetString(body)
                };
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return ProxyProtocol.Error("TimeoutError", "The target GET exceeded its deadline.");
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ProxyProtocol.Error("HttpRequestError", "The target GET failed.");
            }
        }
        finally
        {
            _access.Release();
        }
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }

        _access.Dispose();
    }

    private sealed class OriginSession(HarOrigin capture) : IDisposable
    {
        public HttpClient Client { get; } = new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            CookieContainer = capture.Cookies,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        public CapturedRequest SelectCapture(Uri target)
        {
            // Iterating backwards makes the last capture win when the paths are equally specific.
            CapturedRequest? best = null;
            var bestScore = -1;
            for (var index = capture.Requests.Count - 1; index >= 0; index--)
            {
                var request = capture.Requests[index];
                if (request.Uri.PathAndQuery == target.PathAndQuery)
                {
                    return request;
                }

                var score = request.Uri.AbsolutePath == target.AbsolutePath
                    ? int.MaxValue
                    : SharedPathLength(request.Uri.AbsolutePath, target.AbsolutePath);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = request;
                }
            }

            return best!;
        }

        private static int SharedPathLength(string first, string second)
        {
            var length = Math.Min(first.Length, second.Length);
            var boundary = 0;
            var index = 0;
            while (index < length && first[index] == second[index])
            {
                if (first[index] == '/')
                {
                    boundary = index;
                }

                index++;
            }

            if (index == length
                && ((index == first.Length && index < second.Length && second[index] == '/')
                    || (index == second.Length && index < first.Length && first[index] == '/')))
            {
                return index;
            }

            return boundary;
        }

        public void Dispose() => Client.Dispose();
    }
}
