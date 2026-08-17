namespace Mudslide.Tests;

public class MudslideNotificationSenderTests
{
    [Fact]
    public async Task UsesCmdAndNpxMudslideCommand()
    {
        var runner = new RecordingCommandRunner(new CommandExecutionResult(true, 0));
        var sender = new MudslideNotificationSender(runner);

        var result = await sender.SendAsync("hello \"friend\"\r\nnext", CancellationToken.None);

        Assert.True(result.Started);
        var request = Assert.Single(runner.Requests);
        Assert.Equal("cmd.exe", request.FileName);
        Assert.Equal(" /C npx mudslide send me \"hello \\\"friend\\\"\\nnext\"", request.Arguments);
        Assert.Equal(Path.GetTempPath(), request.WorkingDirectory);
    }

    [Fact]
    public async Task ReturnsLaunchFailure()
    {
        var runner = new RecordingCommandRunner(CommandExecutionResult.FailedToStart);
        var sender = new MudslideNotificationSender(runner);

        var result = await sender.SendAsync("text", CancellationToken.None);

        Assert.False(result.Started);
        Assert.Null(result.ExitCode);
    }

    private sealed class RecordingCommandRunner(CommandExecutionResult result) : ICommandRunner
    {
        public List<CommandRequest> Requests { get; } = [];

        public Task<CommandExecutionResult> RunAsync(
            CommandRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(result);
        }
    }
}
