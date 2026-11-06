using System.Text;
using System.Text.Json;

namespace Rce3Mcp;

internal sealed class FetchProxyRequests(BridgeState state, McpOptions options, TimeProvider timeProvider) : IDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly object _gate = new();
    private PendingResponse? _pending;

    public async Task<JsonElement> FetchAsync(string url, Func<byte[], CancellationToken, Task> publish, CancellationToken cancellationToken)
    {
        if (url is null || url.Contains('\r') || url.Contains('\n'))
            throw new BridgeException("url must be an absolute HTTP(S) URL without line breaks.");
        url = url.Trim();
        byte[] command;
        try
        {
            var text = "fetchproxy: " + url;
            if (StrictUtf8.GetByteCount(text) > options.MaxMessageSizeBytes)
                throw new BridgeException($"FetchProxy request exceeds the configured limit of {options.MaxMessageSizeBytes} bytes.");
            command = StrictUtf8.GetBytes(text);
        }
        catch (EncoderFallbackException)
        {
            throw new BridgeException("url must contain valid Unicode.");
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target)
            || target.Scheme is not ("http" or "https") || target.Host.Length == 0)
            throw new BridgeException("url must be an absolute HTTP(S) URL; relative URLs are not supported.");

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60), timeProvider);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var entered = false;
        PendingResponse? pending = null;
        try
        {
            await _serial.WaitAsync(lifetime.Token);
            entered = true;
            var readerLifetime = await state.WaitUntilReadyAsync(lifetime.Token);
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, readerLifetime);
            operation.Token.ThrowIfCancellationRequested();
            pending = new(url);
            lock (_gate) _pending = pending;
            try
            {
                await publish(command, operation.Token);
            }
            catch (BridgeException)
            {
                operation.Token.ThrowIfCancellationRequested();
                throw new BridgeException("FetchProxy could not submit the request. Delivery may be uncertain; it was not retried. Check the configured connection.");
            }
            var response = await pending.Response.Task.WaitAsync(operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            return response;
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
                throw new BridgeException("FetchProxy request was cancelled. It was not retried; browser work already dispatched may continue.");
            if (deadline.IsCancellationRequested)
            {
                string summary;
                lock (_gate) summary = pending?.Summary() ?? "";
                throw new BridgeException("FetchProxy request exceeded its 60-second deadline. " + summary
                    + "The request was not retried; browser work already dispatched may continue. Check the configured browser and target.");
            }
            throw new BridgeException("FetchProxy request was interrupted because its connection stopped or reset. It was not retried; browser work already dispatched may continue.");
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pending, pending)) _pending = null;
            }
            if (entered) _serial.Release();
        }
    }

    // Called directly by the background reader, independently of history retention.
    public void Receive(byte[]? body)
    {
        if (body is null) return;
        lock (_gate) _pending?.Receive(body);
    }

    public void Dispose() => _serial.Dispose();

    private sealed class PendingResponse(string url)
    {
        private readonly byte[] _prefix = StrictUtf8.GetBytes($"fetchproxyresponse: {url}\n");
        private long _fetchFailures;
        private long _timeouts;
        private long _malformed;
        public TaskCompletionSource<JsonElement> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Summary() => $"Observed {_fetchFailures} browser fetch failure(s), including {_timeouts} timeout(s), and {_malformed} malformed matching reply/replies. ";

        public void Receive(byte[] body)
        {
            if (Response.Task.IsCompleted || !body.AsSpan().StartsWith(_prefix)) return;
            try
            {
                using var document = JsonDocument.Parse(body.AsMemory(_prefix.Length));
                var json = document.RootElement;
                if (TryResponse(json, out var response))
                {
                    Response.TrySetResult(response);
                    return;
                }
                if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("error", out var error)
                    && error.ValueKind == JsonValueKind.Object
                    && IsString(error, "name") && IsString(error, "message"))
                {
                    _fetchFailures++;
                    if (error.GetProperty("name").GetString() == "TimeoutError") _timeouts++;
                    return;
                }
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException) { }
            _malformed++;
        }

        private static bool TryResponse(JsonElement json, out JsonElement response)
        {
            response = default;
            if (json.ValueKind != JsonValueKind.Object || !IsString(json, "url")
                || !json.TryGetProperty("status", out var status) || !status.TryGetInt32(out var code) || code is < 100 or > 599
                || !IsString(json, "statusText") || !IsBoolean(json, "ok") || !IsBoolean(json, "redirected")
                || !IsString(json, "type") || !IsString(json, "body")
                || !json.TryGetProperty("headers", out var headers) || headers.ValueKind != JsonValueKind.Object
                || headers.EnumerateObject().Any(header => header.Value.ValueKind != JsonValueKind.String)) return false;
            response = JsonSerializer.SerializeToElement(new
            {
                url = json.GetProperty("url").GetString(), status = code,
                statusText = json.GetProperty("statusText").GetString(), ok = json.GetProperty("ok").GetBoolean(),
                redirected = json.GetProperty("redirected").GetBoolean(), type = json.GetProperty("type").GetString(),
                headers, body = json.GetProperty("body").GetString()
            });
            return true;
        }

        private static bool IsString(JsonElement json, string name) =>
            json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String;
        private static bool IsBoolean(JsonElement json, string name) =>
            json.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False;
    }
}
