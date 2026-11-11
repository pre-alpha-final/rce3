using System.Globalization;
using System.Net;
using System.Text.Json;

namespace RequestProxy;

internal static class HarArchive
{
    private static readonly JsonSerializerOptions HarJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly HashSet<string> IgnoredHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Proxy-Connection", "Keep-Alive", "TE", "Trailer", "Transfer-Encoding", "Upgrade",
        "Proxy-Authorization", "Proxy-Authenticate", "Expect", "Accept-Encoding", "Cache-Control", "Pragma",
        "Range", "Origin", "Referer", "Cookie", "Cookie2"
    };

    public static async Task<Dictionary<Origin, HarOrigin>> LoadAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var archive = await JsonSerializer.DeserializeAsync<HarFile>(stream, HarJson, cancellationToken);
        var origins = new Dictionary<Origin, HarOrigin>();
        foreach (var entry in archive?.Log?.Entries ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry?.Request is not { Url: { } url } request || !ProxyProtocol.TryParseUrl(url, out var uri))
            {
                continue;
            }

            var origin = Origin.Of(uri);
            if (!origins.TryGetValue(origin, out var capture))
            {
                capture = new HarOrigin();
                origins.Add(origin, capture);
            }

            var headers = (request.Headers ?? [])
                .OfType<HarHeader>()
                .Where(header => !string.IsNullOrEmpty(header.Name) && IsHeaderValue(header.Value))
                .ToArray();

            var connectionHeaders = headers
                .Where(header => IsNamed(header, "Connection"))
                .SelectMany(header => header.Value!.Split(',', StringSplitOptions.TrimEntries))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var capturedHeaders = headers
                .Where(header => IsReplayable(header.Name!, connectionHeaders))
                .Select(header => new KeyValuePair<string, string>(header.Name!, header.Value!))
                .ToArray();
            capture.Requests.Add(new CapturedRequest(uri, capturedHeaders));

            var structuredCookies = false;
            foreach (var cookie in request.Cookies ?? [])
            {
                if (cookie is not null && TryAddCookie(capture.Cookies, uri, cookie))
                {
                    structuredCookies = true;
                }
            }

            if (!structuredCookies)
            {
                foreach (var header in headers.Where(header => IsNamed(header, "Cookie")))
                {
                    AddCookieHeader(capture.Cookies, uri, header.Value!);
                }
            }
        }

        if (origins.Count == 0)
        {
            throw new InvalidDataException("The HAR contains no HTTP(S) requests.");
        }

        return origins;
    }

    private static bool IsNamed(HarHeader header, string name) =>
        string.Equals(header.Name, name, StringComparison.OrdinalIgnoreCase);

    private static bool IsHeaderValue(string? value) =>
        value is not null && !value.Any(character => char.IsControl(character) && character != '\t');

    private static bool IsReplayable(string name, HashSet<string> connectionHeaders) =>
        !IgnoredHeaders.Contains(name) && !connectionHeaders.Contains(name) && !name.StartsWith(':')
        && !name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)
        && !name.StartsWith("If-", StringComparison.OrdinalIgnoreCase)
        && !name.StartsWith("Sec-", StringComparison.OrdinalIgnoreCase);

    private static void AddCookieHeader(CookieContainer jar, Uri uri, string header)
    {
        foreach (var pair in header.Split(';', StringSplitOptions.TrimEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator > 0)
            {
                TryAddCookie(jar, uri, new HarCookie(pair[..separator].TrimEnd(), pair[(separator + 1)..]));
            }
        }
    }

    private static bool TryAddCookie(CookieContainer jar, Uri uri, HarCookie source)
    {
        if (string.IsNullOrEmpty(source.Name) || !IsHeaderValue(source.Value))
        {
            return false;
        }

        try
        {
            var cookie = new Cookie(source.Name, source.Value!, string.IsNullOrEmpty(source.Path) ? "/" : source.Path)
            {
                Secure = source.Secure
            };
            if (!string.IsNullOrEmpty(source.Domain))
            {
                cookie.Domain = source.Domain;
            }

            if (DateTimeOffset.TryParse(source.Expires, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var expires))
            {
                cookie.Expires = expires.UtcDateTime;
            }

            jar.Add(uri, cookie);
            return true;
        }
        catch (CookieException)
        {
            return false;
        }
    }

    private sealed record HarFile(HarLog? Log);
    private sealed record HarLog(HarEntry?[]? Entries);
    private sealed record HarEntry(HarRequest? Request);
    private sealed record HarRequest(string? Url, HarHeader?[]? Headers, HarCookie?[]? Cookies);
    private sealed record HarHeader(string? Name, string? Value);
    private sealed record HarCookie(
        string? Name, string? Value, string? Path = null, string? Domain = null, string? Expires = null, bool Secure = false);
}

internal sealed class HarOrigin
{
    public CookieContainer Cookies { get; } = new() { PerDomainCapacity = int.MaxValue };
    public List<CapturedRequest> Requests { get; } = [];
}

internal sealed record CapturedRequest(Uri Uri, KeyValuePair<string, string>[] Headers);
