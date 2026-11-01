(() => {
    "use strict";

    if (typeof RCE3_FEED !== "string" || RCE3_FEED.trim() === "") {
        throw new Error("FetchProxy: set RCE3_FEED to your feed URL before pasting this snippet.");
    }

    let feed;
    try {
        feed = new URL(RCE3_FEED.trim());
        if (!/^https?:$/.test(feed.protocol) || feed.username || feed.password || feed.search || feed.hash
            || feed.pathname.replace(/\/+$/, "") === "") {
            throw new Error();
        }
    } catch {
        throw new Error("FetchProxy: RCE3_FEED must be an absolute HTTP(S) feed URL without credentials, query, or fragment.");
    }

    const authorization = typeof RCE3_AUTH === "undefined" ? "" : RCE3_AUTH;
    if (typeof authorization !== "string"
        || (authorization !== "" && (authorization.trim() === "" || /[\r\n]/.test(authorization)))) {
        throw new Error("FetchProxy: RCE3_AUTH must be a nonblank, single-line string, or empty for an open feed.");
    }
    const headers = authorization === "" ? {} : { Authorization: authorization };
    try {
        new Headers(headers);
    } catch {
        throw new Error("FetchProxy: RCE3_AUTH is not a valid HTTP header value.");
    }

    // getRandomValues also works on ordinary HTTP pages, unlike randomUUID.
    const bytes = crypto.getRandomValues(new Uint8Array(16));
    bytes[6] = (bytes[6] & 15) | 64;
    bytes[8] = (bytes[8] & 63) | 128;
    const hex = Array.from(bytes, byte => byte.toString(16).padStart(2, "0")).join("");
    const readerId = `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
    const feedUrl = feed.href.replace(/\/+$/, "");
    const readerUrl = `${feedUrl}/${readerId}`;
    const controller = new AbortController();
    const signal = controller.signal;

    window.RCE3_FETCH_PROXY?.stop();
    window.RCE3_FETCH_PROXY = Object.freeze({ stop });
    window.addEventListener("pagehide", stop);

    function stop() {
        if (signal.aborted) return;
        controller.abort();
        window.removeEventListener("pagehide", stop);
        console.info("[FetchProxy] Stopped.");
    }

    function retryDelay() {
        signal.throwIfAborted();
        console.warn("[FetchProxy] Feed read failed; retrying in one second.");
        return new Promise((resolve, reject) => {
            const timer = setTimeout(() => {
                signal.removeEventListener("abort", cancel);
                resolve();
            }, 1000);
            function cancel() {
                clearTimeout(timer);
                reject(signal.reason);
            }
            signal.addEventListener("abort", cancel, { once: true });
        });
    }

    async function receive() {
        let resetting = false;
        while (true) {
            signal.throwIfAborted();
            let response;
            let message;
            try {
                response = await fetch(readerUrl + (resetting ? "/reset" : ""), {
                    headers, credentials: "omit", cache: "no-store", redirect: "manual", signal
                });
                if (!resetting && response.status === 200) {
                    message = await response.text();
                } else {
                    await response.body?.cancel();
                }
            } catch {
                await retryDelay();
                continue;
            }
            signal.throwIfAborted();

            if (resetting && (response.status === 302 || response.type === "opaqueredirect")) {
                resetting = false;
            } else if (!resetting && response.status === 200) {
                return message;
            } else if (!resetting && response.status === 204) {
                continue;
            } else if (!resetting && response.status === 410) {
                console.warn("[FetchProxy] Reader overflowed; resetting its queue.");
                resetting = true;
            } else if (response.status === 408 || response.status === 429 || response.status >= 500) {
                await retryDelay();
            } else {
                throw new Error(`Feed ${resetting ? "reset" : "poll"} failed: HTTP ${response.status}.`);
            }
        }
    }

    async function fetchTarget(url) {
        signal.throwIfAborted();
        const target = new AbortController();
        const cancel = () => target.abort(signal.reason);
        signal.addEventListener("abort", cancel, { once: true });
        let timedOut = false;
        const timer = setTimeout(() => {
            timedOut = true;
            target.abort();
        }, 10_000);
        try {
            if (url === "") throw new TypeError("A target URL is required.");
            const response = await fetch(url, { signal: target.signal });
            const body = await response.text();
            target.signal.throwIfAborted();
            return {
                url: response.url,
                status: response.status,
                statusText: response.statusText,
                ok: response.ok,
                redirected: response.redirected,
                type: response.type,
                headers: Object.fromEntries(response.headers),
                body
            };
        } catch (error) {
            signal.throwIfAborted();
            return { error: {
                name: timedOut ? "TimeoutError" : error.name,
                message: timedOut ? "Target fetch exceeded 10 seconds." : error.message
            } };
        } finally {
            clearTimeout(timer);
            signal.removeEventListener("abort", cancel);
        }
    }

    async function run() {
        while (!signal.aborted) {
            const message = await receive();
            if (!message.startsWith("fetchproxy: ")) continue;
            const url = message.slice("fetchproxy: ".length).trim();
            const result = await fetchTarget(url);
            signal.throwIfAborted();
            let response;
            try {
                response = await fetch(feedUrl, {
                    method: "POST",
                    headers: { ...headers, "Content-Type": "text/plain;charset=UTF-8" },
                    credentials: "omit", redirect: "manual", signal,
                    body: `fetchproxyresponse: ${url}\n${JSON.stringify(result, null, 2)}`
                });
                await response.body?.cancel();
            } catch {
                throw new Error("Response publish failed; delivery is unknown. It was not retried.");
            }
            if (response.status !== 200) {
                throw new Error(`Response publish failed: HTTP ${response.status}. It was not retried.`);
            }
        }
    }

    console.info(`[FetchProxy] Polling reader ${readerId}. Stop with RCE3_FETCH_PROXY.stop().`);
    void run().catch(error => {
        if (!signal.aborted) {
            console.error(`[FetchProxy] ${error.message}`);
            stop();
        }
    });
})();
