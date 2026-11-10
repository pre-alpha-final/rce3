using System.Buffers;
using System.IO.Compression;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace FeedServer;

public static class FeedEndpoints
{
    private static readonly string[] SupportedMethods =
    [
        HttpMethods.Get,
        HttpMethods.Post,
        HttpMethods.Put,
        HttpMethods.Patch,
        HttpMethods.Delete,
        HttpMethods.Options,
        HttpMethods.Head
    ];

    public static void Map(WebApplication app)
    {
        app.MapGet("/", RedirectToNewFeed);
        app.MapGet("/{feedGuid}", GetFeed);
        app.MapPost("/{feedGuid}", PostFeed);
        app.MapPost("/{feedGuid}/kill-reader-queues", KillReaderQueues);
        app.MapGet("/{feedGuid}/admin", GetAdmin);
        app.MapGet("/{feedGuid}/{readerGuid}", GetReader).WithMetadata(new ReaderPollMetadata());
        app.MapGet("/{feedGuid}/{readerGuid}/reset", ResetReader);
        app.MapMethods("/{*path}", SupportedMethods, BadPath);
    }

    internal static bool CanCompressPoll(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<ReaderPollMetadata>() is null)
        {
            return false;
        }

        // An explicit refusal takes precedence over a wildcard.
        return !StringWithQualityHeaderValue.TryParseList(context.Request.Headers.AcceptEncoding, out var encodings)
            || !encodings.Any(encoding => encoding.Value.Equals("gzip", StringComparison.OrdinalIgnoreCase)
                && encoding.Quality == 0);
    }

    private sealed class ReaderPollMetadata;

    private static IResult RedirectToNewFeed()
    {
        return Results.Redirect($"/{Guid.NewGuid():D}");
    }

    private static IResult GetFeed(
        string feedGuid,
        IOptions<FeedServerOptions> options,
        ILogger<Program> logger)
    {
        if (!FeedRouteParser.TryParseFeed(feedGuid, out var feedId, out var problem))
        {
            FeedServerLog.MalformedFeedRoute(logger, feedGuid, problem);
            return BadRequest("Bad feed route", problem);
        }

        return Results.Text(FeedHelpText.Create(feedId, options.Value), "text/plain");
    }

    private static async Task<IResult> PostFeed(
        string feedGuid,
        HttpRequest request,
        FeedStore feedStore,
        IOptions<FeedServerOptions> options,
        ILogger<Program> logger)
    {
        if (!FeedRouteParser.TryParseFeed(feedGuid, out var feedId, out var problem))
        {
            FeedServerLog.MalformedFeedRoute(logger, feedGuid, problem);
            return BadRequest("Bad feed route", problem);
        }

        if (!FeedAuthorizationKey.TryRead(request, out var key, out var authorizationProblem))
        {
            FeedServerLog.InvalidAuthorization(logger, feedId, authorizationProblem);
            return AccessFailure(FeedAccessResult.Failure(
                StatusCodes.Status401Unauthorized,
                "Invalid authorization",
                authorizationProblem));
        }

        var existingAccess = feedStore.AuthorizeExisting(feedId, key);
        if (existingAccess is not null && !existingAccess.Succeeded)
        {
            return AccessFailure(existingAccess);
        }

        var gzip = false;
        if (request.Headers.TryGetValue(HeaderNames.ContentEncoding, out var encodings))
        {
            var encoding = encodings.Count == 1 ? encodings[0]?.Trim() : null;
            gzip = string.Equals(encoding, "gzip", StringComparison.OrdinalIgnoreCase);
            if (!gzip && !string.Equals(encoding, "identity", StringComparison.OrdinalIgnoreCase))
            {
                request.HttpContext.Response.Headers.AcceptEncoding = "gzip";
                return Results.Problem(
                    title: "Unsupported content encoding",
                    detail: "Send a plain body or a body encoded with gzip.",
                    statusCode: StatusCodes.Status415UnsupportedMediaType);
            }
        }

        if (request.ContentLength > options.Value.MaxMessageSizeBytes)
        {
            FeedServerLog.DeclaredPayloadTooLarge(
                logger,
                feedId,
                request.ContentLength,
                options.Value.MaxMessageSizeBytes);
            return PayloadTooLarge(options.Value);
        }

        byte[] body;
        long? gzipDecodedLength = null;
        try
        {
            body = await FeedRequestBodyReader.ReadAsync(
                request.Body,
                options.Value.MaxMessageSizeBytes,
                request.HttpContext.RequestAborted);
            if (gzip)
            {
                if (body.Length == 0)
                {
                    return InvalidGzip();
                }

                gzipDecodedLength = await DecodeGzipAsync(body, Stream.Null, request.HttpContext.RequestAborted);
            }
        }
        catch (InvalidDataException) when (gzip)
        {
            return InvalidGzip();
        }
        catch (MessageTooLargeException)
        {
            FeedServerLog.StreamingPayloadTooLarge(
                logger,
                feedId,
                options.Value.MaxMessageSizeBytes);
            return PayloadTooLarge(options.Value);
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException exception)
            when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            FeedServerLog.StreamingPayloadTooLarge(
                logger,
                feedId,
                options.Value.MaxMessageSizeBytes);
            return PayloadTooLarge(options.Value);
        }

        var access = feedStore.GetOrCreate(feedId, key);
        if (!access.Succeeded)
        {
            return AccessFailure(access);
        }

        var message = new FeedMessage(body, request.ContentType) { GzipDecodedLength = gzipDecodedLength };
        var readerCount = access.Feed!.Publish(message);
        FeedServerLog.MessagePosted(
            logger,
            gzipDecodedLength ?? body.LongLength,
            access.Feed.Id,
            readerCount);

        return Results.Text($"Message distributed to {readerCount} reader(s).", "text/plain");
    }

    private static IResult GetAdmin(
        string feedGuid,
        HttpResponse response,
        IOptions<FeedServerOptions> options,
        ILogger<Program> logger)
    {
        if (!FeedRouteParser.TryParseFeed(feedGuid, out var feedId, out var problem))
        {
            FeedServerLog.MalformedFeedRoute(logger, feedGuid, problem);
            return BadRequest("Bad feed route", problem);
        }

        response.Headers.CacheControl = "no-store";
        return Results.Content(
            FeedAdminPage.Create(feedId, Guid.NewGuid(), options.Value.MessagePreviewLimit),
            "text/html; charset=utf-8");
    }

    private static async Task<IResult> GetReader(
        string feedGuid,
        string readerGuid,
        HttpRequest request,
        FeedStore feedStore,
        IOptions<FeedServerOptions> options,
        IHostApplicationLifetime appLifetime,
        ILogger<Program> logger)
    {
        var route = FeedRouteParser.TryParseReader(feedGuid, readerGuid, out var feedId, out var readerId, out var problem);
        if (!route)
        {
            FeedServerLog.MalformedReaderRoute(
                logger,
                feedGuid,
                readerGuid,
                problem);
            return BadRequest("Bad reader route", problem);
        }

        var access = GetFeedAccess(request, feedStore, feedId, logger);
        if (!access.Succeeded)
        {
            return AccessFailure(access);
        }

        var reader = access.Feed!.EnsureReader(readerId);
        if (reader.IsUnusable)
        {
            FeedServerLog.UnusableReader(logger, readerId, feedId);
            return UnusableReader(options.Value);
        }

        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(
            request.HttpContext.RequestAborted,
            appLifetime.ApplicationStopping);
        var message = await reader.ReadAsync(options.Value.PollTimeout, pollCts.Token);
        if (reader.IsUnusable)
        {
            FeedServerLog.UnusableReaderAfterPoll(logger, readerId, feedId);
            return UnusableReader(options.Value);
        }

        if (message is null)
        {
            FeedServerLog.EmptyReaderPoll(logger, readerId, feedId);
            return Results.NoContent();
        }

        var response = request.HttpContext.Response;
        response.Headers.Append(HeaderNames.Vary, HeaderNames.AcceptEncoding);
        var contentType = message.ContentType ?? "application/octet-stream";
        var decodedLength = message.GzipDecodedLength ?? message.Body.LongLength;
        if (decodedLength == 0)
        {
            response.ContentType = contentType;
            response.ContentLength = 0;
            return Results.Ok();
        }

        if (message.GzipDecodedLength.HasValue)
        {
            response.ContentLength = decodedLength;
            return Results.Stream(
                output => DecodeGzipAsync(message.Body, output, request.HttpContext.RequestAborted),
                contentType);
        }

        return Results.Bytes(message.Body, contentType);
    }

    private static IResult ResetReader(
        string feedGuid,
        string readerGuid,
        HttpRequest request,
        FeedStore feedStore,
        ILogger<Program> logger)
    {
        var route = FeedRouteParser.TryParseReader(feedGuid, readerGuid, out var feedId, out var readerId, out var problem);
        if (!route)
        {
            FeedServerLog.MalformedReaderResetRoute(
                logger,
                feedGuid,
                readerGuid,
                problem);
            return BadRequest("Bad reader reset route", problem);
        }

        var access = GetFeedAccess(request, feedStore, feedId, logger);
        if (!access.Succeeded)
        {
            return AccessFailure(access);
        }

        access.Feed!.ResetReader(readerId);
        FeedServerLog.ReaderReset(logger, readerId, feedId);
        return Results.Redirect($"/{feedId:D}/{readerId:D}");
    }

    private static IResult KillReaderQueues(
        string feedGuid,
        HttpRequest request,
        FeedStore feedStore,
        ILogger<Program> logger)
    {
        if (!FeedRouteParser.TryParseFeed(feedGuid, out var feedId, out var problem))
        {
            FeedServerLog.MalformedFeedRoute(logger, feedGuid, problem);
            return BadRequest("Bad feed route", problem);
        }

        if (!FeedAuthorizationKey.TryRead(request, out var key, out var authorizationProblem))
        {
            FeedServerLog.InvalidAuthorization(logger, feedId, authorizationProblem);
            return AccessFailure(FeedAccessResult.Failure(
                StatusCodes.Status401Unauthorized,
                "Invalid authorization",
                authorizationProblem));
        }

        var access = feedStore.AuthorizeExisting(feedId, key);
        if (access is not null && !access.Succeeded)
        {
            return AccessFailure(access);
        }

        var count = access?.Feed?.KillReaderQueues() ?? 0;
        FeedServerLog.ReaderQueuesKilled(logger, feedId, count);
        return Results.Text($"Killed {count} reader queue(s).", "text/plain");
    }

    private static IResult BadPath(HttpRequest request, ILogger<Program> logger)
    {
        FeedServerLog.UnsupportedFeedPath(logger, request.Path);
        return BadRequest(
            "Bad feed path",
            $"'{request.Path}' does not match GET /{{feedGuid}}, POST /{{feedGuid}}, GET /{{feedGuid}}/admin, GET /{{feedGuid}}/{{readerGuid}}, GET /{{feedGuid}}/{{readerGuid}}/reset, or POST /{{feedGuid}}/kill-reader-queues.");
    }

    private static IResult BadRequest(string title, string detail)
    {
        return Results.Problem(title: title, detail: detail, statusCode: StatusCodes.Status400BadRequest);
    }

    private static FeedAccessResult GetFeedAccess(
        HttpRequest request,
        FeedStore feedStore,
        Guid feedId,
        ILogger<Program> logger)
    {
        if (!FeedAuthorizationKey.TryRead(request, out var key, out var problem))
        {
            FeedServerLog.InvalidAuthorization(logger, feedId, problem);
            return FeedAccessResult.Failure(StatusCodes.Status401Unauthorized, "Invalid authorization", problem);
        }

        return feedStore.GetOrCreate(feedId, key);
    }

    private static IResult AccessFailure(FeedAccessResult access)
    {
        return Results.Problem(title: access.Title, detail: access.Detail, statusCode: access.StatusCode);
    }

    private static IResult UnusableReader(FeedServerOptions options)
    {
        return Results.Problem(
            title: "Reader queue overflowed",
            detail: $"This reader queue exceeded the configured limit of {options.MaxQueuedMessagesPerReader} queued messages; reset is required before polling can resume.",
            statusCode: StatusCodes.Status410Gone);
    }

    private static async Task<long> DecodeGzipAsync(byte[] body, Stream destination, CancellationToken cancellationToken)
    {
        using var gzip = new GZipStream(new MemoryStream(body, writable: false), CompressionMode.Decompress);
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            long length = 0;
            while (true)
            {
                var count = await gzip.ReadAsync(buffer, cancellationToken);
                if (count == 0)
                {
                    return length;
                }

                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                length += count;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static IResult InvalidGzip()
    {
        return BadRequest("Invalid gzip body", "Send a complete gzip stream.");
    }

    private static IResult PayloadTooLarge(FeedServerOptions options)
    {
        return Results.Problem(
            title: "Payload too large",
            detail: $"The upload transfer limit is {options.MaxMessageSizeBytes} bytes, measured before decoding.",
            statusCode: StatusCodes.Status413PayloadTooLarge);
    }
}

public static class FeedRequestBodyReader
{
    private const int BufferSize = 81920;

    public static async Task<byte[]> ReadAsync(Stream source, long maxBytes, CancellationToken cancellationToken)
    {
        using var body = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    return body.ToArray();
                }

                if (body.Length + read > maxBytes)
                {
                    throw new MessageTooLargeException();
                }

                body.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

public sealed class MessageTooLargeException : Exception
{
}
