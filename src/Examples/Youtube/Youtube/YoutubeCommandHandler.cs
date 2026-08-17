using YtAgent;

namespace Youtube;

internal interface IYoutubeCommandHandler
{
    Task HandleAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken);
}

internal interface IKeyboard
{
    Task PressAsync(WinApi.ScanCodes scanCode, CancellationToken cancellationToken);
}

internal sealed class WinApiKeyboard : IKeyboard
{
    public async Task PressAsync(WinApi.ScanCodes scanCode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await WinApi.KeyboardPress(scanCode);
        cancellationToken.ThrowIfCancellationRequested();
    }
}

internal sealed class YoutubeCommandHandler(
    IKeyboard keyboard,
    TextWriter output,
    TextWriter error) : IYoutubeCommandHandler
{
    public async Task HandleAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        if (!YoutubeCommand.TryParse(body.Span, out var command))
        {
            output.WriteLine("Feed message ignored; it is not a Youtube command.");
            return;
        }

        try
        {
            for (var press = 0; press < command.Repetitions; press++)
            {
                await keyboard.PressAsync(command.ScanCode, cancellationToken);
            }

            output.WriteLine("Youtube command executed.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            error.WriteLine("Youtube keyboard input failed; continuing to listen.");
        }
    }
}
