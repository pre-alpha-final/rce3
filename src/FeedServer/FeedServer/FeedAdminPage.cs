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
          <link rel="stylesheet" href="https://bootswatch.com/5/sketchy/bootstrap.min.css">
          <style>
            main { max-width: 58rem; }
            code { overflow-wrap: anywhere; }
            #status { min-height: 1.5rem; }
            #messages { min-height: 8rem; }
            #messages:empty::before { content: "No messages yet."; color: var(--bs-secondary-color); }
            .message pre {
              margin: .25rem 0 0;
              overflow-wrap: anywhere;
              white-space: pre-wrap;
            }
          </style>
        </head>
        <body data-feed-id="__FEED_ID__" data-reader-id="__READER_ID__">
          <main class="container py-5">
            <header class="mb-5 text-center">
              <h1 class="display-2">RCE3 feed admin</h1>
              <p class="lead">Send and receive plain text. No dashboards, secret controls, or grown-up supervision.</p>
              <p><span class="badge border border-dark bg-light p-3 text-dark fs-6">Feed: <code id="feed-id">__FEED_ID__</code></span></p>
            </header>

            <section class="card border-dark mb-4" aria-labelledby="connection-heading">
              <div class="card-body">
                <h2 id="connection-heading" class="card-title">Connect a reader</h2>
                <p class="card-text">This page is public. For a protected feed, enter its raw Authorization value before connecting.</p>
                <label class="form-label" for="authorization">Authorization value</label>
                <input id="authorization" class="form-control" type="password" autocomplete="off" spellcheck="false"
                       placeholder="Leave empty for an open feed">

                <div class="d-flex flex-wrap gap-2 mt-3">
                  <button id="connect" class="btn btn-primary" type="button">Connect</button>
                  <button id="disconnect" class="btn btn-outline-secondary" type="button" disabled>Disconnect</button>
                </div>
                <p id="status" class="alert alert-warning mt-3 mb-0" role="status" aria-live="polite">Disconnected.</p>
              </div>
            </section>

            <section class="card border-dark mb-4" aria-labelledby="composer-heading">
              <div class="card-body">
                <form id="send-form">
                  <h2 id="composer-heading" class="card-title">Publish a message</h2>
                  <label class="form-label" for="message">UTF-8 text message</label>
                  <textarea id="message" class="form-control" rows="5" disabled></textarea>
                  <div class="mt-3">
                    <button id="send" class="btn btn-primary" type="submit" disabled>Send</button>
                  </div>
                </form>
              </div>
            </section>

            <section class="card border-dark mb-4" aria-labelledby="messages-heading">
              <div class="card-body">
                <h2 id="messages-heading" class="card-title">Received messages</h2>
                <div id="messages" aria-live="polite"></div>
              </div>
            </section>
          </main>

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
                item.className = "message list-group-item";
                const metadata = document.createElement("small");
                metadata.className = "text-body-secondary";
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
