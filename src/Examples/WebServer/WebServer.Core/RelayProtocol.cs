using System.Buffers;
using System.Text.Json;

namespace WebServer;

internal sealed record RelayRequest(Guid Id, string Path);
internal sealed record RelayReply(Guid Id, int Status, string? ContentType, byte[] Body);

internal static class RelayProtocol
{
    public const string RequestType = "application/vnd.rce3.webserver-request+json";
    public const string ReplyType = "application/vnd.rce3.webserver-reply+json";
    public const int MaxAssetBytes = 750 * 1024;
    public const int MaxMessageBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static byte[] Write<T>(T message) => JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);

    public static T? Read<T>(byte[] message) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(message, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static async Task<byte[]?> ReadBoundedAsync(Stream stream, int limit, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        using var result = new MemoryStream();
        try
        {
            while (true)
            {
                var size = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, limit + 1 - (int)result.Length)), cancellationToken);
                if (size == 0)
                    return result.ToArray();
                if (result.Length + size > limit)
                    return null;

                result.Write(buffer, 0, size);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
