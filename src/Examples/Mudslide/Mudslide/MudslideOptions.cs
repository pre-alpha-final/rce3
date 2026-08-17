namespace Mudslide;

internal sealed record MudslideOptions(Uri FeedUri, string? Authorization)
{
    public const string FeedEnvironmentVariable = "RCE3_FEED";
    public const string AuthorizationEnvironmentVariable = "RCE3_AUTH";

    public static bool TryCreate(
        IReadOnlyList<string> arguments,
        Func<string, string?> getEnvironmentVariable,
        out MudslideOptions? options,
        out string problem)
    {
        options = null;

        if (arguments.Count > 2)
        {
            problem = "Expected at most a feed URL and an authorization value.";
            return false;
        }

        var feedValue = arguments.Count >= 1
            ? arguments[0]
            : getEnvironmentVariable(FeedEnvironmentVariable);
        var authorizationValue = arguments.Count >= 2
            ? arguments[1]
            : getEnvironmentVariable(AuthorizationEnvironmentVariable);

        if (string.IsNullOrWhiteSpace(feedValue))
        {
            problem = $"A feed URL is required as argument 1 or {FeedEnvironmentVariable}.";
            return false;
        }

        if (!Uri.TryCreate(feedValue, UriKind.Absolute, out var feedUri)
            || (feedUri.Scheme != Uri.UriSchemeHttp && feedUri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(feedUri.Query)
            || !string.IsNullOrEmpty(feedUri.Fragment))
        {
            problem = "The feed URL must be an absolute HTTP or HTTPS URL without a query or fragment.";
            return false;
        }

        var path = feedUri.AbsolutePath.TrimEnd('/');
        var finalSegment = path[(path.LastIndexOf('/') + 1)..];
        if (path.Length == 0
            || !Guid.TryParse(Uri.UnescapeDataString(finalSegment), out _))
        {
            problem = "The feed URL path must end with a valid feed GUID.";
            return false;
        }

        options = new MudslideOptions(
            new Uri(feedUri.AbsoluteUri.TrimEnd('/'), UriKind.Absolute),
            string.IsNullOrEmpty(authorizationValue) ? null : authorizationValue);
        problem = string.Empty;
        return true;
    }
}
