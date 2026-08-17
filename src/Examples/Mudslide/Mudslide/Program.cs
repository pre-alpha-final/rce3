namespace Mudslide;

internal class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (!MudslideOptions.TryCreate(args, Environment.GetEnvironmentVariable, out var options, out var problem))
        {
            Console.Error.WriteLine(problem);
            Console.Error.WriteLine("Usage: Mudslide [feed-url] [authorization]");
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
            var npxCommand = NpxCommand.Resolve();
            if (npxCommand is null)
            {
                Console.Error.WriteLine("Could not locate Node.js and the npx CLI on PATH.");
                return 1;
            }

            using var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false
            };
            using var client = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };

            var commandRunner = new SystemCommandRunner();
            var notificationSender = new MudslideNotificationSender(commandRunner, npxCommand);
            var listener = new FeedListener(
                client,
                options!,
                Guid.NewGuid(),
                notificationSender,
                TimeSpan.FromSeconds(1),
                Console.Error);

            await listener.RunAsync(cancellation.Token);
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return 0;
        }
        catch (FeedProtocolException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Mudslide listener stopped: {exception.Message}");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }
}
