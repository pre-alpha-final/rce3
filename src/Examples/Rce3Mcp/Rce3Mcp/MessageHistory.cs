using System.Globalization;
using System.Text;

namespace Rce3Mcp;

internal sealed record HistoryMessage(string Cursor, DateTimeOffset ReceivedAt, string ContentType,
    long? ByteLength, string? Encoding, string? Body, bool BodyOmitted);
internal sealed record HistoryPage(IReadOnlyList<HistoryMessage> Messages, string NextCursor, bool HistoryGap);
internal sealed record HistoryStatistics(int Count, long EvictedCount);

internal sealed class MessageHistory(McpOptions options, TimeProvider timeProvider)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly object _gate = new();
    private readonly Guid _instance = Guid.NewGuid();
    private readonly Queue<(long Sequence, HistoryMessage Message, int Bytes)> _messages = new();
    private long _sequence;
    private long _bodyBytes;
    private long _evicted;

    public void Add(byte[]? body, string contentType, long? byteLength)
    {
        string? encoding = null;
        string? text = null;
        if (body is not null)
        {
            try
            {
                text = StrictUtf8.GetString(body);
                encoding = "utf8";
            }
            catch (DecoderFallbackException)
            {
                text = Convert.ToBase64String(body);
                encoding = "base64";
            }
        }
        lock (_gate)
        {
            var sequence = ++_sequence;
            var bytes = body?.Length ?? 0;
            _messages.Enqueue((sequence, new(Cursor(sequence), timeProvider.GetUtcNow(), contentType,
                byteLength, encoding, text, body is null), bytes));
            _bodyBytes += bytes;
            while (_messages.Count > options.HistoryCapacity || _bodyBytes > options.MaxHistoryBytes)
            {
                _bodyBytes -= _messages.Dequeue().Bytes;
                _evicted++;
            }
        }
    }

    public HistoryPage Read(string? afterCursor = null, int limit = 20)
    {
        if (limit is < 1 or > 100)
            throw new ArgumentException("limit must be between 1 and 100.");
        lock (_gate)
        {
            if (afterCursor is null)
            {
                var latest = _messages.TakeLast(limit).Select(entry => entry.Message).ToArray();
                return new(latest, Cursor(_sequence), _evicted != 0);
            }
            var parts = afterCursor.Split(':');
            if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out var instance)
                || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var after) || after < 0)
                throw new ArgumentException("afterCursor is malformed. Omit it to read recent messages.");
            if (instance != _instance)
                throw new ArgumentException("History was restarted; this cursor belongs to another process. Omit afterCursor to start again.");
            if (after > _sequence)
                throw new ArgumentException("afterCursor is ahead of this history.");
            var messages = _messages.Where(entry => entry.Sequence > after).Take(limit).Select(entry => entry.Message).ToArray();
            var gap = _messages.TryPeek(out var oldest) && after < oldest.Sequence - 1;
            return new(messages, messages.Length == 0 ? Cursor(after) : messages[^1].Cursor, gap);
        }
    }

    public HistoryStatistics Statistics()
    {
        lock (_gate) return new(_messages.Count, _evicted);
    }

    private string Cursor(long sequence) => $"{_instance:N}:{sequence.ToString(CultureInfo.InvariantCulture)}";
}
