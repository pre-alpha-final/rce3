# TabletUI

A .NET 10 Blazor WebAssembly PWA for controlling apps connected to an RCE3 feed from a phone or tablet. Publish it as static files; no TabletUI backend is required.

## Run locally

Install the .NET 10 SDK. From the repository root, start RCE3 in one terminal and TabletUI in another:

```powershell
dotnet run --project src/FeedServer/FeedServer/FeedServer.csproj --no-launch-profile
dotnet run --project src/Examples/TabletUI/TabletUI/TabletUI.csproj --launch-profile http
```

Open `http://localhost:5218`. Enter the feed URL and optional raw authorization key in Connection, then connect. The browser creates a fresh reader using reset and follows its redirect before enabling sends. With an empty feed this can take one poll timeout (30 seconds by default).

Start the [YouTube listener](../Youtube/README.MD) on its Windows device **before** sending commands. Keep the YouTube player focused on that device. Use the same `RCE3_FEED` and optional `RCE3_AUTH` values for the listener and TabletUI. The static browser app cannot read operating-system environment variables; enter these values through its connection form. Do not put populated values in source, published configuration, or a URL.

For local desktop development, localhost HTTP works. A mobile device's localhost refers to that mobile device, not the development PC.

## Use

The main screen stacks the YouTube and Debug panels, adapting to portrait and landscape. The five YouTube buttons publish exact UTF-8 commands with no trailing newline. A successful send reports how many reader queues accepted the message; it does not confirm that YouTube acted on it or reveal playback state.

Expand Debug to see incoming messages, sends, and errors. The custom editor sends the text exactly as entered, with an editable Content-Type. Messages are displayed as plain text, never interpreted as HTML. Invalid UTF-8 is shown as hex. The log keeps the most recent 200 entries, with previews capped at 4096 text characters or a bounded hex excerpt. Collapse the panel without stopping reception. Clear log only clears the local display.

The header cogs advance from a timer in the Blazor runtime. They show that the app is executing, independently of connection status. They stop when execution is suspended and do not keep the screen or app awake. They are an observable activity indicator, not a definitive OS sleep detector.

## Saved connection

Connecting saves one record under `TabletUI.connection.v1` in this origin's `localStorage`. The record has `Version: 1`, `RCE3_FEED`, and `RCE3_AUTH`. It is restored and automatically connected on reopening.

The authorization key is stored locally in plaintext as requested; it is accessible to scripts running on this origin. The key input is masked. Authorization headers are not included in the Debug log. Storage is specific to the browser/profile and site origin; it does not sync between devices and may be cleared by browser settings. Host only trusted content under the same origin.

Disconnect ends the active reader loop but retains settings. Forget connection disconnects and removes the saved record. If storage is blocked or invalid, the app shows a concise message and still supports a manual session. If forgetting fails, clear this site's storage through browser settings.

## Networking

TabletUI calls the existing RCE3 GET/POST/reset routes directly, with browser credentials omitted and HTTP caching disabled. RCE3 includes wildcard CORS support for GET/POST and Authorization/Content-Type headers. It answers browser preflights without touching feeds. CORS does not change open/protected feed authorization.

Each session has one reader. Polling continues while Debug is collapsed. A redirected reset response that contains a message is recorded rather than discarded. HTTP 204 starts another poll; HTTP 410 resets the overflowed reader. Transient failures retry with exponential delays from 2 to 30 seconds. Authorization failures stop the connection until settings are corrected and Connect is used again.

Coming online or returning to the page interrupts the current polling wait and resumes the same loop. Commands are never queued offline or retried automatically. If a send is interrupted, its delivery is unknown. RCE3 does not retain pre-subscription history, and an expired feed or reset reader can lose queued messages. Your own sends also appear as received messages.

## Publish and install

```powershell
dotnet publish src/Examples/TabletUI/TabletUI/TabletUI.csproj -c Release -o artifacts/tabletui
```

Serve the resulting `artifacts/tabletui/wwwroot` directory from an HTTPS static web server. Serve `.wasm` as `application/wasm`, `.webmanifest` as `application/manifest+json`, and the supplied static files with appropriate content types. The default base path is `/`. For deployment under a subdirectory, set the `<base href="/your-path/">` in `wwwroot/index.html` before publishing; keep its trailing slash. The manifest, worker, and assets use relative paths.

The static host and the feed must both be reachable from the mobile device over HTTPS with trusted certificates. If RCE3 listens on HTTP behind a TLS reverse proxy, use its external HTTPS feed URL. A development certificate trusted on the PC is not automatically trusted by the tablet. Do not expect an HTTPS PWA to call an ordinary HTTP LAN feed.

Use the browser's installation/Add to Home Screen feature after the initial online load. Installation controls vary by browser. The published service worker caches application files, including locally bundled Sketchy CSS and fonts. Development runs intentionally do not cache the application. After a successful published online load, reopening offline loads the interface; sending requires a working connection.

The service worker never caches or replays feed traffic, including same-origin requests. App updates activate after old app windows close; close all TabletUI windows and reopen online to use a newly published version. Keep serving the full published asset set together.

## Add or remove app support

Add a Razor component under `Components` and register its ID, display title, and component type in `PanelCatalog`. Remove its registration to remove support. Rebuild and publish after changes. Panels inherit `CommandPanelBase`, which receives the shared `FeedConnection` service from the catalog renderer and use `PublishAsync(body, contentType)` and `CanSend`; they must not create another reader or add a protocol envelope. `Changed` and `Entries` supply connection/message updates.

`YoutubeCommands` contains the five existing client command bodies. More complex app controls belong in their own components. There is no runtime panel editor or configurable message schema.

## Verify

```powershell
dotnet test src/Examples/TabletUI/TabletUI.slnx
node --test src/Examples/TabletUI/TabletUI.Tests/ServiceWorkerTests.mjs
dotnet test src/FeedServer/FeedServer.slnx
dotnet test src/Examples/Youtube/Youtube.slnx
dotnet build src/Examples/TabletUI/TabletUI.slnx -c Release -warnaserror
dotnet build src/FeedServer/FeedServer.slnx -c Release -warnaserror
dotnet build src/Examples/Youtube/Youtube.slnx -c Release -warnaserror
git diff --check
```

For browser acceptance, use a published build on a different origin from RCE3, test an open and a protected feed, send all YouTube commands and custom multiline text, verify echoed content and CORS errors, and reopen offline after installation. Inspect the service-worker cache to confirm only application assets are present. Check both portrait and landscape layouts, and pause runtime execution to verify the cogs freeze. Actual iOS/Android installation, suspension, and resumption require checking on those devices.
