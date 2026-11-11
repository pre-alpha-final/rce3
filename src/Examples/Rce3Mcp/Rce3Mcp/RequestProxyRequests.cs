using System.Text;
using System.Text.Json;

namespace Rce3Mcp;

internal sealed class RequestProxyRequests(BridgeState state, McpOptions options, TimeProvider timeProvider) : IDisposable
{
    private const string CommandPrefix = "requestproxy: ";
    private const int MaxCommandBytes = 64 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly object _gate = new();
    private PendingResponse? _pending;

    public async Task<JsonElement> GetAsync(string url, Func<byte[], CancellationToken, Task<int>> publish,
        CancellationToken cancellationToken)
    {
        if (url is null || url.Contains('\r') || url.Contains('\n'))
            throw InvalidUrl();
        url = url.Trim();

        byte[] command;
        try
        {
            var limit = Math.Min(MaxCommandBytes, options.MaxMessageSizeBytes);
            if (StrictUtf8.GetByteCount(url) > limit - CommandPrefix.Length)
                throw new BridgeException($"Request Proxy command exceeds the {limit}-byte limit.");
            command = StrictUtf8.GetBytes(CommandPrefix + url);
        }
        catch (EncoderFallbackException)
        {
            throw new BridgeException("url must contain valid Unicode.");
        }

        if (url.Any(char.IsControl) || !Uri.TryCreate(url, UriKind.Absolute, out var target)
            || target.Scheme is not ("http" or "https") || target.Host.Length == 0
            || target.UserInfo.Length != 0 || target.Fragment.Length != 0)
            throw InvalidUrl();

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
                throw new BridgeException("Request Proxy could not submit the command. Delivery may be uncertain. The POST was not retried. Check the feed connection.");
            }

            var response = await pending.Response.Task.WaitAsync(operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new BridgeException("Request Proxy call was cancelled. A dispatched GET may continue.");
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            string summary;
            lock (_gate) summary = PendingResponse.Summarize(pending);
            throw new BridgeException("Request Proxy call exceeded its 60-second deadline. " + summary
                + " Check the running proxy and its imported HAR origins. A dispatched GET may continue.");
        }
        catch (OperationCanceledException)
        {
            throw new BridgeException("Request Proxy call was interrupted by a feed failure, reader reset, or shutdown. A dispatched GET may continue.");
        }
        finally
        {
            if (entered)
            {
                lock (_gate) _pending = null;
                _serial.Release();
            }
        }
    }

    public void Receive(byte[]? body)
    {
        if (body is null) return;
        lock (_gate) _pending?.Receive(body);
    }

    public void Dispose() => _serial.Dispose();

    private static BridgeException InvalidUrl() =>
        new("url must be an absolute HTTP(S) URL without credentials, fragments, or control characters.");

    private sealed class PendingResponse(string url)
    {
        private readonly byte[] _prefix = StrictUtf8.GetBytes($"requestproxy_response: {url}\n");
        private long _errors;
        private long _timeouts;
        private long _malformed;

        public TaskCompletionSource<JsonElement> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static string Summarize(PendingResponse? pending) =>
            $"Proxy errors: {pending?._errors ?? 0}, target timeouts: {pending?._timeouts ?? 0}, malformed matching replies: {pending?._malformed ?? 0}.";

        public void Receive(byte[] body)
        {
            if (Response.Task.IsCompleted || !body.AsSpan().StartsWith(_prefix)) return;
            try
            {
                var payload = body.AsMemory(_prefix.Length);
                StrictUtf8.GetCharCount(payload.Span);
                using var document = JsonDocument.Parse(payload);
                var json = document.RootElement;
                if (TryReadResponse(json, out var response))
                {
                    Response.TrySetResult(response);
                    return;
                }
                if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("error", out var error)
                    && error.ValueKind == JsonValueKind.Object && IsString(error, "name") && IsString(error, "message"))
                {
                    if (error.GetProperty("name").GetString() == "TimeoutError") _timeouts++;
                    _errors++;
                    return;
                }
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException) { }
            _malformed++;
        }

        private static bool TryReadResponse(JsonElement json, out JsonElement response)
        {
            response = default;
            if (json.ValueKind != JsonValueKind.Object
                || !json.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.Number
                || !status.TryGetInt32(out var code) || code is < 100 or > 599
                || !IsString(json, "statusText") || !IsString(json, "body")
                || !json.TryGetProperty("ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !json.TryGetProperty("headers", out var headers) || headers.ValueKind != JsonValueKind.Object
                || headers.EnumerateObject().Any(header => header.Value.ValueKind != JsonValueKind.Array
                    || header.Value.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String)))
                return false;

            response = JsonSerializer.SerializeToElement(new
            {
                status = code,
                statusText = json.GetProperty("statusText").GetString(),
                ok = ok.GetBoolean(),
                headers,
                body = json.GetProperty("body").GetString()
            });
            return true;
        }

        private static bool IsString(JsonElement json, string name) =>
            json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String;
    }
}
