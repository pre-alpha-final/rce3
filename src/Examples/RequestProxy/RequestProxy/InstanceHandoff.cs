using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace RequestProxy;

internal sealed class InstanceHandoff
{
    private const PipeOptions Options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
    private const int MaxPacketBytes = 256 * 1024;
    private static readonly TimeSpan HandoffTimeout = TimeSpan.FromSeconds(10);
    private readonly string _pipeName;

    public InstanceHandoff(string directory)
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        var installation = Path.TrimEndingDirectorySeparator(directory).ToUpperInvariant();
        var scope = $"{identity.User?.Value}\n{process.SessionId}\n{installation}";
        _pipeName = "Rce3.RequestProxy." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
    }

    public Mutex CreateMutex() => new(false, $@"Local\{_pipeName}");

    public static bool TryAcquire(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    public async Task ForwardAsync(string[] paths)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(paths);
        if (payload.Length > MaxPacketBytes)
        {
            throw new IOException("HAR path list is too large.");
        }

        using var deadline = new CancellationTokenSource(HandoffTimeout);
        using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, Options);
        await pipe.ConnectAsync(deadline.Token);
        var size = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(size, payload.Length);
        await pipe.WriteAsync(size, deadline.Token);
        await pipe.WriteAsync(payload, deadline.Token);
        var reply = new byte[1];
        await pipe.ReadExactlyAsync(reply, deadline.Token);
        if (reply[0] != 1)
        {
            throw new IOException("The running instance did not accept the HAR paths.");
        }
    }

    public async Task ListenAsync(ChannelWriter<string[]> queue, CancellationToken cancellationToken)
    {
        while (true)
        {
            using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, Options);
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(HandoffTimeout);
                var paths = await ReadPathsAsync(pipe, deadline.Token);
                cancellationToken.ThrowIfCancellationRequested();
                var accepted = paths is not null && queue.TryWrite(paths);
                var reply = new byte[] { accepted ? (byte)1 : (byte)0 };
                await pipe.WriteAsync(reply, deadline.Token);
                // The client closes the pipe after reading the acknowledgment.
                await pipe.CopyToAsync(Stream.Null, deadline.Token);
            }
            catch (Exception exception) when (exception is IOException or JsonException
                || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                // A bad or disconnected launch can be discarded without stopping the listener.
            }
        }
    }

    private static async Task<string[]?> ReadPathsAsync(Stream pipe, CancellationToken cancellationToken)
    {
        var size = new byte[sizeof(int)];
        await pipe.ReadExactlyAsync(size, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(size);
        if (length is <= 0 or > MaxPacketBytes)
        {
            return null;
        }

        var payload = new byte[length];
        await pipe.ReadExactlyAsync(payload, cancellationToken);
        var paths = JsonSerializer.Deserialize<string[]>(payload);
        return paths is { Length: > 0 } && paths.All(path => path is not null && Path.IsPathFullyQualified(path))
            ? paths
            : null;
    }
}
