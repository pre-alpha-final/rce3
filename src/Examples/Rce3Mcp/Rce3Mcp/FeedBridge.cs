using System.Globalization;
using System.Net;

namespace Rce3Mcp;

internal sealed class BridgeException(string message) : Exception(message);

internal sealed class FeedBridge(HttpClient client, FeedConnection connection, ReaderIdentity identity,
    McpOptions options, MessageHistory history, BridgeState state, TimeProvider timeProvider, ILogger<FeedBridge> logger)
    : BackgroundService
{
    private CancellationToken _stoppingToken;
    private readonly Uri _readerUri = new($"{connection.FeedUri.AbsoluteUri}/{identity.Id:D}");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        var resetting = false;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var request = Request(HttpMethod.Get, resetting ? new Uri($"{_readerUri.AbsoluteUri}/reset") : _readerUri);
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stoppingToken);
                    if (resetting && response.StatusCode == HttpStatusCode.Found)
                    {
                        resetting = false;
                        state.ReaderReset();
                        continue;
                    }
                    if (!resetting && response.StatusCode == HttpStatusCode.Gone)
                    {
                        state.Set("resetting", "Reader overflowed; queued messages were lost. Resetting the retained reader.");
                        resetting = true;
                        continue;
                    }
                    if (!resetting && response.StatusCode == HttpStatusCode.OK)
                    {
                        var (body, length) = await ReadBodyAsync(response.Content, stoppingToken);
                        var contentType = response.Content.Headers.NonValidated.TryGetValues("Content-Type", out var values)
                            ? string.Join(", ", values) : "application/octet-stream";
                        history.Add(body, contentType, length);
                        state.PollSucceeded(receivedMessage: true);
                        continue;
                    }
                    if (!resetting && response.StatusCode == HttpStatusCode.NoContent)
                    {
                        state.PollSucceeded(receivedMessage: false);
                        continue;
                    }
                    var error = $"Feed {(resetting ? "reset" : "poll")} returned HTTP {(int)response.StatusCode}.";
                    if (!IsTransient(response.StatusCode))
                    {
                        state.Set("faulted", error + " Check configuration and restart the bridge.");
                        logger.LogWarning("Feed listener faulted with HTTP {StatusCode}", (int)response.StatusCode);
                        return;
                    }
                    state.Set("retrying", error + " Retrying.");
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException
                    || exception is OperationCanceledException && !stoppingToken.IsCancellationRequested)
                {
                    state.Set("retrying", "Feed connection was interrupted. Retrying.");
                }
                await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            if (stoppingToken.IsCancellationRequested) state.Set("stopping");
        }
    }

    public async Task<int> PublishAsync(byte[] body, string contentType, CancellationToken cancellationToken)
    {
        if (!state.CanSend)
            throw new BridgeException("Reader not ready. Call get_status and wait for connectionState polling before sending.");
        using var request = Request(HttpMethod.Post, connection.FeedUri);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stoppingToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new BridgeException($"Feed publish returned HTTP {(int)response.StatusCode}. The POST was not retried.");
            // The count is tiny. Bound even a malformed upstream response.
            var buffer = new byte[65];
            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var length = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, timeout.Token);
            var text = System.Text.Encoding.ASCII.GetString(buffer, 0, length);
            const string prefix = "Message distributed to ";
            const string suffix = " reader(s).";
            if (length == buffer.Length || text.Length <= prefix.Length + suffix.Length
                || !text.StartsWith(prefix, StringComparison.Ordinal) || !text.EndsWith(suffix, StringComparison.Ordinal)
                || !int.TryParse(text.AsSpan(prefix.Length, text.Length - prefix.Length - suffix.Length),
                    NumberStyles.None, CultureInfo.InvariantCulture, out var accepted) || accepted < 0)
                throw new BridgeException("Feed returned an invalid acceptance count. Delivery is uncertain; the POST was not retried.");
            return accepted;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
        {
            throw new BridgeException("Feed publish was interrupted. Delivery is uncertain; the POST was not retried. Check history before deciding to resend.");
        }
    }

    private HttpRequestMessage Request(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        if (connection.Authorization is not null)
            request.Headers.TryAddWithoutValidation("Authorization", connection.Authorization);
        return request;
    }

    private async Task<(byte[]? Body, long? Length)> ReadBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var declared = content.Headers.ContentLength;
        if (declared > options.MaxMessageSizeBytes) return (null, declared);
        using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var body = new MemoryStream();
        var buffer = new byte[Math.Min(options.MaxMessageSizeBytes, 8192)];
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, options.MaxMessageSizeBytes - body.Length + 1)), cancellationToken);
            if (count == 0) return (body.ToArray(), body.Length);
            if (body.Length + count > options.MaxMessageSizeBytes) return (null, declared);
            body.Write(buffer, 0, count);
        }
    }

    private static bool IsTransient(HttpStatusCode status) => status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;
}
