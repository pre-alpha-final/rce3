using System.Buffers;
using Microsoft.Extensions.Options;

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
        app.MapGet("/", CreateFeed);
        app.MapGet("/{feedGuid}", GetFeed);
        app.MapPost("/{feedGuid}", PostFeed);
        app.MapGet("/{feedGuid}/{readerGuid}", GetReader);
        app.MapGet("/{feedGuid}/{readerGuid}/reset", ResetReader);
        app.MapMethods("/{*path}", SupportedMethods, BadPath);
    }

    private static IResult CreateFeed(HttpRequest request, FeedStore feedStore, ILogger<Program> logger)
    {
        var feedId = Guid.NewGuid();
        var access = GetFeedAccess(request, feedStore, feedId, logger);
        if (!access.Succeeded)
        {
            return AccessFailure(access);
        }

        return Results.Redirect($"/{feedId:D}");
    }

    private static IResult GetFeed(
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

        var access = GetFeedAccess(request, feedStore, feedId, logger);
        if (!access.Succeeded)
        {
            return AccessFailure(access);
        }

        return Results.Text(FeedHelpText.Create(access.Feed, options.Value), "text/plain");
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
        try
        {
            body = await FeedRequestBodyReader.ReadAsync(
                request.Body,
                options.Value.MaxMessageSizeBytes,
                request.HttpContext.RequestAborted);
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

        var readerCount = access.Feed!.Publish(new FeedMessage(body, request.ContentType));
        FeedServerLog.MessagePosted(
            logger,
            body.Length,
            access.Feed.Id,
            readerCount);

        return Results.Text($"Message distributed to {readerCount} reader(s).", "text/plain");
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

        return Results.Bytes(message.Body, message.ContentType ?? "application/octet-stream");
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

        access.Feed!.EnsureReader(readerId).Reset();
        FeedServerLog.ReaderReset(logger, readerId, feedId);
        return Results.Redirect($"/{feedId:D}/{readerId:D}");
    }

    private static IResult BadPath(HttpRequest request, ILogger<Program> logger)
    {
        FeedServerLog.UnsupportedFeedPath(logger, request.Path);
        return BadRequest(
            "Bad feed path",
            $"'{request.Path}' does not match GET /{{feedGuid}}, POST /{{feedGuid}}, GET /{{feedGuid}}/{{readerGuid}}, or GET /{{feedGuid}}/{{readerGuid}}/reset.");
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
            title: "Reader queue unusable",
            detail: $"This reader queue exceeded the configured limit of {options.MaxQueuedMessagesPerReader} queued messages and will remain unusable until it is reset.",
            statusCode: StatusCodes.Status500InternalServerError);
    }

    private static IResult PayloadTooLarge(FeedServerOptions options)
    {
        return Results.Problem(
            title: "Payload too large",
            detail: $"Message bodies may not exceed {options.MaxMessageSizeBytes} bytes.",
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
