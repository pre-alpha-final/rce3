# RCE3 - Remote Controlled Electronics v3

After two somewhat successful attempts, it's time to drop all pretense of professionalism and goblin-engineer something actually worth using. A tiny, wysiwyg feed server; POST a raw request body to a feed and every long-polling "connected" reader gets it. There are no message schemas, envelopes, sender identities, receiver identities, acknowledgements, or required client libraries.

## HTTP API

### Core messaging requests

RCE3's protocol revolves around two requests: readers keep a GET pending for the next message, and publishers POST raw bytes to the feed.

| Request | Behavior |
| --- | --- |
| `GET /{feedGuid}/{readerGuid}` | Create or touch a reader, then long-poll for exactly one queued message. Returns the raw message, or `204 No Content` if the poll ends without one. |
| `POST /{feedGuid}` | Publish the message body to every reader that currently exists. Returns the number of reader queues that accepted the message. |

A missing feed is created by the first valid POST, reader poll, or reader reset request. A POST is valid for creation only after its authorization and body-size checks pass.

### Supporting requests

These routes create convenient IDs, explain the service, provide a browser client, or manage reader queues.

| Request | Behavior |
| --- | --- |
| `GET /` | Generate a random feed ID and redirect to `/{feedGuid}` without creating the feed. |
| `GET /{feedGuid}` | Return public plain-text help without creating, touching, or authorizing the feed. |
| `GET /{feedGuid}/admin` | Open a browser debug client without creating or touching the feed. |
| `GET /{feedGuid}/{readerGuid}/reset` | Create or reset a reader, discard its queued messages, make it usable after overflow, and redirect to the reader URL. |
| `POST /{feedGuid}/kill-reader-queues` | Discard every reader queue on the feed. Pending polls end with `204 No Content` and the next poll for each reader creates a fresh queue. |

Send the queue clear request with an empty body. It returns `Killed N reader queue(s).` as plain text, with a count of zero for a missing or expired feed. The feed keeps its access mode and expiration deadline.

## Authorization

Root, help, and admin requests ignore Authorization and leave feed state unchanged. The first valid POST, reader poll, or reader reset request fixes a feed's access mode until that feed expires:

- No `Authorization` header creates an open feed. Open feeds reject requests that include the header with `403 Forbidden`.
- One non-empty `Authorization: <key>` header creates a protected feed. Protected feeds return `401 Unauthorized` for a missing or mismatched key.
- Blank or multiple Authorization values are invalid, return `401 Unauthorized`, and do not create a feed.

## Responses and limits

- `200 OK`: help, admin page, accepted publish, queue clear, or delivery of one message.
- `204 No Content`: long-poll timeout, cancellation, feed expiration, reset, queue clear, or shutdown when no message was delivered.
- `302 Found`: random feed URL or reader reset redirect.
- `400 Bad Request`: malformed GUID or path, or invalid gzip body.
- `401 Unauthorized`: invalid Authorization header or failed protected-feed authorization.
- `403 Forbidden`: an Authorization header was sent to an open feed.
- `410 Gone`: the reader queue overflowed and must be reset before polling can resume.
- `413 Payload Too Large`: the uploaded body exceeds the transfer limit, including an unknown-length upload.
- `415 Unsupported Media Type`: unsupported, blank, or stacked `Content-Encoding` values.

An overflow makes that reader unusable and its queued messages unavailable. It does not affect other readers. The overflow-causing message is not accepted by that reader.

## Browser debug client

Open `/{feedGuid}/admin` in a browser to send and receive UTF-8 text messages through the existing feed API. The page gets a fresh reader ID when it is rendered; Connect creates or resets that reader before polling so it exists before any message is published from the page.

The page itself is public and does not create, authorize, inspect, or touch the feed. For a protected feed, enter the raw Authorization value before connecting or clearing reader queues. The value stays in page memory and is sent only in the Authorization header of reset, polling, publish, and queue clear requests. It is not read from `RCE3_AUTH`, saved in browser storage, or placed in the URL.

**Kill reader queues** asks for confirmation, then clears the feed's queues and shows how many it removed. Anyone with the URL of an open feed can use it. If the page is connected, its next poll recreates its own queue.

The debug client displays received bodies as text.

## MCP bridge

[Rce3Mcp](src/Examples/Rce3Mcp/README.MD) is a standalone .NET 10 bridge exposing `send_message`, `get_messages`, and `get_status` over MCP Streamable HTTP at `http://localhost:5138/mcp`. Configure one feed with `RCE3_FEED` and optional raw `RCE3_AUTH`. A background reader collects messages into bounded, shared in-memory history; LLM clients retrieve recent messages or continue from their own cursors.

## Example app reader identities

Youtube, Mudslide, TabletUI, and Rce3Mcp each generate a reader ID once, save it before connecting, and reuse it across restarts. Startup polls directly; it never resets a retained queue. Overflow recovery (`410 Gone`) resets the same reader and discards its unusable queue.

- Youtube, Mudslide, and Rce3Mcp store a plain GUID in `readerid` beside the executable, independent of the shell's current directory. The directory must be writable on first use. Preserve the file during updates and exclude it when copying an installation to another device. Invalid or inaccessible files stop startup rather than silently replacing the identity. Delete the file deliberately to start a new queue.
- TabletUI stores its ID under `TabletUI.readerid.v1` in local storage for that browser profile and site. Reconnecting, reloading, and forgetting connection settings preserve it. Clearing site data creates a new identity. If identity storage is unavailable or invalid, connection stops with a visible error.

Two running copies sharing an ID on the same feed consume from the same queue and split messages; use separate installations or browser profiles for independent readers. A reader ID can be reused on different feeds because queues are scoped to feeds.

Messages queued while an app is stopped remain available only while the server retains the feed and the queue has not overflowed. Server restart, feed expiration, overflow, and a queue clear all discard queued messages. A message already removed by an interrupted poll is not replayed: RCE3 has no acknowledgements or durable delivery. Messages published before the first reader poll are not retained for that reader.

## License

MIT
