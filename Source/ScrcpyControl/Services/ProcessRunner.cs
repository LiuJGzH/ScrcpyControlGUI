using System.Diagnostics;
using System.IO;
using System.Text;

namespace ScrcpyControl.Services;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string CombinedOutput =>
        string.Join(
            Environment.NewLine,
            new[] { StandardOutput, StandardError }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
}

public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = CreateStartInfo(fileName, arguments, workingDirectory);
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        if (!process.Start())
        {
            throw new InvalidOperationException($"无法启动进程：{fileName}");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    public static ProcessStartInfo CreateStartInfo(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(fileName) ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    public static void TryKill(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The process may have already exited between the check and kill.
        }
    }

    public static async Task<ProcessResult> RunStreamingAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory,
        Action<string> onOutput,
        Action<string> onError,
        CancellationToken cancellationToken = default)
    {
        using var process = new Process
        {
            StartInfo = CreateStartInfo(fileName, arguments, workingDirectory),
            EnableRaisingEvents = true
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"无法启动进程：{fileName}");
        }

        await using var cancellationRegistration = cancellationToken.Register(
            static state => TryKill((Process?)state),
            process);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        var stdoutTask = PumpAsync(process.StandardOutput, stdout, onOutput, cancellationToken);
        var stderrTask = PumpAsync(process.StandardError, stderr, onError, cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static async Task PumpAsync(
        StreamReader reader,
        StringBuilder target,
        Action<string> callback,
        CancellationToken cancellationToken)
    {
        var buffer = new char[2048];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var chunk = new string(buffer, 0, read);
            target.Append(chunk);
            callback(chunk);
        }
    }
}
