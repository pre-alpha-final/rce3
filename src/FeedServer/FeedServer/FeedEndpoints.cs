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

    private static IResult PostFeed(
        string feedGuid,
        HttpRequest request,
        FeedStore feedStore,
        IOptions<FeedServerOptions> options)
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

        return Results.Text("Message accepted for feed handling. Reader queues will be implemented in phase 3.", "text/plain");
    }

    private static IResult GetReader(
        string feedGuid,
        string readerGuid,
        HttpRequest request,
        FeedStore feedStore,
        IOptions<FeedServerOptions> options)
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

        access.Feed!.EnsureReader(readerId);

        return NotImplemented($"Reader long polling will be implemented in a later phase. Configured timeout: {options.Value.PollTimeout}.");
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

    private static IResult NotImplemented(string detail)
    {
        return Results.Problem(
            title: "Feed handling not implemented",
            detail: detail,
            statusCode: StatusCodes.Status501NotImplemented);
    }
}
