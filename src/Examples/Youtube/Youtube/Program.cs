namespace Youtube;

internal class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Youtube requires Windows because it sends keyboard input through the Windows API.");
            return 1;
        }

        if (!YoutubeOptions.TryCreate(args, Environment.GetEnvironmentVariable, out var options, out var problem))
        {
            Console.Error.WriteLine(problem);
            Console.Error.WriteLine("Usage: Youtube [feed-url] [authorization]");
            return 1;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            using var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false
            };
            using var client = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };

            var commandHandler = new YoutubeCommandHandler(
                new WinApiKeyboard(),
                Console.Out,
                Console.Error);
            var listener = new FeedListener(
                client,
                options!,
                Guid.NewGuid(),
                commandHandler,
                TimeSpan.FromSeconds(1),
                Console.Out,
                Console.Error);

            await listener.RunAsync(cancellation.Token);
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.Out.WriteLine("Listener stopped.");
            return 0;
        }
        catch (FeedProtocolException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Youtube listener stopped: {exception.Message}");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }
}
