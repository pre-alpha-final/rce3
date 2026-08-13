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
        app.MapGet("/{feedGuid}", GetFeed);
        app.MapPost("/{feedGuid}", PostFeed);
        app.MapGet("/{feedGuid}/{readerGuid}", GetReader);
        app.MapMethods("/{*path}", SupportedMethods, BadPath);
    }

    private static IResult GetFeed(string feedGuid, IOptions<FeedServerOptions> options)
    {
        if (!FeedRouteParser.TryParseFeed(feedGuid, out var feedId, out var problem))
        {
            return BadRequest("Bad feed route", problem);
        }

        return Results.Text(FeedHelpText.Create(feedId, options.Value), "text/plain");
    }

    private static IResult PostFeed(string feedGuid, HttpRequest request, IOptions<FeedServerOptions> options)
    {
        if (!FeedRouteParser.TryParseFeed(feedGuid, out _, out var problem))
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

        return NotImplemented("Feed posting will be implemented in phase 2.");
    }

    private static IResult GetReader(string feedGuid, string readerGuid, IOptions<FeedServerOptions> options)
    {
        var route = FeedRouteParser.TryParseReader(feedGuid, readerGuid, out _, out _, out var problem);
        if (!route)
        {
            return BadRequest("Bad reader route", problem);
        }

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

    private static IResult NotImplemented(string detail)
    {
        return Results.Problem(
            title: "Feed handling not implemented",
            detail: detail,
            statusCode: StatusCodes.Status501NotImplemented);
    }
}
