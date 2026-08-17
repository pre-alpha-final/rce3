# RCE3 - Remote Controlled Electronics v3

After two somewhat successful attempts, it's time to drop all pretense of professionalism and goblin-engineer something actually worth using. A tiny, wysiwyg feed server; POST a raw request body to a feed and every long-polling "connected" reader gets it. There are no message schemas, envelopes, sender identities, receiver identities, acknowledgements, or required client libraries.

## HTTP API

### Core messaging requests

RCE3's protocol revolves around two requests: readers keep a GET pending for the next message, and publishers POST raw bytes to the feed.

| Request | Behavior |
| --- | --- |
| `GET /{feedGuid}/{readerGuid}` | Create or touch a reader, then long-poll for exactly one queued message. Returns the raw message, or `204 No Content` if the poll ends without one. |
| `POST /{feedGuid}` | Publish the raw request body to every reader that currently exists. Returns the number of reader queues that accepted the message. |

A missing feed is created by the first valid request. A POST is valid for creation only after its authorization and body-size checks pass.

### Supporting requests

These routes create convenient IDs, explain the service, provide a browser client, or recover a reader.

| Request | Behavior |
| --- | --- |
| `GET /` | Create a random feed and redirect to `/{feedGuid}`. |
| `GET /{feedGuid}` | Return public plain-text help. |
| `GET /{feedGuid}/admin` | Open a browser debug client without creating or touching the feed. |
| `GET /{feedGuid}/{readerGuid}/reset` | Create or reset a reader, discard its queued messages, make it usable after overflow, and redirect to the reader URL. |

## Authorization

The first valid feed request fixes a feed's access mode until that feed expires:

- No `Authorization` header creates an open feed. Open feeds reject requests that include the header with `403 Forbidden`.
- One non-empty `Authorization: <key>` header creates a protected feed. Protected feeds return `401 Unauthorized` for a missing or mismatched key.
- Blank or multiple Authorization values are invalid, return `401 Unauthorized`, and do not create a feed.

## Responses and limits

- `200 OK`: help, admin page, accepted POST, or delivery of one message.
- `204 No Content`: long-poll timeout, cancellation, feed expiration, reset, or shutdown when no message was delivered.
- `302 Found`: feed creation or reader reset redirect.
- `400 Bad Request`: malformed GUID or path.
- `401 Unauthorized`: invalid Authorization header or failed protected-feed authorization.
- `403 Forbidden`: an Authorization header was sent to an open feed.
- `413 Payload Too Large`: the body exceeds `MaxMessageSizeBytes`, including an unknown-length streaming body.
- `410 Gone`: the reader queue overflowed and must be reset before polling can resume.

An overflow makes that reader unusable and its queued messages unavailable. It does not affect other readers. The overflow-causing message is not accepted by that reader.

## Browser debug client

Open `/{feedGuid}/admin` in a browser to send and receive UTF-8 text messages through the existing feed API. The page gets a fresh reader ID when it is rendered; Connect creates or resets that reader before polling so it exists before any message is published from the page.

The page itself is public and does not create, authorize, inspect, or touch the feed. For a protected feed, enter the raw Authorization value before connecting. The value stays in page memory and is sent only in the Authorization header of reset, polling, and publish requests. It is not read from `RCE3_AUTH`, saved in browser storage, or placed in the URL.

This is a debug client, not a privileged administration or monitoring interface. It displays received bodies as text and does not expose server statistics.

## License

MIT
