using System.Text.Json;
using System.Threading.Channels;

namespace RequestProxy;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Request Proxy requires Windows.");
            return 1;
        }

        string[] paths;
        try
        {
            paths = args.Select(Path.GetFullPath).ToArray();
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            Console.Error.WriteLine("A HAR path is invalid.");
            return 1;
        }

        var directory = AppContext.BaseDirectory;
        try
        {
            var handoff = new InstanceHandoff(directory);
            using var mutex = handoff.CreateMutex();
            if (!InstanceHandoff.TryAcquire(mutex))
            {
                return Forward(handoff, paths);
            }

            // Mutex ownership stays on this thread for the life of the primary process.
            try
            {
                return RunPrimary(handoff, directory, paths);
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            Console.Error.WriteLine("Could not open the reader identity or the local instance handoff.");
            return 1;
        }
    }

    private static int Forward(InstanceHandoff handoff, string[] paths)
    {
        if (paths.Length == 0)
        {
            Console.WriteLine("Request Proxy is already running.");
            return 0;
        }

        try
        {
            handoff.ForwardAsync(paths).GetAwaiter().GetResult();
            Console.WriteLine("HAR files queued in the running Request Proxy.");
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            Console.Error.WriteLine("Could not hand off the HAR files to the running Request Proxy.");
            return 1;
        }
    }

    private static int RunPrimary(InstanceHandoff handoff, string directory, string[] paths)
    {
        using var shutdown = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, args) =>
        {
            args.Cancel = true;
            shutdown.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            var options = FeedOptions.Load();
            var readerId = ReaderIdentity.LoadOrCreate(directory);
            RunAsync(handoff, options, readerId, paths, shutdown).GetAwaiter().GetResult();
            return 0;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or FeedProtocolException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    private static async Task RunAsync(InstanceHandoff handoff, FeedOptions options, Guid readerId,
        string[] initialPaths, CancellationTokenSource shutdown)
    {
        using var sessions = new ProxySessions(TimeSpan.FromSeconds(10));
        using var feed = new FeedConnection(options, readerId, sessions, TimeSpan.FromSeconds(1));
        var imports = Channel.CreateUnbounded<string[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var token = shutdown.Token;
        var startup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipe = handoff.ListenAsync(imports.Writer, token);
        var importer = ImportAsync(sessions, initialPaths, imports.Reader, startup, token);
        var listener = StartFeedAsync(feed, startup.Task, token);
        try
        {
            await Task.WhenAny(pipe, importer, listener);
        }
        finally
        {
            await shutdown.CancelAsync();
            await Task.WhenAll(pipe, importer, listener);
        }
    }

    private static async Task StartFeedAsync(FeedConnection feed, Task startup, CancellationToken cancellationToken)
    {
        await startup.WaitAsync(cancellationToken);
        await feed.RunAsync(cancellationToken);
    }

    private static async Task ImportAsync(ProxySessions sessions, string[] initialPaths, ChannelReader<string[]> queue,
        TaskCompletionSource startup, CancellationToken cancellationToken)
    {
        await ImportFilesAsync(sessions, initialPaths, cancellationToken);
        startup.SetResult();
        await foreach (var batch in queue.ReadAllAsync(cancellationToken))
        {
            await ImportFilesAsync(sessions, batch, cancellationToken);
        }
    }

    private static async Task ImportFilesAsync(ProxySessions sessions, string[] paths, CancellationToken cancellationToken)
    {
        for (var index = 0; index < paths.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var count = await sessions.ImportAsync(paths[index], cancellationToken);
                Console.WriteLine($"Imported {count} origin(s) from HAR file {index + 1}.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
                or InvalidDataException or ArgumentException or NotSupportedException)
            {
                Console.Error.WriteLine($"HAR file {index + 1} could not be imported.");
            }
        }
    }
}
