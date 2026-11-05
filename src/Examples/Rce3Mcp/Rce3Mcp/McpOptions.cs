namespace Rce3Mcp;

internal sealed class McpOptions
{
    public int HistoryCapacity { get; init; } = 100;
    public long MaxHistoryBytes { get; init; } = 8 * 1024 * 1024;
    public int MaxMessageSizeBytes { get; init; } = 1024 * 1024;

    public static McpOptions Load(IConfiguration configuration)
    {
        McpOptions options;
        try
        {
            options = configuration.GetSection("Rce3Mcp").Get<McpOptions>() ?? new();
        }
        catch (InvalidOperationException)
        {
            throw new ArgumentException("Rce3Mcp limits must be valid integers.");
        }
        if (options.HistoryCapacity <= 0 || options.MaxMessageSizeBytes <= 0
            || options.MaxHistoryBytes < options.MaxMessageSizeBytes)
        {
            throw new ArgumentException("Rce3Mcp requires positive HistoryCapacity and MaxMessageSizeBytes, and MaxHistoryBytes >= MaxMessageSizeBytes.");
        }
        return options;
    }
}

internal sealed record FeedConnection(Uri FeedUri, string? Authorization)
{
    public static FeedConnection Load(IConfiguration configuration)
    {
        var value = configuration["RCE3_FEED"];
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || !Guid.TryParse(Uri.UnescapeDataString(uri.AbsolutePath.TrimEnd('/').Split('/')[^1]), out _))
        {
            throw new ArgumentException("RCE3_FEED must be an absolute HTTP(S) feed URL ending in a GUID, without credentials, query, or fragment.");
        }
        var authorization = configuration["RCE3_AUTH"];
        if (authorization is not null && authorization.Length != 0
            && (string.IsNullOrWhiteSpace(authorization) || authorization.Contains('\r') || authorization.Contains('\n')))
        {
            throw new ArgumentException("RCE3_AUTH must be empty or one non-blank Authorization header value without line breaks.");
        }
        return new(new Uri(uri.AbsoluteUri.TrimEnd('/')), string.IsNullOrEmpty(authorization) ? null : authorization);
    }
}
