# Agent Notes for RCE3

## Project Purpose

RCE3 is a deliberately tiny ASP.NET Core feed server for ephemeral fan-out messaging. Clients POST an arbitrary raw request body to a feed, and each long-polling reader for that feed receives its own queued copy of every message. Keep the server format-free: no schemas, message envelopes, sender identities, receiver identities, acknowledgements, or client protocol layers unless the user explicitly asks for them.

The public contract is intentionally small:

- `GET /` creates a random feed and redirects to it.
- `GET /{feedGuid}` creates or touches a feed and returns plain text help.
- `POST /{feedGuid}` publishes the raw body to all readers of the feed.
- `GET /{feedGuid}/{readerGuid}` long-polls one queued message for that reader.
- `GET /{feedGuid}/{readerGuid}/reset` clears that reader queue, makes it usable again, and redirects to the reader URL.

Feed and reader IDs must parse as C# `Guid` values. Missing feeds are created by the first valid request.

## Repository Layout

- `README.MD` describes the current API and curl examples.
- `docs/my_plan.txt` and `docs/ai_plan.html` are planning references for product intent and edge cases.
- `src/FeedServer/FeedServer.slnx` is the solution file.
- `src/FeedServer/FeedServer` contains the ASP.NET Core app.
- `src/FeedServer/FeedServer.Tests` contains xUnit tests using `Microsoft.AspNetCore.Mvc.Testing`.
- `scripts/smoke-test.ps1` runs an end-to-end local smoke test against a running server.
- `scripts/package-release.ps1` runs tests, publishes the app, and zips a release artifact.
- `src/Examples` may be added for client examples; see the examples guidance below.

## Build and Test

Run from the repository root unless there is a good reason not to:

```powershell
dotnet test src\FeedServer\FeedServer.slnx
dotnet run --project src\FeedServer\FeedServer\FeedServer.csproj
.\scripts\smoke-test.ps1 -BaseUrl http://localhost:5137
.\scripts\package-release.ps1 -Runtime win-x64 -Configuration Release
```

The app targets `net10.0`. The default server URL is `http://0.0.0.0:5137` unless `urls` or `ASPNETCORE_URLS` is configured.

## Configuration

Runtime options are bound from the `FeedServer` config section and validated on startup:

- `Port`, default `5137`.
- `PollTimeout`, default `00:00:30`.
- `FeedTtl`, default `1.00:00:00`.
- `MaxMessageSizeBytes`, default `1048576`.
- `MaxQueuedMessagesPerReader`, default `1024`.
- `RequestHeadersTimeout`, default `00:00:30`.
- `MinRequestBodyDataRateBytesPerSecond`, default `240`.
- `MinRequestBodyDataRateGracePeriod`, default `00:00:10`.

Request body size is enforced both through Kestrel limits and by `FeedRequestBodyReader`, including unknown-length streaming bodies.

## Authorization Contract

The first valid request to a feed fixes its access mode for the life of that feed:

- No `Authorization` header creates an open feed.
- A single non-empty `Authorization: <key>` header creates a protected feed.
- Open feeds reject every request that includes `Authorization` with `403 Forbidden`.
- Protected feeds require the same raw header value on every endpoint; missing or mismatched keys return `401 Unauthorized`.
- Multiple `Authorization` values or blank authorization values are invalid and return `401 Unauthorized`.

Implementation detail: `FeedAuthorizationKey` hashes the raw UTF-8 authorization header value with SHA-256 and compares hashes using fixed-time comparison. Do not store or log raw keys.

## Feed and Reader Behavior

- `FeedStore` owns in-memory feed state in a `ConcurrentDictionary`.
- Feeds are memory-only and disappear on process restart.
- Valid GET or POST activity touches a feed and extends its TTL.
- `FeedExpirationService` periodically removes inactive feeds and completes pending reader polls.
- Expired feeds may be recreated later with a different access mode.
- `FeedState.Publish` fans each message out to all current readers and returns the count of readers that accepted it.
- Each reader has an independent bounded `Channel<FeedMessage>`.
- Readers receive messages one at a time, preserving the original body bytes and `Content-Type`.
- A reader queue that overflows becomes unusable and reads return `500 Internal Server Error` until `GET /{feedGuid}/{readerGuid}/reset`.
- Reset completes the previous queue, clears queued messages, clears unusable state, and redirects to the canonical reader URL.
- Because the service has no sender identity, clients can receive their own posted messages.

## Coding Style

- Follow the existing C# style: file-scoped namespaces, nullable enabled, implicit usings, small sealed types where appropriate, and straightforward minimal API endpoints.
- Keep the public API plain and predictable. Prefer text/plain help and ASP.NET problem responses over custom response envelopes.
- Prefer focused helpers like `FeedRouteParser`, `FeedAuthorizationKey`, and `FeedRequestBodyReader` when behavior has a clear boundary.
- Preserve concurrency and cancellation semantics. Long polls should return `204 No Content` on timeout, cancellation, feed expiration, reset, or app shutdown when no message is delivered.
- Keep request logging useful but avoid logging message bodies or authorization secrets.
- Do not add persistence, user accounts, message types, or per-reader auth unless the user explicitly changes the product direction.

## Examples Guidance

When adding or changing examples under `src/Examples`, make protected-feed examples secure by default and easy to point at a known feed:

- At startup or argument parsing time, read the auth key from the environment variable `RCE3_AUTH`.
- Use `RCE3_AUTH` as the default `Authorization` value when it is set.
- At startup or argument parsing time, read the default feed URL from the environment variable `RCE3_FEED_URL`.
- Use `RCE3_FEED_URL` as the default feed URL when it is set.
- Allow explicit later options, flags, config values, or command-line arguments to override these defaults.
- If neither `RCE3_AUTH` nor an explicit override is supplied, examples may demonstrate open-feed behavior only when that is clearly intentional.
- Do not hard-code real keys in examples, tests, docs, or scripts.

This convention lets local examples run against a known protected feed by default without requiring users to paste secrets or feed URLs into source files.

## Testing Expectations

Before finishing changes that touch behavior, run:

```powershell
dotnet test src\FeedServer\FeedServer.slnx
```

Add or update tests when changing:

- Route parsing or endpoint status codes.
- Authorization mode creation or enforcement.
- Long-poll timeout, cancellation, reset, or expiration behavior.
- Request body size handling.
- Reader queue overflow behavior.
- Feed TTL or cleanup behavior.
- Example code that depends on `RCE3_AUTH` or `RCE3_FEED_URL` defaults or overrides.

Use short test timeouts through in-memory configuration overrides, as existing endpoint tests do with `FeedServer:PollTimeout`.

## Line Endings and Diffs

Most existing repository text files use CRLF. Before editing existing text files, detect their line endings and preserve them exactly. After edits, run:

```powershell
git diff --check
```

Avoid unrelated formatting churn and do not normalize line endings unless explicitly asked.
