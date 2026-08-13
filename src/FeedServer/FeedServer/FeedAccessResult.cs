namespace FeedServer;

public sealed class FeedAccessResult
{
    private FeedAccessResult(FeedState? feed, int? statusCode, string? title, string? detail)
    {
        Feed = feed;
        StatusCode = statusCode;
        Title = title;
        Detail = detail;
    }

    public FeedState? Feed { get; }

    public int? StatusCode { get; }

    public string? Title { get; }

    public string? Detail { get; }

    public bool Succeeded => Feed is not null;

    public static FeedAccessResult Success(FeedState feed)
    {
        return new FeedAccessResult(feed, null, null, null);
    }

    public static FeedAccessResult Failure(int statusCode, string title, string detail)
    {
        return new FeedAccessResult(null, statusCode, title, detail);
    }
}
