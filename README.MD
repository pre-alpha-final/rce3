# RCE3 - Remote Controlled Electronics v3

After two somewhat successful attempts, it's time to drop all pretense of professionalism and goblin-engineer something actually worth using. A tiny, wysiwyg feed server; POST a raw request body to a feed and every long-pooling "connected" reader gets it. There are no message schemas, envelopes, sender identities, receiver identities, acknowledgements, or required client libraries.

## HTTP API

| Request | Behavior |
| --- | --- |
| `GET /` | Create a random feed and redirect to `/{feedGuid}`. |
| `GET /{feedGuid}` | Create or touch a feed and return plain-text help. |
| `POST /{feedGuid}` | Publish the raw request body to every current reader. |
| `GET /{feedGuid}/{readerGuid}` | Create or touch a reader and return one queued message, or `204 No Content` after the poll timeout. |
| `GET /{feedGuid}/{readerGuid}/reset` | Create or reset a reader, discard its queued messages, make it usable after overflow, and redirect to the reader URL. |

Feed and reader IDs must parse as C# `Guid` values. A missing feed is created by the first valid request. A POST is valid for creation only after its authorization and body-size checks pass.

Messages are fanned out only to readers that exist when the POST is accepted. Create a reader first by calling its reset endpoint or starting a long poll. Each reader has an independent FIFO queue, and clients can receive their own messages because the server has no sender identity.

The response to a reader preserves the posted `Content-Type`. If the POST did not include one, the response uses `application/octet-stream`.

## Authorization

The first valid request fixes a feed's access mode until that feed expires:

- No `Authorization` header creates an open feed. Open feeds reject requests that include the header with `403 Forbidden`.
- One non-empty `Authorization: <key>` header creates a protected feed. Protected feeds return `401 Unauthorized` for a missing or mismatched key.
- Blank or multiple Authorization values are invalid, return `401 Unauthorized`, and do not create a feed.

## Responses and limits

- `200 OK`: help, accepted POST, or delivery of one message.
- `204 No Content`: long-poll timeout, cancellation, feed expiration, reset, or shutdown when no message was delivered.
- `302 Found`: feed creation or reader reset redirect.
- `400 Bad Request`: malformed GUID or path.
- `401 Unauthorized`: invalid Authorization header or failed protected-feed authorization.
- `403 Forbidden`: an Authorization header was sent to an open feed.
- `413 Payload Too Large`: the body exceeds `MaxMessageSizeBytes`, including an unknown-length streaming body.
- `500 Internal Server Error`: the reader queue overflowed and must be reset.

An overflow makes that reader unusable and its queued messages unavailable. It does not affect other readers. The overflow-causing message is not accepted by that reader.

## License

MIT
