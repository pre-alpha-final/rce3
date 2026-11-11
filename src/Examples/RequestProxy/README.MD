# Request Proxy

Request Proxy is a .NET 10 Windows console app. It loads browser sessions from HAR files and uses them to make GET requests sent over an RCE3 feed.

## Build and start

From the repository root, publish the example.

~~~powershell
dotnet publish src/Examples/RequestProxy/RequestProxy/RequestProxy.csproj -c Release
~~~

`RequestProxy.exe` and its companion files are in `src/Examples/RequestProxy/RequestProxy/bin/Release/net10.0-windows/publish/`. The machine running it needs the .NET 10 runtime and a writable installation directory for `readerid`.

Set `RCE3_FEED` to the full feed URL and, for a protected feed, `RCE3_AUTH` to the raw feed key. Leave `RCE3_AUTH` unset or empty for an open feed. To launch by dropping a file from Explorer, set the variables in **Edit environment variables for your account** so Explorer can pass them to the app. If Explorer still has the old values, sign out and back in. You can also launch from a PowerShell session that already has the variables set.

1. In your browser, log in and make requests to the endpoints you want to use. In Chrome DevTools, enable **Allow to generate HAR with sensitive data** under **Settings → Preferences → Network**, then choose **Export HAR (with sensitive data)** in the Network panel. [Chrome DevTools documentation](https://developer.chrome.com/docs/devtools/network/reference)
2. Drop the HAR onto `RequestProxy.exe`, or run `.\RequestProxy.exe "C:\path with spaces\session.har"` from the publish directory. Wait for `Reader ready.`
3. Drop more files onto the same executable to load them into the running process. The later launch exits after queuing the paths. Watch the original console for the import results. Use the same Windows account, session, and elevation level for each launch.

Each new HAR replaces the sessions for the origins it contains. An origin includes the scheme, host, and port. Request Proxy picks headers from the exact URL, then the same path, then the closest whole-segment path match on that origin. The last capture wins ties. Cookies are maintained in memory and updated from target responses. Recorded cookie paths apply when present. Cookies captured without path information are scoped to `/` on the captured origin.

The HAR can contain browser credentials. Every feed subscriber can request data using the loaded sessions and read the replies. With an open feed, anyone who has the feed URL can do so.

## Send a request

Wait for `Reader ready.`, then connect a separate reply reader before sending the command. For example, open `/{feedGuid}/admin`, enter the raw feed key if needed, select **Connect**, and wait for it to connect. Send a UTF-8 message containing the case-sensitive prefix and an absolute URL on an imported origin.

~~~text
requestproxy: https://example.com/api/profile
~~~

The reply starts with the requested URL, followed by a newline and a JSON result.

~~~text
requestproxy_response: https://example.com/api/profile
{"status":200,"statusText":"OK","ok":true,"headers":{"content-type":["application/json"]},"body":"{\"name\":\"Example\"}"}
~~~

The first target response is returned, including HTTP errors and redirects with their `Location` header. To follow a redirect, send another command for its destination if that origin is loaded. Bodies are UTF-8 text. Invalid URLs, unloaded origins, transport failures, timeouts, and oversized replies use an `error` object with `name` and `message`.

The app accepts commands up to 64 KiB and gives each target GET 10 seconds, including the response body. A reply can use up to 1 MiB including its prefix and JSON. The app attempts each reply publication once and reports failures in the console.

Press Ctrl+C to stop. At startup, Request Proxy uses its saved reader ID and resets that queue before polling, discarding commands queued while it was stopped. Drop the HAR files again to restore sessions after a restart.

## Tests

From the repository root, run `dotnet test src/Examples/RequestProxy/RequestProxy.slnx` on Windows.
