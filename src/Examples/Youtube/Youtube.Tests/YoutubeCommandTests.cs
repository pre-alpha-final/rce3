using System.Text;
using YtAgent;

namespace Youtube.Tests;

public class YoutubeCommandTests
{
    [Theory]
    [InlineData("youtube: back6", WinApi.ScanCodes.J, 6)]
    [InlineData("youtube: back", WinApi.ScanCodes.J, 1)]
    [InlineData("youtube: playpause", WinApi.ScanCodes.K, 1)]
    [InlineData("youtube: forward", WinApi.ScanCodes.L, 1)]
    [InlineData("youtube: forward6", WinApi.ScanCodes.L, 6)]
    public void ExactBodyMapsToExpectedKeypresses(
        string body,
        WinApi.ScanCodes expectedScanCode,
        int expectedRepetitions)
    {
        var succeeded = YoutubeCommand.TryParse(Encoding.UTF8.GetBytes(body), out var command);

        Assert.True(succeeded);
        Assert.Equal(expectedScanCode, command.ScanCode);
        Assert.Equal(expectedRepetitions, command.Repetitions);
    }

    [Fact]
    public void NonExactBodiesAreRejected()
    {
        byte[][] bodies =
        [
            Encoding.UTF8.GetBytes("Youtube: back"),
            Encoding.UTF8.GetBytes(" youtube: back"),
            Encoding.UTF8.GetBytes("youtube: back "),
            Encoding.UTF8.GetBytes("youtube: back\n"),
            Encoding.UTF8.GetBytes("youtube: unknown"),
            [0xff, 0xfe]
        ];

        Assert.All(bodies, body => Assert.False(YoutubeCommand.TryParse(body, out _)));
    }

    [Fact]
    public async Task HandlerPressesKeysSequentially()
    {
        var keyboard = new RecordingKeyboard();
        var handler = new YoutubeCommandHandler(keyboard, TextWriter.Null, TextWriter.Null);

        await handler.HandleAsync(Encoding.UTF8.GetBytes("youtube: back6"), CancellationToken.None);
        await handler.HandleAsync(Encoding.UTF8.GetBytes("youtube: playpause"), CancellationToken.None);

        Assert.Equal(
            [
                WinApi.ScanCodes.J,
                WinApi.ScanCodes.J,
                WinApi.ScanCodes.J,
                WinApi.ScanCodes.J,
                WinApi.ScanCodes.J,
                WinApi.ScanCodes.J,
                WinApi.ScanCodes.K
            ],
            keyboard.Presses);
        Assert.Equal(1, keyboard.MaximumConcurrentPresses);
    }

    [Fact]
    public async Task UnknownBodyIsIgnoredWithoutBeingLogged()
    {
        var body = Encoding.UTF8.GetBytes("private unknown command");
        var keyboard = new RecordingKeyboard();
        var output = new StringWriter();
        var handler = new YoutubeCommandHandler(keyboard, output, TextWriter.Null);

        await handler.HandleAsync(body, CancellationToken.None);

        Assert.Empty(keyboard.Presses);
        Assert.Contains("ignored", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private unknown command", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task KeyboardFailureIsContainedAndLoggedWithoutCommandBody()
    {
        var error = new StringWriter();
        var handler = new YoutubeCommandHandler(new FailingKeyboard(), TextWriter.Null, error);

        await handler.HandleAsync(Encoding.UTF8.GetBytes("youtube: forward6"), CancellationToken.None);

        Assert.Contains("failed", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("youtube: forward6", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationIsNotTreatedAsAKeyboardFailure()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = new StringWriter();
        var handler = new YoutubeCommandHandler(new RecordingKeyboard(), TextWriter.Null, error);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => handler.HandleAsync(Encoding.UTF8.GetBytes("youtube: back"), cancellation.Token));

        Assert.Empty(error.ToString());
    }

    private sealed class RecordingKeyboard : IKeyboard
    {
        private int activePresses;

        public List<WinApi.ScanCodes> Presses { get; } = [];

        public int MaximumConcurrentPresses { get; private set; }

        public async Task PressAsync(WinApi.ScanCodes scanCode, CancellationToken cancellationToken)
        {
            var currentPresses = Interlocked.Increment(ref activePresses);
            MaximumConcurrentPresses = Math.Max(MaximumConcurrentPresses, currentPresses);
            try
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                Presses.Add(scanCode);
            }
            finally
            {
                Interlocked.Decrement(ref activePresses);
            }
        }
    }

    private sealed class FailingKeyboard : IKeyboard
    {
        public Task PressAsync(WinApi.ScanCodes scanCode, CancellationToken cancellationToken)
        {
            return Task.FromException(new InvalidOperationException("private implementation detail"));
        }
    }
}
