# Agent Guide for RCE3

## Mission and boundaries

RCE3 is a deliberately tiny ASP.NET Core server for ephemeral fan-out messaging. Clients POST arbitrary bytes to a feed. Every reader that already exists on that feed gets an independent queued copy and retrieves messages one at a time through long polling.

Preserve that simplicity. Do not add schemas, envelopes, message types, sender or receiver identities, acknowledgements, persistence, accounts, dashboards, or client protocol layers unless the user explicitly changes the product direction. Device-specific behavior belongs in clients.

The public API is exactly:

- `GET /` creates a random feed and returns a `302` redirect to it.
- `GET /{feedGuid}` creates or touches a feed and returns plain-text help.
- `POST /{feedGuid}` publishes the raw body to all current readers.
- `GET /{feedGuid}/admin` returns a public browser debug client without creating or touching a feed.
- `GET /{feedGuid}/{readerGuid}` creates or touches a reader and long-polls one queued message.
- `GET /{feedGuid}/{readerGuid}/reset` creates or resets a reader and redirects to its canonical URL.

Feed and reader route values must parse with C# `Guid.TryParse`. Do not add unrelated public endpoints such as health, diagnostics, or OpenAPI routes without an explicit request.

## Documentation authority

- `README.MD` is the current user and operator documentation.
- `docs/my_plan.txt` records the product intent and non-goals.
- `docs/ai_plan.html` is a historical planning reference. It is useful for rationale, but `README.MD` and the tested contract take precedence for current behavior.
- Update documentation and tests together when the public behavior changes.

Examples must create a reader before publishing. A POST is delivered only to readers that exist when it is accepted; the server does not retain pre-subscription history.

## Repository map

- `src/FeedServer/FeedServer.slnx`: solution.
- `src/FeedServer/FeedServer`: ASP.NET Core application.
- `src/FeedServer/FeedServer.Tests`: xUnit integration and unit tests.
- `src/FeedServer/FeedServer/FeedEndpoints.cs`: minimal API routes, response mapping, and bounded request-body reader.
- `src/FeedServer/FeedServer/FeedStore.cs`: feed lookup, authorization, activity, and expiration coordination.
- `src/FeedServer/FeedServer/FeedState.cs`: feed and bounded reader-queue state.
- `src/FeedServer/FeedServer/FeedExpirationService.cs`: periodic inactive-feed cleanup.
- `src/FeedServer/FeedServer/FeedServerLog.cs`: source-generated structured logging definitions.
- `scripts/smoke-test.ps1`: end-to-end open/protected feed check against a running server.
- `scripts/package-release.ps1`: test, publish, and zip a framework-dependent single-file release.

## Build, test, and run

Run commands from the repository root:

```powershell
dotnet test src\FeedServer\FeedServer.slnx
dotnet build src\FeedServer\FeedServer.slnx -c Release -warnaserror
dotnet run --project src\FeedServer\FeedServer\FeedServer.csproj --no-launch-profile
.\scripts\smoke-test.ps1 -BaseUrl http://localhost:5137
.\scripts\package-release.ps1 -Runtime win-x64 -Configuration Release
```

The project targets `net10.0`. With no `urls` or `ASPNETCORE_URLS` override, it binds to `http://0.0.0.0:{Port}`. The launch profiles bind localhost URLs and therefore override `Port` when they are active.

Before finishing any behavior change, run the full solution tests and `git diff --check`. For release/script changes, also run the affected script when practical. Do not leave generated `artifacts/` output in a patch.

## Configuration

Options bind from the `FeedServer` section and are validated at startup:

- `Port`: `5137`, range 1-65535.
- `PollTimeout`: `00:00:30`, positive.
- `FeedTtl`: `1.00:00:00`, positive.
- `MaxMessageSizeBytes`: `1048576`, range 1 through `int.MaxValue`.
- `MaxQueuedMessagesPerReader`: `1024`, positive.
- `RequestHeadersTimeout`: `00:00:30`, positive.
- `MinRequestBodyDataRateBytesPerSecond`: `240`, positive.
- `MinRequestBodyDataRateGracePeriod`: `00:00:10`, positive.

Keep Kestrel's request-body limit and `FeedRequestBodyReader`'s streaming limit aligned. Kestrel covers normal hosted requests; the explicit reader also covers unknown-length bodies and in-memory test hosting.

## Authorization contract

The admin page ignores Authorization and never creates, touches, or accesses a feed. For feed-data routes, the first valid request fixes a feed's mode for that feed's lifetime:

- No Authorization header creates an open feed.
- Exactly one non-empty raw Authorization value creates a protected feed.
- Open feeds reject every request containing Authorization with `403 Forbidden`.
- Protected feeds return `401 Unauthorized` for a missing or mismatched key.
- Blank or multiple Authorization values return `401 Unauthorized` and do not create a feed.

`FeedAuthorizationKey` hashes the raw UTF-8 header value with SHA-256 and uses fixed-time hash comparison. Never store or log raw keys. Never log message bodies.

For POST, preserve the deliberate validation order:

1. Parse the route and Authorization header.
2. Authorize an existing feed before reporting payload-size errors.
3. Reject a declared or streamed oversized body without creating a missing feed.
4. Create/authorize the feed only after the complete body is accepted.
5. Publish and touch the feed.

This order prevents an unauthorized caller from learning protected-feed limits through a different response and prevents invalid POSTs from claiming a feed's access mode.

## Feed and expiration invariants

- `FeedStore` owns feeds in a `ConcurrentDictionary<Guid, FeedState>`.
- State is memory-only and is lost on restart.
- Successful GET activity and accepted POST activity touch a feed; rejected requests do not.
- Activity time must never move backward when older concurrent requests finish after newer ones.
- `AuthorizeExisting` is a preliminary POST check and intentionally does not touch the feed.
- A feed is expired lazily during access and periodically by `FeedExpirationService`.
- The store and cleanup timer use the injected `TimeProvider`; keep expiration testable without day-long tests.
- Expiration removes the exact state instance before completing its readers, preventing an old cleanup attempt from deleting a newly recreated feed.
- Expired feeds may later be recreated with a different access mode.

Preserve concurrency and cancellation behavior. A pending poll with no delivered message resolves as no content on timeout, reset, expiration, application shutdown, or request cancellation where a response can still be written.

## Reader and message invariants

- Each reader GUID maps to one independent bounded `Channel<FeedMessage>`.
- A reader receives only messages published after that reader exists.
- Each successful read removes exactly one message in FIFO order.
- Body bytes and the complete Content-Type value pass through unchanged. A missing Content-Type becomes `application/octet-stream` on delivery.
- Multiple simultaneous polls for one reader may consume different messages; `SingleReader` must remain false unless the API contract changes.
- When a queue is full, the overflow-causing message is rejected for that reader, the channel is completed, and the reader becomes unusable.
- Reads from an unusable reader return `500 Internal Server Error` until reset. Other readers remain usable.
- Reset atomically installs a fresh queue, clears unusable state, increments the generation, and completes the previous queue. The generation check prevents an old pending poll from returning an old queued message after reset.
- Expiring a feed completes all of its current reader queues.

Because there is no sender identity, clients can receive their own posts. Do not infer sender suppression from HTTP connection, headers, or reader IDs.

## HTTP behavior

- Help, the admin page, accepted POSTs, and message delivery return `200 OK`.
- An empty long poll returns `204 No Content`.
- Root creation and reader reset use `302 Found` with root-relative canonical locations.
- Malformed documented routes return ASP.NET problem responses with `400 Bad Request`.
- Authorization failures return problem responses with `401` or `403` as described above.
- Oversized bodies return a `413` problem response.
- Unusable readers return a useful `500` problem response that points to reset.
- POST returns the count of reader queues that accepted the message.

Keep the surface plain: text help, raw message responses, and standard problem details. Do not wrap successful messages in JSON or invent a public data model.

## Coding conventions

- Follow the existing C# style: file-scoped namespaces, nullable enabled, implicit usings, small sealed state types, and straightforward minimal API handlers.
- Prefer focused helpers for route parsing, authorization parsing, request-body bounds, and state transitions.
- Preserve async cancellation and channel-completion semantics.
- Use the source-generated `FeedServerLog` methods for structured logs with feed/reader IDs and counts. Keep empty long-poll completions at Debug to avoid routine production noise, and never include secrets or bodies.
- Avoid unrelated dependency additions. Production code currently needs only the ASP.NET Core shared framework.

## Test expectations

Add or update tests whenever changing:

- Route shapes, redirects, status codes, help text, or response content types.
- Admin page state neutrality, browser-client behavior, or safe text rendering.
- Open/protected feed creation and every authorization failure case.
- POST validation order or missing-feed creation behavior.
- Fan-out, FIFO delivery, concurrent publishing, or queue overflow/reset behavior.
- Long-poll wake-up, timeout, cancellation, expiration, reset, or shutdown behavior.
- Feed touches, TTL cleanup, or injected-time behavior.
- Request size and slow-client configuration.
- Smoke or packaging scripts.

Use short in-memory `FeedServer:PollTimeout` overrides in endpoint tests. Use an injected/manual `TimeProvider` for TTL tests. Avoid timing-only sleeps when a deterministic queue setup or completion signal can establish the same precondition.

## Examples and secrets

Do not hard-code real keys in examples, tests, docs, scripts, or the admin page. The browser debug client accepts a key only through its in-memory input and must never place it in URLs, browser storage, logs, or rendered output.

## PowerShell and release safety

- Check `$LASTEXITCODE` after native commands whose failure must stop a script; `$ErrorActionPreference = "Stop"` alone is not sufficient in all supported PowerShell versions.
- Validate values interpolated into deletion paths.
- Resolve and verify recursive deletion targets before `Remove-Item -Recurse`.
- Keep the publish directory a direct child of the requested output root.

## Line endings and diff hygiene

Existing repository text files use CRLF except `docs/ai_plan.html`, which uses LF. Detect the target file before editing and preserve its exact convention. Never leave mixed line endings or introduce line-ending-only churn.

The working tree may contain user changes. Inspect `git status`, preserve unrelated edits, and never use destructive reset/checkout commands to discard them. Finish with:

```powershell
git diff --check
git status --short
```
