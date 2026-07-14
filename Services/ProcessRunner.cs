using System.Diagnostics;
using System.Text;

namespace UnrealManager.Services;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}

public static class ProcessRunner
{
    /// <summary>
    /// Runs a process asynchronously, streaming stdout/stderr line by line to <paramref name="onOutput"/>.
    /// Cancellation kills the whole process tree.
    /// </summary>
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        string arguments,
        string? workingDirectory = null,
        Action<string>? onOutput = null,
        CancellationToken ct = default,
        string? stdin = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory ?? string.Empty,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stdout.AppendLine(e.Data);
            onOutput?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stderr.AppendLine(e.Data);
            onOutput?.Invoke(e.Data);
        };

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start process: {fileName}");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin);
            process.StandardInput.Close();
        }

        using var registration = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
        });

        await process.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();

        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Runs a .bat/.cmd file through cmd.exe.</summary>
    public static Task<ProcessResult> RunBatchAsync(
        string batchFile,
        string arguments,
        string? workingDirectory = null,
        Action<string>? onOutput = null,
        CancellationToken ct = default)
    {
        var args = $"/c \"\"{batchFile}\" {arguments}\"";
        return RunAsync("cmd.exe", args, workingDirectory, onOutput, ct);
    }

    /// <summary>
    /// Re-reads PATH from the registry (machine + user) into this process, so tools
    /// installed while the app is running (e.g. via winget) are found without a restart.
    /// </summary>
    public static void RefreshProcessPath()
    {
        var machine = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine) ?? "";
        var user = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
        Environment.SetEnvironmentVariable("Path", machine + ";" + user, EnvironmentVariableTarget.Process);
    }

    /// <summary>Returns true if an executable can be resolved on PATH.</summary>
    public static async Task<string?> WhereAsync(string exe)
    {
        try
        {
            var result = await RunAsync("where.exe", exe);
            if (!result.Success) return null;
            var first = result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return first?.Trim();
        }
        catch
        {
            return null;
        }
    }
}
