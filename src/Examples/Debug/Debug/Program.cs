namespace Debug;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Any(argument => argument is "--help" or "-h"))
        {
            DebugOptions.WriteUsage(Console.Out);
            return 0;
        }

        if (!DebugOptions.TryParse(args, out var options, out var error))
        {
            Console.Error.WriteLine($"error: {error}");
            Console.Error.WriteLine();
            DebugOptions.WriteUsage(Console.Error);
            return 2;
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
            using var client = new DebugFeedClient(options);
            await client.RunAsync(cancellation.Token);
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception)
        {
            await cancellation.CancelAsync();
            Console.Error.WriteLine($"error: {exception.Message}");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }
}
