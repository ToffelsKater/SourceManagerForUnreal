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

/// <summary>A pending changelist in the current workspace, as offered for submission.</summary>
public sealed record PendingChange(string Id, string Description)
{
    /// <summary>Files opened outside any numbered changelist live in "default".</summary>
    public bool IsDefault => Id == DefaultId;

    public const string DefaultId = "default";

    /// <summary>Depot paths open in this changelist — exactly what a submit would publish.</summary>
    public List<string> Files { get; init; } = [];

    /// <summary>The description in full; the one in <see cref="Description"/> is p4's truncated form.</summary>
    public string FullDescription { get; init; } = "";

    public string User { get; init; } = "";

    /// <summary>Description column, using the same placeholder P4V shows for an undescribed changelist.</summary>
    public string DescriptionDisplay => Description.Length == 0 ? "<enter description here>" : Description;

    public int FileCount => Files.Count;
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

    /// <summary>Streams on the server (`p4 streams`), as depot paths like //Game/Main.</summary>
    public static async Task<List<string>> ListStreamsAsync(P4Connection conn, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(
            "p4", conn.GlobalArgs(includeClient: false) + "streams", ct: ct);
        if (!result.Success) return [];

        // Lines look like: Stream //Game/Main mainline none 'Main line'
        return result.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.StartsWith("Stream "))
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 2)
            .Select(parts => parts[1])
            .ToList();
    }

    /// <summary>
    /// The stream a workspace is currently bound to, or null when it is a classic (non-stream)
    /// workspace — which is what tells us whether `p4 switch` can be used on it at all.
    /// </summary>
    public static async Task<string?> GetClientStreamAsync(P4Connection conn, string client, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(client)) return null;

        // -ztag prints one "... Field value" line per field, which is far easier to read than the form.
        var result = await ProcessRunner.RunAsync(
            "p4", "-ztag " + conn.GlobalArgs(includeClient: false) + $"client -o \"{client}\"", ct: ct);
        if (!result.Success) return null;

        return result.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("... Stream "))
            .Select(l => l["... Stream ".Length..].Trim())
            .FirstOrDefault(s => s.Length > 0);
    }

    /// <summary>
    /// `p4 switch` — re-points a stream workspace at another stream and updates the files it maps.
    /// Fails (and says so) if the workspace has opened files or is not a stream workspace.
    /// </summary>
    public static Task<ProcessResult> SwitchStreamAsync(
        P4Connection conn, string stream, Action<string> onOutput, CancellationToken ct)
        => ProcessRunner.RunAsync("p4", conn.GlobalArgs() + $"switch \"{stream}\"",
            onOutput: onOutput, ct: ct);

    public static Task<ProcessResult> SyncAsync(
        P4Connection conn, string? path, string? changelist, bool force, bool parallel,
        Action<string> onOutput, CancellationToken ct)
    {
        var args = conn.GlobalArgs() + "sync ";
        if (parallel) args += "--parallel=threads=8 ";
        if (force) args += "-f ";

        // A revision specifier sticks to the path; with no path it applies to the whole workspace.
        var spec = string.IsNullOrWhiteSpace(path) ? "" : path.Trim();
        var rev = NormalizeRevision(changelist);
        if (rev.Length > 0 || spec.Length > 0) args += $"\"{spec}{rev}\"";

        return ProcessRunner.RunAsync("p4", args.Trim(), onOutput: onOutput, ct: ct);
    }

    /// <summary>Turns "12345", "@12345" or a label name into the "@rev" suffix p4 expects.</summary>
    public static string NormalizeRevision(string? changelist)
    {
        var value = (changelist ?? "").Trim().TrimStart('@').Trim();
        return value.Length == 0 ? "" : "@" + value;
    }

    /* ---------------------------- pending changelists ---------------------------- */

    /// <summary>
    /// Pending changelists this workspace can submit: the user's numbered ones, plus the default
    /// changelist when anything is open in it. Changelists belonging to another workspace are left
    /// out because they cannot be submitted from here. Each one comes back with its open files and
    /// full description already loaded, so picking one in the UI needs no further round trips.
    /// </summary>
    public static async Task<List<PendingChange>> ListPendingChangesAsync(P4Connection conn, CancellationToken ct)
    {
        var changes = new List<PendingChange>();

        // Files opened with no numbered changelist are submitted as "default".
        var openedInDefault = await OpenedFilesAsync(conn, PendingChange.DefaultId, ct);
        if (openedInDefault.Count > 0)
            changes.Add(new PendingChange(PendingChange.DefaultId, "")
            {
                Files = openedInDefault,
                User = conn.User,
            });

        var args = conn.GlobalArgs() + "changes -s pending";
        if (!string.IsNullOrWhiteSpace(conn.User)) args += $" -u \"{conn.User}\"";
        if (!string.IsNullOrWhiteSpace(conn.Client)) args += $" -c \"{conn.Client}\"";

        var result = await ProcessRunner.RunAsync("p4", args, ct: ct);
        if (!result.Success) return changes;

        foreach (var change in ParseChanges(result.StdOut))
        {
            changes.Add(change with
            {
                Files = await OpenedFilesAsync(conn, change.Id, ct),
                FullDescription = await GetChangeDescriptionAsync(conn, change.Id, ct),
            });
        }

        return changes;
    }

    /// <summary>Reads the id and short description out of `p4 changes` output.</summary>
    public static List<PendingChange> ParseChanges(string stdout)
    {
        var changes = new List<PendingChange>();

        // Lines look like: Change 12345 on 2026/09/05 by user@ws *pending* 'short description '
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var text = line.Trim();
            if (!text.StartsWith("Change ")) continue;

            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var id = words.ElementAtOrDefault(1);
            if (id is null || id.Length == 0 || !id.All(char.IsDigit)) continue;

            // The owner is written "by user@workspace"; only the user half is worth a column.
            var owner = words.SkipWhile(w => w != "by").ElementAtOrDefault(1) ?? "";
            var user = owner.Split('@')[0];

            var quoted = text.IndexOf('\'');
            var lastQuote = text.LastIndexOf('\'');
            var description = quoted >= 0 && lastQuote > quoted ? text[(quoted + 1)..lastQuote].Trim() : "";
            changes.Add(new PendingChange(id, description) { User = user });
        }

        return changes;
    }

    /// <summary>Depot paths open in a changelist, with the action taken on each.</summary>
    public static async Task<List<string>> OpenedFilesAsync(P4Connection conn, string changelist, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(
            "p4", conn.GlobalArgs() + $"opened -c \"{changelist}\"", ct: ct);

        // "no file(s) opened" comes back on stderr with a non-zero exit code; that is not an error here.
        if (!result.Success) return [];

        // Lines look like: //depot/Game/File.cpp#3 - edit change 12345 (text)
        return result.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("//"))
            .ToList();
    }

    /// <summary>The full, multi-line description of a numbered changelist.</summary>
    public static async Task<string> GetChangeDescriptionAsync(P4Connection conn, string changelist, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(
            "p4", conn.GlobalArgs() + $"change -o \"{changelist}\"", ct: ct);
        return result.Success ? ParseDescription(result.StdOut) : "";
    }

    /// <summary>
    /// Pulls the Description field out of a `p4 change -o` form. Each field's value is indented,
    /// so the description runs until the next unindented line.
    /// </summary>
    public static string ParseDescription(string form)
    {
        var lines = new List<string>();
        var inDescription = false;
        foreach (var raw in form.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("Description:")) { inDescription = true; continue; }
            if (!inDescription) continue;
            if (line.Length > 0 && !char.IsWhiteSpace(line[0])) break;
            lines.Add(line.Trim());
        }

        return string.Join(Environment.NewLine, lines).Trim();
    }

    /// <summary>
    /// Rewrites a numbered changelist's description by feeding the edited form back to `p4 change -i`,
    /// which is the only way to change it without opening an editor.
    /// </summary>
    public static async Task<ProcessResult> SetChangeDescriptionAsync(
        P4Connection conn, string changelist, string description, CancellationToken ct)
    {
        var form = await ProcessRunner.RunAsync(
            "p4", conn.GlobalArgs() + $"change -o \"{changelist}\"", ct: ct);
        if (!form.Success) return form;

        return await ProcessRunner.RunAsync("p4", conn.GlobalArgs() + "change -i",
            ct: ct, stdin: ReplaceDescription(form.StdOut, description));
    }

    /// <summary>Returns a `p4 change` form with its Description field replaced, ready for `change -i`.</summary>
    public static string ReplaceDescription(string form, string description)
    {
        var rebuilt = new List<string>();
        var inDescription = false;
        foreach (var raw in form.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (inDescription)
            {
                // Skip the old description's indented lines; the next field ends it.
                if (line.Length == 0 || char.IsWhiteSpace(line[0])) continue;
                inDescription = false;
            }

            if (line.StartsWith("Description:"))
            {
                inDescription = true;
                rebuilt.Add("Description:");
                foreach (var descriptionLine in description.Replace("\r\n", "\n").Split('\n'))
                    rebuilt.Add("\t" + descriptionLine);
                continue;
            }

            rebuilt.Add(line);
        }

        return string.Join("\n", rebuilt) + "\n";
    }

    /// <summary>
    /// Submits a changelist. The default changelist has no stored description, so one is passed on
    /// the command line; numbered changelists submit with the description they already carry.
    /// </summary>
    public static Task<ProcessResult> SubmitAsync(
        P4Connection conn, PendingChange change, string description, Action<string> onOutput, CancellationToken ct)
    {
        var args = conn.GlobalArgs() + "submit ";
        args += change.IsDefault
            ? $"-d \"{description.Replace("\"", "'").Replace("\r\n", " ").Replace("\n", " ")}\""
            : $"-c \"{change.Id}\"";
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
