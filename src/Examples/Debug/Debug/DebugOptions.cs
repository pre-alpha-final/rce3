namespace Debug;

internal sealed record DebugOptions(Uri FeedUri, string? Authorization)
{
    private const string FeedUrlEnvironmentVariable = "RCE3_FEED_URL";
    private const string AuthorizationEnvironmentVariable = "RCE3_AUTH";

    public static bool TryParse(string[] args, out DebugOptions options, out string? error)
    {
        var feedUrl = Environment.GetEnvironmentVariable(FeedUrlEnvironmentVariable);
        var authorization = Environment.GetEnvironmentVariable(AuthorizationEnvironmentVariable);

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--feed":
                    if (!TryReadValue(args, ref index, "--feed", out feedUrl, out error))
                    {
                        options = null!;
                        return false;
                    }
                    break;
                case "--auth":
                    if (!TryReadValue(args, ref index, "--auth", out authorization, out error))
                    {
                        options = null!;
                        return false;
                    }
                    break;
                case "--no-auth":
                    authorization = null;
                    break;
                default:
                    if (args[index].StartsWith('-'))
                    {
                        options = null!;
                        error = $"Unknown option '{args[index]}'.";
                        return false;
                    }

                    feedUrl = args[index];
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(feedUrl))
        {
            options = null!;
            error = $"A feed URL is required. Pass --feed URL or set {FeedUrlEnvironmentVariable}.";
            return false;
        }

        if (!TryNormalizeFeedUri(feedUrl, out var feedUri, out error))
        {
            options = null!;
            return false;
        }

        options = new DebugOptions(feedUri, authorization);
        error = null;
        return true;
    }

    public static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("Usage: Debug [--feed URL] [--auth VALUE | --no-auth] [URL]");
        writer.WriteLine();
        writer.WriteLine("Connect to an RCE3 feed with a new reader. Each stdin line is posted as");
        writer.WriteLine("one UTF-8 text message; each received message is written to stdout.");
        writer.WriteLine();
        writer.WriteLine($"  --feed URL    Feed URL (default: {FeedUrlEnvironmentVariable})");
        writer.WriteLine($"  --auth VALUE  Raw Authorization value (default: {AuthorizationEnvironmentVariable})");
        writer.WriteLine("  --no-auth     Do not send Authorization, overriding RCE3_AUTH");
        writer.WriteLine("  -h, --help    Show this help");
    }

    private static bool TryReadValue(
        string[] args,
        ref int index,
        string option,
        out string? value,
        out string? error)
    {
        index++;
        if (index >= args.Length)
        {
            value = null;
            error = $"{option} requires a value.";
            return false;
        }

        value = args[index];
        error = null;
        return true;
    }

    private static bool TryNormalizeFeedUri(string value, out Uri feedUri, out string? error)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            || (!parsed.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            feedUri = null!;
            error = "The feed URL must be an absolute HTTP or HTTPS URL.";
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment))
        {
            feedUri = null!;
            error = "The feed URL must not contain a query string or fragment.";
            return false;
        }

        var path = parsed.AbsolutePath.TrimEnd('/');
        var lastSegment = path[(path.LastIndexOf('/') + 1)..];
        if (!Guid.TryParse(Uri.UnescapeDataString(lastSegment), out _))
        {
            feedUri = null!;
            error = "The feed URL path must end with a feed GUID.";
            return false;
        }

        var builder = new UriBuilder(parsed)
        {
            Path = path
        };
        feedUri = builder.Uri;
        error = null;
        return true;
    }
}
