using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace TabletUI.Services;

public sealed class FeedConnection(HttpClient client, TimeProvider timeProvider, TimeSpan? retryDelay = null)
    : IAsyncDisposable
{
    public const int LogLimit = 200;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly List<CommunicationEntry> _entries = [];
    private Session? _session;
    private bool _sending;
    private bool _online = true;
    private string _status = "Disconnected";

    public event Action? Changed;
    public string Status => _status;
    public bool IsConnected => _session is { Ready: true } && _online;
    public bool CanSend => IsConnected && !_sending;
    public IReadOnlyList<CommunicationEntry> Entries
    {
        get { lock (_entries) { return _entries.ToArray(); } }
    }

    public async Task ConnectAsync(ConnectionSettings settings)
    {
        if (!ConnectionSettings.TryCreate(settings.RCE3_FEED, settings.RCE3_AUTH, out var validated, out var error))
        {
            SetStatus(error);
            return;
        }

        await _lifecycle.WaitAsync();
        try
        {
            await StopSessionAsync();
            ClearLog();
            var session = new Session(validated!);
            _session = session;
            SetStatus(_online ? "Connecting — creating reader" : "Offline");
            session.Loop = PollAsync(session);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            await StopSessionAsync();
            SetStatus("Disconnected");
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task StopSessionAsync()
    {
        var previous = _session;
        _session = null;
        if (previous is null) { return; }
        previous.Stop.Cancel();
        await previous.Loop;
        previous.Stop.Dispose();
    }

    public Task ResumeAsync()
    {
        _online = true;
        var session = _session;
        if (session is not null && !session.Halted)
        {
            session.Ready = false;
            SetStatus("Reconnecting");
            session.Interrupt?.Cancel();
        }
        return Task.CompletedTask;
    }

    public Task SetOfflineAsync()
    {
        _online = false;
        var session = _session;
        if (session is not null && !session.Halted)
        {
            session.Ready = false;
            SetStatus("Offline");
            session.Interrupt?.Cancel();
        }
        return Task.CompletedTask;
    }

    public async Task<int?> PublishAsync(string body, string contentType)
    {
        var session = _session;
        if (!CanSend || session is null) { return null; }
        if (!MediaTypeHeaderValue.TryParse(contentType, out _))
        {
            Add(session, "Error", "Enter a valid Content-Type.");
            return null;
        }

        _sending = true;
        Notify();
        var bytes = Encoding.UTF8.GetBytes(body);
        try
        {
            using var request = CreateRequest(session, HttpMethod.Post, session.Settings.RCE3_FEED);
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            using var response = await client.SendAsync(request, session.Stop.Token);
            if (_session != session) { return null; }
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var countText = await response.Content.ReadAsStringAsync(session.Stop.Token);
                const string prefix = "Message distributed to ";
                const string suffix = " reader(s).";
                if (countText.StartsWith(prefix, StringComparison.Ordinal)
                    && countText.EndsWith(suffix, StringComparison.Ordinal)
                    && int.TryParse(countText.AsSpan(prefix.Length, countText.Length - prefix.Length - suffix.Length), out var count)
                    && count >= 0)
                {
                    Add(session, "Sent", $"Accepted by {count} reader queue(s).", contentType, bytes);
                    return count;
                }
                Add(session, "Error", "POST succeeded but returned an unexpected acceptance count. Not resent.", contentType, bytes);
            }
            else
            {
                Add(session, "Error", $"POST returned HTTP {(int)response.StatusCode}. Not resent.", contentType, bytes);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    session.Halted = true;
                    session.Ready = false;
                    SetStatus($"Authorization failed (HTTP {(int)response.StatusCode}) — check connection settings");
                    session.Stop.Cancel();
                }
            }
        }
        catch (OperationCanceledException)
        {
            Add(session, "Error", "Send interrupted; delivery is unknown. Not resent.", contentType, bytes);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            Add(session, "Error", "Send failed; delivery is unknown. Not resent.", contentType, bytes);
        }
        finally
        {
            _sending = false;
            Notify();
        }
        return null;
    }

    private async Task PollAsync(Session session)
    {
        var reset = true;
        var failures = 0;
        while (!session.Stop.IsCancellationRequested)
        {
            using var interruption = CancellationTokenSource.CreateLinkedTokenSource(session.Stop.Token);
            session.Interrupt = interruption;
            try
            {
                if (!_online)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, timeProvider, interruption.Token);
                    continue;
                }

                using var request = CreateRequest(session, HttpMethod.Get, session.ReaderUrl + (reset ? "/reset" : ""));
                // Reset redirects into a long poll in the browser. Waiting for its response
                // would disable controls on an idle feed, including after reconnecting.
                // Treat an active request as connected unless it reports a failure.
                session.Ready = true;
                SetStatus("Connected");
                using var response = await client.SendAsync(request, interruption.Token);
                if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent
                    || (reset && response.StatusCode == HttpStatusCode.Found))
                {
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        var bytes = await response.Content.ReadAsByteArrayAsync(interruption.Token);
                        var contentType = response.Content.Headers.TryGetValues("Content-Type", out var values)
                            ? string.Join(", ", values) : "application/octet-stream";
                        Add(session, "Received", $"{bytes.Length} bytes", contentType, bytes);
                    }
                    reset = false;
                    failures = 0;
                    session.Ready = true;
                    SetStatus("Connected");
                    continue;
                }

                session.Ready = false;
                if (response.StatusCode == HttpStatusCode.Gone)
                {
                    reset = true;
                    Add(session, "Connection", "Reader overflowed; resetting its queue.");
                    SetStatus("Resetting reader");
                }
                else if (response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
                    || (int)response.StatusCode >= 500)
                {
                    Add(session, "Error", $"Poll returned HTTP {(int)response.StatusCode}; retrying.");
                    await RetryAsync(++failures, interruption.Token);
                }
                else
                {
                    session.Halted = true;
                    var message = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                        ? $"Authorization failed (HTTP {(int)response.StatusCode}) — check connection settings"
                        : $"Connection stopped (HTTP {(int)response.StatusCode}) — check feed URL";
                    Add(session, "Error", message);
                    SetStatus(message);
                    return;
                }
            }
            catch (OperationCanceledException) when (session.Stop.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException) when (interruption.IsCancellationRequested)
            {
                // The lifecycle event interrupts the existing loop; it never creates a second loop.
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            {
                session.Ready = false;
                Add(session, "Error", "Feed request failed. Check network, HTTPS, and CORS; retrying.");
                try
                {
                    await RetryAsync(++failures, interruption.Token);
                }
                catch (OperationCanceledException) { }
            }
            finally
            {
                session.Interrupt = null;
            }
        }
    }

    private Task RetryAsync(int failures, CancellationToken token)
    {
        SetStatus(_online ? "Reconnecting" : "Offline");
        var initial = (retryDelay ?? TimeSpan.FromSeconds(2)).TotalMilliseconds;
        var delay = TimeSpan.FromMilliseconds(Math.Min(30000, initial * Math.Pow(2, Math.Min(failures - 1, 5))));
        return Task.Delay(delay, timeProvider, token);
    }

    private static HttpRequestMessage CreateRequest(Session session, HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Omit);
        request.SetBrowserRequestCache(BrowserRequestCache.NoStore);
        if (session.Settings.RCE3_AUTH.Length > 0)
        {
            request.Headers.TryAddWithoutValidation("Authorization", session.Settings.RCE3_AUTH);
        }
        return request;
    }

    private void Add(Session session, string direction, string detail, string? contentType = null, byte[]? body = null)
    {
        if (_session != session) { return; }
        var key = session.Settings.RCE3_AUTH;
        string Redact(string text) => key.Length == 0 ? text : text.Replace(key, "[redacted]", StringComparison.Ordinal);
        var preview = body is null ? string.Empty : Redact(CommunicationEntry.FormatPreview(body));
        if (preview.Length > CommunicationEntry.PreviewLimit)
        {
            preview = preview[..CommunicationEntry.PreviewLimit] + " … [truncated]";
        }
        var entry = new CommunicationEntry(timeProvider.GetUtcNow(), direction, Redact(detail),
            contentType is null ? null : Redact(contentType), preview);
        lock (_entries)
        {
            _entries.Add(entry);
            if (_entries.Count > LogLimit) { _entries.RemoveAt(0); }
        }
        Notify();
    }

    public void ClearLog()
    {
        lock (_entries) { _entries.Clear(); }
        Notify();
    }

    private void SetStatus(string status)
    {
        if (_status == status) { return; }
        _status = status;
        Notify();
    }

    private void Notify() => Changed?.Invoke();

    public async ValueTask DisposeAsync() => await DisconnectAsync();

    private sealed class Session(ConnectionSettings settings)
    {
        public ConnectionSettings Settings { get; } = settings;
        public string ReaderUrl { get; } = $"{settings.RCE3_FEED}/{Guid.NewGuid():D}";
        public CancellationTokenSource Stop { get; } = new();
        public CancellationTokenSource? Interrupt { get; set; }
        public Task Loop { get; set; } = Task.CompletedTask;
        public bool Ready { get; set; }
        public bool Halted { get; set; }
    }
}
