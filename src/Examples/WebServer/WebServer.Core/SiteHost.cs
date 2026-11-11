using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Logging;

namespace WebServer;

internal sealed class SiteHost(FeedClient feed, string assets, ILogger<SiteHost> logger)
{
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();
    private static readonly FileExtensionContentTypeProvider ContentTypes = new()
    {
        Mappings =
        {
            [".html"] = "text/html; charset=utf-8",
            [".htm"] = "text/html; charset=utf-8",
            [".css"] = "text/css; charset=utf-8"
        }
    };

    public async Task ReceiveRequestAsync(byte[] message, CancellationToken cancellationToken)
    {
        var request = RelayProtocol.Read<RelayRequest>(message);
        if (request is null || request.Id == Guid.Empty || request.Path is null || !request.Path.StartsWith('/'))
            return;

        var reply = await ReadAssetAsync(request, cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(FeedClient.WriteTimeout);
        try
        {
            await feed.PublishAsync(RelayProtocol.ReplyType, RelayProtocol.Write(reply), deadline.Token);
        }
        catch (FeedStatusException exception) when (!exception.IsAuthorizationFailure)
        {
            logger.LogWarning("{Reason}", exception.Message);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && FeedClient.IsTransportFailure(exception))
        {
            logger.LogWarning("Feed reply publish failed. Delivery is uncertain.");
        }
    }

    private async Task<RelayReply> ReadAssetAsync(RelayRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var file = ResolveAssetPath(request.Path);
            if (file is null)
                return new(request.Id, StatusCodes.Status404NotFound, null, []);

            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            var bytes = await RelayProtocol.ReadBoundedAsync(stream, RelayProtocol.MaxAssetBytes, cancellationToken);
            if (bytes is not null)
            {
                var contentType = ContentTypes.TryGetContentType(file, out var mappedType)
                    ? mappedType
                    : "application/octet-stream";
                return new(request.Id, StatusCodes.Status200OK, contentType, bytes);
            }

            logger.LogWarning("Asset for request {RequestId} exceeds the relay size limit.", request.Id);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or PathTooLongException)
        {
            return new(request.Id, StatusCodes.Status404NotFound, null, []);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Asset for request {RequestId} could not be read.", request.Id);
        }

        return new(request.Id, StatusCodes.Status502BadGateway, null, []);
    }

    private string? ResolveAssetPath(string requestPath)
    {
        var relativePath = requestPath == "/" ? "index.html" : requestPath[1..];
        var path = assets;
        FileAttributes attributes = default;
        foreach (var segment in relativePath.Split('/'))
        {
            // Windows can normalize trailing dots and spaces into a traversal segment.
            if (segment.Length == 0 || segment is "." or ".."
                || segment.IndexOfAny(InvalidFileNameChars) >= 0
                || (OperatingSystem.IsWindows() && (segment.EndsWith('.') || segment.EndsWith(' '))))
            {
                return null;
            }

            path = Path.Join(path, segment);
            attributes = File.GetAttributes(path);
            // A file symlink or directory junction can point outside the site.
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                return null;
        }

        return (attributes & FileAttributes.Directory) == 0 ? path : null;
    }
}
