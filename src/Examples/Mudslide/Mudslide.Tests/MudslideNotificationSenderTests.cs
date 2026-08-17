namespace Mudslide.Tests;

public class MudslideNotificationSenderTests
{
    [Fact]
    public async Task PassesNotificationAsOneArgumentWithoutUsingAShell()
    {
        var runner = new RecordingCommandRunner(new CommandExecutionResult(true, 0));
        var sender = new MudslideNotificationSender(
            runner,
            new NpxCommand("node.exe", @"C:\nodejs\node_modules\npm\bin\npx-cli.js"));

        var result = await sender.SendAsync(
            "hello \"friend\" & whoami\r\nnext",
            CancellationToken.None);

        Assert.True(result.Started);
        var request = Assert.Single(runner.Requests);
        Assert.Equal("node.exe", request.FileName);
        Assert.Equal(
            [
                @"C:\nodejs\node_modules\npm\bin\npx-cli.js",
                "mudslide",
                "send",
                "me",
                "hello \"friend\" & whoami\\nnext"
            ],
            request.Arguments);
        Assert.Equal(Path.GetTempPath(), request.WorkingDirectory);
    }

    [Fact]
    public async Task ReturnsLaunchFailure()
    {
        var runner = new RecordingCommandRunner(CommandExecutionResult.FailedToStart);
        var sender = new MudslideNotificationSender(runner, new NpxCommand("npx", null));

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
