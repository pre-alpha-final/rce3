# FetchProxy

Paste [fetchproxy.js](fetchproxy.js) into the Chrome or Firefox DevTools console of the tab whose `fetch()` you want to use. It listens for URL requests on an RCE3 feed and publishes the browser's responses to the same feed.

## Configuration and startup

First set these variables separately in the page console, using your own feed URL and optional raw Authorization value:

```javascript
var RCE3_FEED = "http://localhost:5137/00000000-0000-0000-0000-000000000000";
// For a protected feed only:
var RCE3_AUTH = "replace-with-your-key";
```

For an open feed, omit `RCE3_AUTH` or explicitly set it to `""`. Both `var` globals and top-level `let`/`const` variables work. The snippet never supplies or overwrites these variables. Missing `RCE3_FEED` or invalid configuration throws before any connection is attempted; the key is never printed.

Then paste the entire snippet. It starts a direct long poll with a new random reader ID. Wait until that reader's GET is pending in the Network panel before sending a command. RCE3 only delivers messages to readers that already exist, and it retains no pre-subscription history. If a separate client will receive responses, create that client's reader before publishing commands too.

The feed key is sent only in Authorization headers on feed requests. It is never added to target requests. Feed requests omit browser cookies; target requests use normal Fetch defaults, including same-origin cookies, redirect following, and relative-URL resolution against the page's base URL.

## Messages

Publish a UTF-8 text message to the feed:

```text
fetchproxy: /api/example
```

The exact prefix is `fetchproxy: `, including a mandatory literal space after the colon. It is case-sensitive; commands without that space are ignored. The snippet trims the URL, runs one GET, reads its complete body, and publishes a message beginning with the original requested URL:

```text
fetchproxyresponse: /api/example
{
  "url": "https://example.com/api/example",
  "status": 200,
  "statusText": "OK",
  "ok": true,
  "redirected": false,
  "type": "basic",
  "headers": {
    "content-type": "application/json"
  },
  "body": "{\"example\":true}"
}
```

`url` inside the JSON is the final response URL after redirects. `body` is always a string, including for JSON, HTML, HTTP errors, and empty responses. It uses Fetch's UTF-8 text decoding; binary bytes are not preserved. HTTP errors such as 404 include their status, headers, and body rather than becoming fetch errors.

Invalid URLs, network failures, and timeouts return the same response prefix and an error object:

```text
fetchproxyresponse: /api/example
{
  "error": {
    "name": "TimeoutError",
    "message": "Target fetch exceeded 10 seconds."
  }
}
```

The 10-second timeout includes reading the response body. Commands are processed one at a time: no further feed poll starts until the current target request and response publication finish. Unrelated messages, differently cased prefixes, and `fetchproxyresponse:` messages are ignored, including the snippet's own responses. Multiple tabs running the snippet independently answer the same requests.

## Stopping and failures

```javascript
RCE3_FETCH_PROXY.stop();
```

Re-pasting stops the previous run and creates a fresh reader. Closing, reloading, or navigating away from the tab stops the run and aborts outstanding work. Returning through browser history does not restart it. Closing DevTools alone leaves it running; background tabs may be throttled or suspended by the browser. No worker, extension, or browser storage is used, and old queues are not resumed after restarting.

Empty polls (`204`) continue normally. Network errors and transient feed-read responses (`408`, `429`, `5xx`) retry after one second. Reader overflow (`410`) resets the same reader and discards its queue. Authorization failures and other permanent feed errors stop with a console error.

Each response is published once. A failed publication stops with a console error; a network failure may mean it was delivered despite the failure. Responses are not retried, truncated, or split. The serialized message must fit the feed's configured message limit (1 MiB by default), or publication returns `413` and the snippet stops.

The snippet uses the page's ordinary Fetch capabilities. CORS, CSP, mixed-content rules, and other browser policies can block requests. Headers contain only what Fetch exposes, not every wire-level header; for example, `Set-Cookie` is unavailable. See [Using the Fetch API](https://developer.mozilla.org/en-US/docs/Web/API/Fetch_API/Using_Fetch). Choose a tab whose browser policies permit access to both the feed and the target.

## Tests

From the repository root, run:

```powershell
node --test src/Examples/FetchProxy/fetchproxy.tests.mjs
```

These dependency-free tests run the actual snippet in an isolated JavaScript context with mocked Fetch, timers, and page events.
