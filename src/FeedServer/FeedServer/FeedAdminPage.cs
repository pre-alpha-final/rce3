namespace FeedServer;

internal static class FeedAdminPage
{
    public static string Create(Guid feedId, Guid readerId)
    {
        return Html
            .Replace("__FEED_ID__", feedId.ToString("D"), StringComparison.Ordinal)
            .Replace("__READER_ID__", readerId.ToString("D"), StringComparison.Ordinal);
    }

    private const string Html = """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <link rel="icon" href="data:,">
          <title>RCE3 feed admin</title>
          <style>
            :root { color-scheme: light dark; font-family: system-ui, sans-serif; }
            body { margin: 0 auto; max-width: 52rem; padding: 1.5rem; }
            h1 { margin-bottom: .25rem; }
            code { overflow-wrap: anywhere; }
            label { display: block; font-weight: 600; margin-top: 1rem; }
            input, textarea, button { box-sizing: border-box; font: inherit; }
            input, textarea { margin-top: .35rem; padding: .55rem; width: 100%; }
            textarea { min-height: 7rem; resize: vertical; }
            button { cursor: pointer; margin: .75rem .5rem 0 0; padding: .5rem .9rem; }
            button:disabled { cursor: default; }
            #status { margin-top: 1rem; min-height: 1.5rem; }
            #messages { border: 1px solid currentColor; min-height: 8rem; padding: .75rem; }
            .message { border-bottom: 1px solid color-mix(in srgb, currentColor 25%, transparent); padding: .5rem 0; }
            .message:last-child { border-bottom: 0; }
            .message small { opacity: .7; }
            .message pre { margin: .25rem 0 0; overflow-wrap: anywhere; white-space: pre-wrap; }
          </style>
        </head>
        <body data-feed-id="__FEED_ID__" data-reader-id="__READER_ID__">
          <h1>RCE3 feed admin</h1>
          <p>This is a debug client, not a privileged administration interface.</p>
          <p>Feed: <code id="feed-id">__FEED_ID__</code></p>

          <label for="authorization">Authorization value</label>
          <input id="authorization" type="password" autocomplete="off" spellcheck="false"
                 placeholder="Leave empty for an open feed">

          <div>
            <button id="connect" type="button">Connect</button>
            <button id="disconnect" type="button" disabled>Disconnect</button>
          </div>
          <p id="status" role="status" aria-live="polite">Disconnected.</p>

          <form id="send-form">
            <label for="message">UTF-8 text message</label>
            <textarea id="message" disabled></textarea>
            <button id="send" type="submit" disabled>Send</button>
          </form>

          <h2>Received messages</h2>
          <div id="messages" aria-live="polite"></div>

          <script>
            (() => {
              "use strict";

              const feedId = document.body.dataset.feedId;
              const readerId = document.body.dataset.readerId;
              const feedUrl = `/${feedId}`;
              const readerUrl = `${feedUrl}/${readerId}`;
              const authorizationInput = document.querySelector("#authorization");
              const connectButton = document.querySelector("#connect");
              const disconnectButton = document.querySelector("#disconnect");
              const status = document.querySelector("#status");
              const sendForm = document.querySelector("#send-form");
              const messageInput = document.querySelector("#message");
              const sendButton = document.querySelector("#send");
              const messages = document.querySelector("#messages");

              let generation = 0;
              let connection = null;

              function requestHeaders(authorization) {
                return authorization === "" ? {} : { Authorization: authorization };
              }

              function setConnected(connected) {
                authorizationInput.disabled = connected;
                connectButton.disabled = connected;
                disconnectButton.disabled = !connected;
                messageInput.disabled = !connected;
                sendButton.disabled = !connected;
              }

              function appendMessage(body) {
                const item = document.createElement("div");
                item.className = "message";
                const metadata = document.createElement("small");
                metadata.textContent = `Received ${new Date().toLocaleTimeString()}`;
                const content = document.createElement("pre");
                content.textContent = body;
                item.append(metadata, content);
                messages.append(item);
                item.scrollIntoView({ block: "nearest" });
              }

              async function responseError(action, response) {
                const detail = (await response.text()).trim();
                return `${action} failed: HTTP ${response.status}${detail === "" ? "" : ` — ${detail}`}`;
              }

              function stop(expectedGeneration, message) {
                if (expectedGeneration !== generation) {
                  return;
                }

                generation++;
                connection?.controller.abort();
                connection = null;
                setConnected(false);
                status.textContent = message;
              }

              async function poll(expectedGeneration, authorization, controller) {
                try {
                  while (expectedGeneration === generation) {
                    const response = await fetch(readerUrl, {
                      headers: requestHeaders(authorization),
                      signal: controller.signal
                    });

                    if (response.status === 204) {
                      continue;
                    }
                    if (!response.ok) {
                      throw new Error(await responseError("Receive", response));
                    }

                    appendMessage(await response.text());
                  }
                } catch (error) {
                  if (error.name !== "AbortError") {
                    stop(expectedGeneration, error.message);
                  }
                }
              }

              connectButton.addEventListener("click", async () => {
                const expectedGeneration = ++generation;
                const authorization = authorizationInput.value;
                const controller = new AbortController();
                connection?.controller.abort();
                connection = { authorization, controller };
                connectButton.disabled = true;
                authorizationInput.disabled = true;
                status.textContent = "Connecting…";

                try {
                  const response = await fetch(`${readerUrl}/reset`, {
                    headers: requestHeaders(authorization),
                    redirect: "manual",
                    signal: controller.signal
                  });
                  const redirected = response.status === 302 || response.type === "opaqueredirect";
                  if (!redirected) {
                    throw new Error(await responseError("Connect", response));
                  }
                  if (expectedGeneration !== generation) {
                    return;
                  }

                  setConnected(true);
                  status.textContent = `Connected as reader ${readerId}.`;
                  void poll(expectedGeneration, authorization, controller);
                } catch (error) {
                  if (error.name !== "AbortError") {
                    stop(expectedGeneration, error.message);
                  }
                }
              });

              disconnectButton.addEventListener("click", () => {
                stop(generation, "Disconnected.");
              });

              sendForm.addEventListener("submit", async event => {
                event.preventDefault();
                const current = connection;
                if (current === null) {
                  return;
                }

                sendButton.disabled = true;
                try {
                  const response = await fetch(feedUrl, {
                    method: "POST",
                    headers: {
                      ...requestHeaders(current.authorization),
                      "Content-Type": "text/plain;charset=UTF-8"
                    },
                    body: messageInput.value,
                    signal: current.controller.signal
                  });
                  if (!response.ok) {
                    throw new Error(await responseError("Send", response));
                  }

                  status.textContent = await response.text();
                  messageInput.value = "";
                  messageInput.focus();
                } catch (error) {
                  if (error.name !== "AbortError") {
                    status.textContent = error.message;
                  }
                } finally {
                  sendButton.disabled = connection !== current;
                }
              });

              window.addEventListener("pagehide", () => connection?.controller.abort());
            })();
          </script>
        </body>
        </html>
        """;
}
