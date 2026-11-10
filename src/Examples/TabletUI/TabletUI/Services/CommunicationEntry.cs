using System.Text;

namespace TabletUI.Services;

public sealed record CommunicationEntry(
    DateTimeOffset Timestamp, string Direction, string Detail, string? ContentType, string Preview)
{
    public const int DefaultPreviewLimit = 500;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string FormatPreview(byte[] body, int limit = DefaultPreviewLimit, string authorization = "")
    {
        string text;
        try
        {
            text = StrictUtf8.GetString(body);
        }
        catch (DecoderFallbackException)
        {
            text = "Hex: " + Convert.ToHexStringLower(body);
        }

        text = Redact(text, authorization);
        var end = 0;
        foreach (var character in text.EnumerateRunes())
        {
            if (limit == 0)
            {
                return text[..end] + " … [truncated]";
            }

            limit--;
            end += character.Utf16SequenceLength;
        }
        return text;
    }

    internal static string Redact(string text, string authorization) =>
        authorization.Length == 0 ? text : text.Replace(authorization, "[redacted]", StringComparison.Ordinal);
}
