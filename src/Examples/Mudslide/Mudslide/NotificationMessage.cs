using System.Text;

namespace Mudslide;

internal static class NotificationMessage
{
    private const string Prefix = "notify: ";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static bool TryParse(ReadOnlySpan<byte> body, out string notification)
    {
        string message;
        try
        {
            message = StrictUtf8.GetString(body);
        }
        catch (DecoderFallbackException)
        {
            notification = string.Empty;
            return false;
        }

        if (!message.StartsWith(Prefix, StringComparison.Ordinal)
            || message.Length == Prefix.Length)
        {
            notification = string.Empty;
            return false;
        }

        notification = message[Prefix.Length..];
        return true;
    }

    public static string ToMudslideText(string notification)
    {
        return notification
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }
}
