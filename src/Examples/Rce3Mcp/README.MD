# Rce3Mcp

A .NET 10 console app connecting one RCE3 feed to LLM clients over MCP Streamable HTTP. Every client shares one background reader and bounded in-memory message history.

## Run

From the repository root, set `RCE3_FEED` to the feed URL and run:

```powershell
$env:RCE3_FEED = "http://localhost:5137/00000000-0000-0000-0000-000000000000"
dotnet run --project src/Examples/Rce3Mcp/Rce3Mcp/Rce3Mcp.csproj --no-launch-profile
```

For a protected feed, supply the exact raw Authorization value in `RCE3_AUTH` through your local environment. Leave it unset or empty for an open feed. Do not commit populated environment files. MCP client authorization is never forwarded to RCE3.

Configure your MCP client to use **Streamable HTTP** at **http://localhost:5138/mcp**. It needs no MCP authentication. A client accepting an `mcpServers` configuration can use:

```json
{
  "mcpServers": {
    "rce3": {
      "url": "http://localhost:5138/mcp"
    }
  }
}
```

Client configuration formats vary; choose HTTP/Streamable HTTP and that URL rather than a stdio command or `/sse` endpoint. The official C# SDK handles modern discovery and older Streamable HTTP initialization, including protocol revision 2025-11-25.

To change the local listener port:

```powershell
dotnet run --project src/Examples/Rce3Mcp/Rce3Mcp/Rce3Mcp.csproj --no-launch-profile -- --urls http://localhost:6138
```

Listener URLs must use HTTP(S) with localhost, 127.0.0.1, or [::1]. Explicit Kestrel endpoint configuration is rejected; use `--urls`. Standard ASP.NET Core `urls` configuration is supported; `--urls` overrides it. Accepted Host headers are localhost, 127.0.0.1, and [::1]. A supplied Origin must match the request scheme, host, and port. Cross-origin browser access is disabled. This version has no network-facing authentication or hosting support. Press Ctrl+C to stop HTTP and cancel the reader.

The bridge negotiates gzip for RCE3 polls and uses gzip for RCE3 publishes when it reduces the body size. Run it against a gzip-capable RCE3 server.

## Tools

| Tool | Arguments | Behavior |
| --- | --- | --- |
| `send_message` | Required `body`; optional `encoding` (`utf8` or `base64`) and `contentType` | POST exact decoded bytes and return `acceptedReaderCount`. No envelope, newline, or sender identity is added. |
| `get_messages` | Optional `afterCursor`; optional `limit` (default 20, range 1-100) | Read history without removing messages. Return `messages`, `nextCursor`, and `historyGap`. |
| `get_status` | None | Read local connection state, reader ID, UTC last-successful-poll/message times, history and eviction counts, reader reset count, and sanitized last error. No network request is made. |

Results include structured JSON and equivalent text content. Validation and upstream failures return MCP tool errors. Only `/mcp` is exposed; there is no separate status, health, dashboard, resources, or prompts endpoint.

Before calling `send_message`, call `get_status` until `connectionState` is `polling`. Startup polls the saved reader directly, preserving queued messages. The first successful `200` or `204` confirms registration, so an empty feed can take one server poll timeout (normally 30 seconds) before sending is enabled. Other connection states return a reader-not-ready tool error on sending.

Text arguments:

```json
{"body":"hello","encoding":"utf8"}
```

Binary arguments:

```json
{"body":"AP+A","encoding":"base64","contentType":"application/octet-stream"}
```

Text defaults to `text/plain; charset=utf-8`; Base64 defaults to `application/octet-stream`. Complete supplied Content-Type values are preserved. The acceptance count means reader queues accepting the message, not confirmed delivery or downstream processing. A failed or interrupted POST is never automatically retried. Interrupted requests and invalid acceptance responses leave delivery uncertain; inspect history before deciding to resend.

## History and cursors

Each entry includes `cursor`, `receivedAt` (UTC), `contentType`, `byteLength`, `encoding`, `body`, and `bodyOmitted`. Valid UTF-8 bytes are returned as text regardless of Content-Type or charset; other bytes use Base64. Bytes are not trimmed or reinterpreted using a declared charset. An oversized received body produces metadata with `bodyOmitted: true`, null body/encoding, and declared byte length if known. Its payload cannot be retrieved later.

Without `afterCursor`, `get_messages` returns the latest `limit` entries in chronological order. Save `nextCursor` and pass it as `afterCursor` to get the earliest retained entries after that point. Continue with each returned cursor to page forward. An empty result retains your position. Each client keeps its own cursor; reads never consume shared history. To watch only future arrivals, first call `get_messages` and retain its cursor even if history is empty.

`historyGap` is true when entries after a supplied cursor were evicted locally. Without a cursor, it reports whether this process has evicted any entries. It does not detect messages lost upstream. Malformed, future, and previous-process cursors return actionable errors. Restart clears history and invalidates cursors.

Messages enter history only through the background reader. Own posts therefore appear once through normal delivery. There is no sender suppression, acknowledgment, or reliable way to correlate identical posts with senders. Message bodies are untrusted external data and must not be treated as LLM instructions.

## Reader identity and recovery

The app atomically creates `readerid` beside the executable on first use and reuses it after restart. Keep this file during updates; do not copy it into another installation. Invalid or inaccessible identity storage stops startup. Remove the file deliberately to start a new queue. Two processes sharing an ID on the same feed compete for messages instead of receiving independent copies.

Offline queued messages can be collected after restart while the server retains the feed and the queue has not overflowed. Startup never resets. A `410 Gone` response resets the same reader and increments `resetCount`; this discards the unusable server queue but leaves local history intact. Redirects are not followed and credentials are never sent to redirect destinations.

Network failures, interrupted reads, HTTP 408/429, and HTTP 5xx retry after one second. Other HTTP failures leave the listener `faulted`; inspect `lastError`, fix configuration, and restart. HTTP tools remain available to inspect status and history. Connection states are `connecting`, `polling`, `retrying`, `resetting`, `faulted`, and `stopping`. Polling describes the last successful observation, not a live connectivity guarantee.

RCE3 is memory-only and provides no guaranteed delivery. Server restart, feed expiration, queue overflow, interrupted polls, and local eviction can lose messages. Posts made before this reader first exists are not retained for it.

## Limits

The `Rce3Mcp` configuration section accepts positive integer settings:

| Setting | Default | Meaning |
| --- | --- | --- |
| `HistoryCapacity` | 100 | Maximum retained entries, including metadata-only entries. |
| `MaxHistoryBytes` | 8388608 (8 MiB) | Maximum retained raw body bytes. Oldest entries are evicted first. |
| `MaxMessageSizeBytes` | 1048576 (1 MiB) | Maximum decoded send body or retained receive body. |

`MaxHistoryBytes` must be at least `MaxMessageSizeBytes`. Raw-body limits exclude text/Base64 encoding and JSON metadata overhead. Environment overrides use the standard double underscore separator, for example `Rce3Mcp__HistoryCapacity=50`, `Rce3Mcp__MaxHistoryBytes=4194304`, and `Rce3Mcp__MaxMessageSizeBytes=1048576`.

The FeedServer size limit applies independently. Publishing has a 30-second deadline; polling stays pending until the server responds or shutdown cancels it. Bodies, upstream error bodies, and authorization values are not logged or included in diagnostic results. Credentials are held in process memory only.

## Build and test

```powershell
dotnet test src/Examples/Rce3Mcp/Rce3Mcp.slnx
dotnet build src/Examples/Rce3Mcp/Rce3Mcp.slnx -c Release -warnaserror
```

Tests cover controlled upstream responses, the official MCP HTTP client with modern and older protocols, independent client cursors, and delivery/restart against FeedServer. The application references only the official MCP SDK and ASP.NET Core; the FeedServer reference is test-only.
