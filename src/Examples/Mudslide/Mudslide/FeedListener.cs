using System.Net;

namespace Mudslide;

internal sealed class FeedProtocolException(HttpStatusCode statusCode, string operation)
    : Exception($"Feed {operation} failed with HTTP {(int)statusCode} ({statusCode}).")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

internal sealed class FeedListener
{
    private readonly HttpClient _client;
    private readonly MudslideOptions _options;
    private readonly INotificationSender _notificationSender;
    private readonly TimeSpan _retryDelay;
    private readonly TextWriter _error;
    private readonly Uri _readerUri;
    private readonly Uri _resetUri;

    public FeedListener(
        HttpClient client,
        MudslideOptions options,
        Guid readerId,
        INotificationSender notificationSender,
        TimeSpan retryDelay,
        TextWriter error)
    {
        _client = client;
        _options = options;
        _notificationSender = notificationSender;
        _retryDelay = retryDelay;
        _error = error;
        _readerUri = new Uri($"{options.FeedUri.AbsoluteUri}/{readerId:D}", UriKind.Absolute);
        _resetUri = new Uri($"{_readerUri.AbsoluteUri}/reset", UriKind.Absolute);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await ResetUntilReadyAsync(cancellationToken);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var response = await SendAsync(_readerUri, cancellationToken);

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                    if (NotificationMessage.TryParse(body, out var notification))
                    {
                        var result = await _notificationSender.SendAsync(notification, cancellationToken);
                        LogCommandFailure(result);
                    }

                    continue;
                }

                if (response.StatusCode == HttpStatusCode.NoContent)
                {
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.InternalServerError)
                {
                    _error.WriteLine("Reader became unusable; resetting it.");
                    await ResetUntilReadyAsync(cancellationToken);
                    continue;
                }

                if (IsTransient(response.StatusCode))
                {
                    _error.WriteLine($"Feed returned HTTP {(int)response.StatusCode}; retrying.");
                    await DelayBeforeRetryAsync(cancellationToken);
                    continue;
                }

                throw new FeedProtocolException(response.StatusCode, "poll");
            }
            catch (HttpRequestException)
            {
                _error.WriteLine("Feed request failed; retrying.");
                await DelayBeforeRetryAsync(cancellationToken);
            }
            catch (IOException)
            {
                _error.WriteLine("Feed response failed; retrying.");
                await DelayBeforeRetryAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _error.WriteLine("Feed request was interrupted; retrying.");
                await DelayBeforeRetryAsync(cancellationToken);
            }
        }
    }

    private async Task ResetUntilReadyAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var response = await SendAsync(_resetUri, cancellationToken);
                if (response.StatusCode == HttpStatusCode.Found)
                {
                    return;
                }

                if (IsTransient(response.StatusCode))
                {
                    _error.WriteLine($"Reader reset returned HTTP {(int)response.StatusCode}; retrying.");
                    await DelayBeforeRetryAsync(cancellationToken);
                    continue;
                }

                throw new FeedProtocolException(response.StatusCode, "reader reset");
            }
            catch (HttpRequestException)
            {
                _error.WriteLine("Reader reset request failed; retrying.");
                await DelayBeforeRetryAsync(cancellationToken);
            }
            catch (IOException)
            {
                _error.WriteLine("Reader reset response failed; retrying.");
                await DelayBeforeRetryAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _error.WriteLine("Reader reset was interrupted; retrying.");
                await DelayBeforeRetryAsync(cancellationToken);
            }
        }
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (_options.Authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", _options.Authorization);
        }

        return await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
    }

    private void LogCommandFailure(CommandExecutionResult result)
    {
        if (!result.Started)
        {
            _error.WriteLine("Could not start the mudslide command.");
            return;
        }

        if (result.ExitCode == 0)
        {
            return;
        }

        _error.WriteLine($"The mudslide command exited with code {result.ExitCode}.");
    }

    private Task DelayBeforeRetryAsync(CancellationToken cancellationToken)
    {
        return _retryDelay == TimeSpan.Zero
            ? Task.CompletedTask
            : Task.Delay(_retryDelay, cancellationToken);
    }

    private static bool IsTransient(HttpStatusCode statusCode)
    {
        return statusCode == HttpStatusCode.RequestTimeout
            || statusCode == HttpStatusCode.TooManyRequests
            || (int)statusCode >= 500;
    }
}
