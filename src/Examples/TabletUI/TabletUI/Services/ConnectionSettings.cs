namespace TabletUI.Services;

public sealed record ConnectionSettings(int Version, string RCE3_FEED, string RCE3_AUTH)
{
    public static bool TryCreate(string feed, string auth, out ConnectionSettings? settings, out string error)
    {
        settings = null;
        error = "Enter an HTTP(S) feed URL ending in a feed GUID, without credentials, query, or fragment.";
        if (!Uri.TryCreate(feed.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
        {
            return false;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        if (!Guid.TryParse(Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..]), out _))
        {
            return false;
        }

        if (auth.Length > 0 && (string.IsNullOrWhiteSpace(auth) || auth.Contains('\r') || auth.Contains('\n')))
        {
            error = "Enter a nonblank authorization key on one line, or leave it empty for an open feed.";
            return false;
        }

        settings = new ConnectionSettings(1, uri.AbsoluteUri.TrimEnd('/'), auth);
        error = string.Empty;
        return true;
    }
}
