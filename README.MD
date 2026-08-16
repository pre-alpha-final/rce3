# Rce3 - Remote Controlled Electronics v3

After two somewhat successful attempts, it's time to drop all pretense of professionalism and goblin-engineer something actually worth using. A tiny, wysiwyg feed server; POST a raw request body to a feed and every long-pooling "connected" reader gets it. No types, no formats, no protocols, no redtape.

## API

```text
GET  /                         Create a random feed and redirect to it.
GET  /{feedGuid}                Create or touch a feed and return help.
POST /{feedGuid}                Publish the raw request body to all readers.
GET  /{feedGuid}/{readerGuid}   Long-poll one queued message for one reader.
GET  /{feedGuid}/{readerGuid}/reset
                                Clear one reader queue, make it usable again, and redirect to it.
```

Feed and reader IDs must be valid GUIDs. A missing feed is created by the first valid request.

## Authorization

The first valid request to a feed sets its access mode:

- No `Authorization` header creates an open feed.
- `Authorization: <key>` creates a protected feed.

Open feeds reject requests with `Authorization`. Protected feeds require the same key on every request.

## Curl examples

### Open feed

```bash
FEED_ID=4cb82df9-f198-40d8-8f11-fc59365e28a2
READER_ID=0576e9fc-75d5-4620-9d72-a7ff33d859de

curl http://localhost:5137/$FEED_ID
curl -X POST http://localhost:5137/$FEED_ID --data-binary 'hello feed' -H 'Content-Type: text/plain'
curl http://localhost:5137/$FEED_ID/$READER_ID
curl http://localhost:5137/$FEED_ID/$READER_ID/reset
```

### Protected feed

```bash
FEED_ID=56d248ae-8723-4e33-b56c-02e290f8bf84
READER_ID=090259f4-9d9e-4ee5-a94e-ddcb6fc36378
KEY='test-feed-key'

curl http://localhost:5137/$FEED_ID -H "Authorization: $KEY"
curl -X POST http://localhost:5137/$FEED_ID -H "Authorization: $KEY" --data-binary 'secret hello'
curl http://localhost:5137/$FEED_ID/$READER_ID -H "Authorization: $KEY"
curl http://localhost:5137/$FEED_ID/$READER_ID/reset -H "Authorization: $KEY"
```

## License

MIT
