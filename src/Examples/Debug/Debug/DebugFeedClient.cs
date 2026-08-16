using System.Net;
using System.Text;

namespace Debug;

internal sealed class DebugFeedClient : IDisposable
{
    private readonly DebugOptions options;
    private readonly HttpClient client;

    public DebugFeedClient(DebugOptions options)
    {
        this.options = options;
        client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var readerId = Guid.NewGuid();
        var readerUri = new Uri($"{options.FeedUri.AbsoluteUri}/{readerId:D}");

        await ResetReaderAsync(readerUri, cancellationToken);
        Console.Error.WriteLine($"Connected reader {readerId:D} to {options.FeedUri}.");

        var receiveTask = ReceiveAsync(readerUri, cancellationToken);
        var sendTask = SendAsync(cancellationToken);
        var firstTask = await Task.WhenAny(receiveTask, sendTask);

        if (firstTask == sendTask && sendTask.Status == TaskStatus.RanToCompletion)
        {
            await receiveTask;
            return;
        }

        await firstTask;
    }

    public void Dispose()
    {
        client.Dispose();
    }

    private async Task ResetReaderAsync(Uri readerUri, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, new Uri($"{readerUri.AbsoluteUri}/reset"));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (response.StatusCode != HttpStatusCode.Found)
        {
            await ThrowUnexpectedResponseAsync("reset reader", response, cancellationToken);
        }
    }

    private async Task ReceiveAsync(Uri readerUri, CancellationToken cancellationToken)
    {
        var output = Console.OpenStandardOutput();
        var separator = Encoding.UTF8.GetBytes(Environment.NewLine);

        while (true)
        {
            using var request = CreateRequest(HttpMethod.Get, readerUri);
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                continue;
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                await ThrowUnexpectedResponseAsync("receive message", response, cancellationToken);
            }

            await response.Content.CopyToAsync(output, cancellationToken);
            await output.WriteAsync(separator, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
    }

    private async Task SendAsync(CancellationToken cancellationToken)
    {
        while (await Console.In.ReadLineAsync(cancellationToken) is { } line)
        {
            using var request = CreateRequest(HttpMethod.Post, options.FeedUri);
            request.Content = new StringContent(line, Encoding.UTF8, "text/plain");

            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                await ThrowUnexpectedResponseAsync("send message", response, cancellationToken);
            }
        }

        Console.Error.WriteLine("Standard input closed; continuing to receive until canceled.");
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        if (options.Authorization is not null
            && !request.Headers.TryAddWithoutValidation("Authorization", options.Authorization))
        {
            request.Dispose();
            throw new InvalidOperationException("The Authorization value could not be added to the request.");
        }

        return request;
    }

    private static async Task ThrowUnexpectedResponseAsync(
        string operation,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var detail = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
        var detailSuffix = detail.Length == 0 ? string.Empty : $": {detail}";
        throw new HttpRequestException(
            $"Could not {operation}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}{detailSuffix}",
            null,
            response.StatusCode);
    }
}
