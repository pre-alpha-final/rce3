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
        app.MapMethods("/{*path}", SupportedMethods, BadPath);
    }

    private static IResult CreateFeed(HttpRequest request, FeedStore feedStore)
    {
        var feedId = Guid.NewGuid();
        var access = GetFeedAccess(request, feedStore, feedId);
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
        IOptions<FeedServerOptions> options)
    {
        if (!FeedRouteParser.TryParseFeed(feedGuid, out var feedId, out var problem))
        {
            return BadRequest("Bad feed route", problem);
        }

        var access = GetFeedAccess(request, feedStore, feedId);
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
            return BadRequest("Bad feed route", problem);
        }

        if (request.ContentLength > options.Value.MaxMessageSizeBytes)
        {
            return Results.Problem(
                title: "Payload too large",
                detail: $"Message bodies may not exceed {options.Value.MaxMessageSizeBytes} bytes.",
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        var access = GetFeedAccess(request, feedStore, feedId);
        if (!access.Succeeded)
        {
            return AccessFailure(access);
        }

        var body = await ReadBodyAsync(request, request.HttpContext.RequestAborted);
        var readerCount = access.Feed!.Publish(new FeedMessage(body, request.ContentType));
        logger.LogInformation(
            "Posted {MessageSize} byte message to feed {FeedId}; distributed to {ReaderCount} reader(s).",
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
            return BadRequest("Bad reader route", problem);
        }

        var access = GetFeedAccess(request, feedStore, feedId);
        if (!access.Succeeded)
        {
            return AccessFailure(access);
        }

        var reader = access.Feed!.EnsureReader(readerId);
        if (reader.IsUnusable)
        {
            return UnusableReader(options.Value);
        }

        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(
            request.HttpContext.RequestAborted,
            appLifetime.ApplicationStopping);
        var message = await reader.ReadAsync(options.Value.PollTimeout, pollCts.Token);
        if (reader.IsUnusable)
        {
            return UnusableReader(options.Value);
        }

        if (message is null)
        {
            logger.LogInformation("Reader {ReaderId} timed out or completed with no message for feed {FeedId}.", readerId, feedId);
            return Results.NoContent();
        }

        return Results.Bytes(message.Body, message.ContentType ?? "application/octet-stream");
    }

    private static IResult BadPath(HttpRequest request)
    {
        return BadRequest(
            "Bad feed path",
            $"'{request.Path}' does not match GET /{{feedGuid}}, POST /{{feedGuid}}, or GET /{{feedGuid}}/{{readerGuid}}.");
    }

    private static IResult BadRequest(string title, string detail)
    {
        return Results.Problem(title: title, detail: detail, statusCode: StatusCodes.Status400BadRequest);
    }

    private static FeedAccessResult GetFeedAccess(HttpRequest request, FeedStore feedStore, Guid feedId)
    {
        if (!FeedAuthorizationKey.TryRead(request, out var key, out var problem))
        {
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
            detail: $"This reader queue exceeded the configured limit of {options.MaxQueuedMessagesPerReader} queued messages and will remain unusable until the feed is deleted.",
            statusCode: StatusCodes.Status500InternalServerError);
    }

    private static async Task<byte[]> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        using var body = new MemoryStream();
        await request.Body.CopyToAsync(body, cancellationToken);
        return body.ToArray();
    }
}
