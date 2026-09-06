using System.Text;

namespace TabletUI.Services;

public sealed record CommunicationEntry(
    DateTimeOffset Timestamp, string Direction, string Detail, string? ContentType, string Preview)
{
    public const int PreviewLimit = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string FormatPreview(byte[] body)
    {
        try
        {
            var text = StrictUtf8.GetString(body);
            return text.Length <= PreviewLimit ? text : text[..PreviewLimit] + " … [truncated]";
        }
        catch (DecoderFallbackException)
        {
            var count = Math.Min(body.Length, PreviewLimit / 3);
            return "Hex: " + Convert.ToHexStringLower(body.AsSpan(0, count))
                + (count < body.Length ? " … [truncated]" : string.Empty);
        }
    }
}
