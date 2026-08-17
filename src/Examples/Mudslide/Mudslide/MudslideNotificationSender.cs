using System.ComponentModel;
using System.Diagnostics;

namespace Mudslide;

internal sealed record CommandRequest(string FileName, string Arguments, string WorkingDirectory);

internal readonly record struct CommandExecutionResult(
    bool Started,
    int? ExitCode,
    string StandardOutput = "",
    string StandardError = "")
{
    public static CommandExecutionResult FailedToStart => new(false, null);
}

internal interface ICommandRunner
{
    Task<CommandExecutionResult> RunAsync(CommandRequest request, CancellationToken cancellationToken);
}

internal interface INotificationSender
{
    Task<CommandExecutionResult> SendAsync(string notification, CancellationToken cancellationToken);
}

internal sealed class MudslideNotificationSender(ICommandRunner commandRunner) : INotificationSender
{
    public Task<CommandExecutionResult> SendAsync(string notification, CancellationToken cancellationToken)
    {
        var message = NotificationMessage
            .ToMudslideText(notification)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        var request = new CommandRequest(
            "cmd.exe",
            $" /C npx mudslide send me \"{message}\"",
            Path.GetTempPath());
        return commandRunner.RunAsync(request, cancellationToken);
    }
}

internal sealed class SystemCommandRunner : ICommandRunner
{
    public async Task<CommandExecutionResult> RunAsync(
        CommandRequest request,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = CreateStartInfo(request)
        };

        try
        {
            if (!process.Start())
            {
                return CommandExecutionResult.FailedToStart;
            }
        }
        catch (Win32Exception)
        {
            return CommandExecutionResult.FailedToStart;
        }
        catch (InvalidOperationException)
        {
            return CommandExecutionResult.FailedToStart;
        }

        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }

        await Task.WhenAll(stdout, stderr);
        return new CommandExecutionResult(
            true,
            process.ExitCode,
            await stdout,
            await stderr);
    }

    private static ProcessStartInfo CreateStartInfo(CommandRequest request)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            Arguments = request.Arguments,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        return startInfo;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }
}
