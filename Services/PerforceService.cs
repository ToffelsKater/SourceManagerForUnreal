namespace UnrealManager.Services;

/// <summary>Thin wrapper around the p4 command line. Connection settings are passed per-call.</summary>
public sealed class P4Connection
{
    public string Port { get; init; } = "";
    public string User { get; init; } = "";
    public string Client { get; init; } = "";

    public string GlobalArgs(bool includeClient = true)
    {
        var args = "";
        if (!string.IsNullOrWhiteSpace(Port)) args += $"-p \"{Port}\" ";
        if (!string.IsNullOrWhiteSpace(User)) args += $"-u \"{User}\" ";
        if (includeClient && !string.IsNullOrWhiteSpace(Client)) args += $"-c \"{Client}\" ";
        return args;
    }
}

public static class PerforceService
{
    public static Task<ProcessResult> InfoAsync(P4Connection conn, Action<string> onOutput, CancellationToken ct)
        => ProcessRunner.RunAsync("p4", conn.GlobalArgs() + "info", onOutput: onOutput, ct: ct);

    /// <summary>`p4 login -s`: exit code 0 means a valid session ticket exists.</summary>
    public static Task<ProcessResult> LoginStatusAsync(P4Connection conn, CancellationToken ct)
        => ProcessRunner.RunAsync("p4", conn.GlobalArgs(includeClient: false) + "login -s", ct: ct);

    /// <summary>Logs in by piping the password to `p4 login` stdin (never shown in the log).</summary>
    public static Task<ProcessResult> LoginAsync(P4Connection conn, string password, Action<string> onOutput, CancellationToken ct)
        => ProcessRunner.RunAsync("p4", conn.GlobalArgs(includeClient: false) + "login",
            onOutput: onOutput, ct: ct, stdin: password + "\n");

    public static async Task<List<string>> ListClientsAsync(P4Connection conn, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(
            "p4", conn.GlobalArgs(includeClient: false) + $"clients -u \"{conn.User}\"", ct: ct);
        if (!result.Success) return [];

        // Lines look like: Client my_workspace 2024/01/01 root D:\ws 'description'
        return result.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.StartsWith("Client "))
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 2)
            .Select(parts => parts[1])
            .ToList();
    }

    public static Task<ProcessResult> SyncAsync(
        P4Connection conn, string? path, bool force, bool parallel,
        Action<string> onOutput, CancellationToken ct)
    {
        var args = conn.GlobalArgs() + "sync ";
        if (parallel) args += "--parallel=threads=8 ";
        if (force) args += "-f ";
        if (!string.IsNullOrWhiteSpace(path)) args += $"\"{path}\"";
        return ProcessRunner.RunAsync("p4", args.Trim(), onOutput: onOutput, ct: ct);
    }

    public static void OpenP4V(P4Connection conn)
    {
        var args = "";
        if (!string.IsNullOrWhiteSpace(conn.Port)) args += $"-p \"{conn.Port}\" ";
        if (!string.IsNullOrWhiteSpace(conn.User)) args += $"-u \"{conn.User}\" ";
        if (!string.IsNullOrWhiteSpace(conn.Client)) args += $"-c \"{conn.Client}\" ";
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "p4v",
            Arguments = args.Trim(),
            UseShellExecute = true,
        });
    }
}
