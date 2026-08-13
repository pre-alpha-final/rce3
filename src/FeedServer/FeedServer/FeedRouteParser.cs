namespace FeedServer;

public static class FeedRouteParser
{
    public static bool TryParseFeed(string feedGuid, out Guid feedId, out string problem)
    {
        feedId = default;

        if (!Guid.TryParse(feedGuid, out feedId))
        {
            problem = "Feed ID must parse cleanly as a C# Guid value.";
            return false;
        }

        problem = string.Empty;
        return true;
    }

    public static bool TryParseReader(
        string feedGuid,
        string readerGuid,
        out Guid feedId,
        out Guid readerId,
        out string problem)
    {
        readerId = default;

        if (!TryParseFeed(feedGuid, out feedId, out problem))
        {
            return false;
        }

        if (!Guid.TryParse(readerGuid, out readerId))
        {
            problem = "Reader ID must parse cleanly as a C# Guid value.";
            return false;
        }

        problem = string.Empty;
        return true;
    }
}
