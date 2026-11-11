using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;

namespace RequestProxy;

internal static class ProxyProtocol
{
    private static readonly byte[] CommandPrefix = "requestproxy: "u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal const int MaxCommandBytes = 64 * 1024;
    internal const int MaxReplyBytes = 1024 * 1024;

    public static string? ReadCommand(byte[] message)
    {
        if (!message.AsSpan().StartsWith(CommandPrefix))
        {
            return null;
        }

        try
        {
            var url = StrictUtf8.GetString(message.AsSpan(CommandPrefix.Length));
            return url.Contains('\r') || url.Contains('\n') ? null : url.Trim();
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    public static bool TryParseUrl(string value, [NotNullWhen(true)] out Uri? uri)
    {
        if (!value.Any(char.IsControl)
            && Uri.TryCreate(value, UriKind.Absolute, out uri)
            && uri.Scheme is "http" or "https"
            && uri.Host.Length > 0
            && uri.UserInfo.Length == 0
            && uri.Fragment.Length == 0)
        {
            return true;
        }

        uri = null;
        return false;
    }

    public static byte[] Reply(string url, object result)
    {
        var prefix = Encoding.UTF8.GetBytes($"requestproxy_response: {url}\n");
        var json = JsonSerializer.SerializeToUtf8Bytes(result);
        if (prefix.Length + json.Length > MaxReplyBytes)
        {
            json = JsonSerializer.SerializeToUtf8Bytes(TooLarge());
        }

        return [.. prefix, .. json];
    }

    public static object Error(string name, string message) => new { error = new { name, message } };

    public static object TooLarge() => Error("ResponseTooLarge", "The response exceeds the Request Proxy reply limit.");

    public static async Task<byte[]?> ReadBoundedAsync(
        HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (read > maxBytes - output.Length)
            {
                return null;
            }

            output.Write(buffer.AsSpan(0, read));
        }
    }
}

internal readonly record struct Origin(string Scheme, string Host, int Port)
{
    public static Origin Of(Uri uri) => new(uri.Scheme, uri.IdnHost.ToLowerInvariant(), uri.Port);
}
