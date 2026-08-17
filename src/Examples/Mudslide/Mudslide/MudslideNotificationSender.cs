using System.ComponentModel;
using System.Diagnostics;

namespace Mudslide;

internal sealed record CommandRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory);

internal sealed record NpxCommand(string FileName, string? ScriptPath)
{
    public static NpxCommand? Resolve()
    {
        if (!OperatingSystem.IsWindows())
        {
            var npxPath = FindOnPath("npx");
            return npxPath is null ? null : new NpxCommand(npxPath, null);
        }

        var npxExecutable = FindOnPath("npx.exe");
        if (npxExecutable is not null)
        {
            return new NpxCommand(npxExecutable, null);
        }

        // npx.cmd forwards its arguments through cmd.exe. Invoke its JavaScript entry point
        // directly so notification text is never parsed as a shell command.
        var nodePath = FindOnPath("node.exe");
        if (nodePath is null)
        {
            return null;
        }

        var nodeDirectory = Path.GetDirectoryName(nodePath)!;
        var searchDirectories = new[] { nodeDirectory }
            .Concat(GetPathDirectories())
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in searchDirectories)
        {
            var scriptPath = Path.Combine(directory, "node_modules", "npm", "bin", "npx-cli.js");
            if (File.Exists(scriptPath))
            {
                return new NpxCommand(nodePath, scriptPath);
            }
        }

        return null;
    }

    private static string? FindOnPath(string fileName)
    {
        foreach (var directory in GetPathDirectories())
        {
            var candidate = Path.GetFullPath(Path.Combine(directory, fileName));
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> GetPathDirectories()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            yield break;
        }

        foreach (var value in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = value.Trim().Trim('"');
            if (directory.Length > 0)
            {
                yield return Path.GetFullPath(directory);
            }
        }
    }
}

internal readonly record struct CommandExecutionResult(
    bool Started,
    int? ExitCode)
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

internal sealed class MudslideNotificationSender(
    ICommandRunner commandRunner,
    NpxCommand npxCommand) : INotificationSender
{
    public Task<CommandExecutionResult> SendAsync(string notification, CancellationToken cancellationToken)
    {
        var arguments = new List<string>();
        if (npxCommand.ScriptPath is not null)
        {
            arguments.Add(npxCommand.ScriptPath);
        }

        arguments.Add("mudslide");
        arguments.Add("send");
        arguments.Add("me");
        arguments.Add(NotificationMessage.ToMudslideText(notification));

        var request = new CommandRequest(
            npxCommand.FileName,
            arguments,
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

        var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None);
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            throw;
        }

        await Task.WhenAll(stdout, stderr);
        return new CommandExecutionResult(true, process.ExitCode);
    }

    private static ProcessStartInfo CreateStartInfo(CommandRequest request)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

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
