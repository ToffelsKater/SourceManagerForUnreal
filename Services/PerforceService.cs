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

/// <summary>One file as a submitted changelist left it: what happened to it, and at which revision.</summary>
public sealed record ChangedFile(string DepotPath, string Action, string Revision)
{
    /// <summary>The file name alone — the part a reader actually scans for.</summary>
    public string FileName
    {
        get
        {
            var slash = DepotPath.LastIndexOf('/');
            return slash < 0 ? DepotPath : DepotPath[(slash + 1)..];
        }
    }

    /// <summary>The depot folder holding the file, shown under the name so the path is still there.</summary>
    public string Folder
    {
        get
        {
            var slash = DepotPath.LastIndexOf('/');
            return slash < 0 ? "" : DepotPath[..slash];
        }
    }

    public string RevisionDisplay => Revision.Length == 0 ? "" : "#" + Revision;

    /// <summary>A one-character stand-in for the action, so a long file list reads at a glance.</summary>
    public string ActionGlyph => Action switch
    {
        "add" or "move/add" or "branch" => "+",
        "delete" or "move/delete" or "purge" => "−",
        "edit" or "integrate" => "~",
        _ => "•",
    };

    /// <summary>Green for new files, red for removed ones, amber for edits — the usual diff reading.</summary>
    public string ActionColor => Action switch
    {
        "add" or "move/add" or "branch" => "#5CC98C",
        "delete" or "move/delete" or "purge" => "#E5646E",
        "edit" => "#E0A458",
        "integrate" => "#6BA4FF",
        _ => "#9AA0AE",
    };

    public bool IsAdd => Action is "add" or "move/add" or "branch";
    public bool IsDelete => Action is "delete" or "move/delete" or "purge";
    public bool IsEdit => !IsAdd && !IsDelete;
}

/// <summary>A changelist that has already been submitted — one entry of the stream's history.</summary>
public sealed record SubmittedChange(string Id, string User, DateTime Time, string Description)
{
    /// <summary>Files the changelist touched, loaded with the history so selecting one costs nothing.</summary>
    public List<ChangedFile> Files { get; init; } = [];

    /// <summary>Set when the file list was cut short; a sync-everything changelist can hold thousands.</summary>
    public bool FilesTruncated { get; init; }

    public int FileCount => Files.Count;
    public int AddedCount => Files.Count(f => f.IsAdd);
    public int DeletedCount => Files.Count(f => f.IsDelete);
    public int EditedCount => Files.Count(f => f.IsEdit);

    public string IdDisplay => "@" + Id;

    /// <summary>First letter of the author, for the round badge that starts each row.</summary>
    public string UserInitial => User.Length == 0 ? "?" : User[..1].ToUpperInvariant();

    /// <summary>The first line of the description — the subject, in git terms.</summary>
    public string Summary
    {
        get
        {
            var first = Description
                .Split('\n')
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.Length > 0);
            return string.IsNullOrEmpty(first) ? "(no description)" : first;
        }
    }

    /// <summary>The submit time in full, for the tooltip behind the "3 hours ago" in the row.</summary>
    public string TimeDisplay => Time.ToString("ddd d MMM yyyy, HH:mm");

    /// <summary>Header the history groups under: Today, Yesterday, then the full date.</summary>
    public string DayGroup
    {
        get
        {
            var day = Time.Date;
            if (day == DateTime.Today) return "Today";
            if (day == DateTime.Today.AddDays(-1)) return "Yesterday";
            return Time.ToString("dddd, d MMMM yyyy");
        }
    }

    /// <summary>"3 hours ago" — how a history is usually read, rather than by timestamp.</summary>
    public string AgoDisplay
    {
        get
        {
            var span = DateTime.Now - Time;
            if (span.TotalMinutes < 1) return "just now";
            if (span.TotalMinutes < 60) return Ago((int)span.TotalMinutes, "minute");
            if (span.TotalHours < 24) return Ago((int)span.TotalHours, "hour");
            if (span.TotalDays < 7) return Ago((int)span.TotalDays, "day");
            if (span.TotalDays < 30) return Ago((int)(span.TotalDays / 7), "week");
            if (span.TotalDays < 365) return Ago((int)(span.TotalDays / 30), "month");
            return Ago((int)(span.TotalDays / 365), "year");
        }
    }

    private static string Ago(int count, string unit) => $"{count} {unit}{(count == 1 ? "" : "s")} ago";

    /// <summary>Per-action counters for the row, blank when zero so the badges stay quiet.</summary>
    public string AddedBadge => AddedCount > 0 ? $"+{AddedCount}" : "";

    public string EditedBadge => EditedCount > 0 ? $"~{EditedCount}" : "";

    public string DeletedBadge => DeletedCount > 0 ? $"−{DeletedCount}" : "";

    public string FileCountDisplay => FilesTruncated
        ? $"{FileCount}+ files"
        : $"{FileCount} file{(FileCount == 1 ? "" : "s")}";

    /// <summary>"12 files — 3 added, 8 edited, 1 deleted", the row's one-line shape of the change.</summary>
    public string FileSummary
    {
        get
        {
            if (FileCount == 0) return "no files";
            var parts = new List<string>();
            if (AddedCount > 0) parts.Add($"{AddedCount} added");
            if (EditedCount > 0) parts.Add($"{EditedCount} edited");
            if (DeletedCount > 0) parts.Add($"{DeletedCount} deleted");
            var counted = $"{FileCount} file{(FileCount == 1 ? "" : "s")}";
            if (FilesTruncated) counted = "first " + counted;
            return parts.Count == 0 ? counted : $"{counted} — {string.Join(", ", parts)}";
        }
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

    /* ---------------------------- submitted history ---------------------------- */

    /// <summary>Files kept per changelist — a "sync the whole depot" submit can hold thousands.</summary>
    public const int MaxFilesPerChange = 500;

    /// <summary>
    /// The most recent submitted changelists under <paramref name="depotPath"/> (a stream path such as
    /// //Game/Main, which is completed to //Game/Main/... ), newest first. Each one comes back with its
    /// files already attached: they are fetched for the whole page in a single `p4 describe`, so
    /// clicking through the history costs no further round trips.
    /// </summary>
    public static async Task<List<SubmittedChange>> ListSubmittedChangesAsync(
        P4Connection conn, string? depotPath, int max, CancellationToken ct)
    {
        var args = conn.GlobalArgs(includeClient: false) + $"changes -s submitted -l -m {max}";
        var path = NormalizeDepotPath(depotPath);
        if (path.Length > 0) args += $" \"{path}\"";

        var listed = await ProcessRunner.RunAsync("p4", "-ztag " + args, ct: ct);
        if (!listed.Success) return [];

        var changes = ParseSubmittedChanges(listed.StdOut);
        if (changes.Count == 0) return changes;

        // One describe for every changelist on the page; `-s` leaves out the diffs, keeping it to a file list.
        var ids = string.Join(" ", changes.Select(c => c.Id));
        var described = await ProcessRunner.RunAsync(
            "p4", "-ztag " + conn.GlobalArgs(includeClient: false) + "describe -s " + ids, ct: ct);
        if (!described.Success) return changes;

        var files = ParseDescribedFiles(described.StdOut);
        return changes
            .Select(c => files.TryGetValue(c.Id, out var list)
                ? c with
                {
                    Files = list.Take(MaxFilesPerChange).ToList(),
                    FilesTruncated = list.Count > MaxFilesPerChange,
                }
                : c)
            .ToList();
    }

    /// <summary>
    /// Turns a stream or folder into the file pattern `p4 changes` expects: //Game/Main becomes
    /// //Game/Main/... , while a path that already ends in a wildcard is left alone.
    /// </summary>
    public static string NormalizeDepotPath(string? depotPath)
    {
        var path = (depotPath ?? "").Trim();
        if (path.Length == 0) return "";
        if (path.EndsWith("...") || path.EndsWith("*")) return path;
        return path.TrimEnd('/') + "/...";
    }

    /// <summary>Reads `p4 -ztag changes -l` output into changelists, newest first as p4 lists them.</summary>
    public static List<SubmittedChange> ParseSubmittedChanges(string stdout)
    {
        var changes = new List<SubmittedChange>();
        foreach (var block in ZtagBlocks(stdout))
        {
            if (!block.TryGetValue("change", out var id) || id.Length == 0) continue;

            // p4 reports the submit time as unix seconds; the history reads better in local time.
            var time = long.TryParse(block.GetValueOrDefault("time"), out var epoch)
                ? DateTimeOffset.FromUnixTimeSeconds(epoch).LocalDateTime
                : DateTime.MinValue;

            changes.Add(new SubmittedChange(
                id,
                block.GetValueOrDefault("user", "").Trim(),
                time,
                block.GetValueOrDefault("desc", "").Trim()));
        }

        return changes;
    }

    /// <summary>
    /// Reads `p4 -ztag describe -s` output into a file list per changelist. Fields are numbered per
    /// file (depotFile0, action0, rev0…), so they are gathered by that index before being paired up.
    /// </summary>
    public static Dictionary<string, List<ChangedFile>> ParseDescribedFiles(string stdout)
    {
        var byChange = new Dictionary<string, List<ChangedFile>>(StringComparer.Ordinal);

        foreach (var block in ZtagBlocks(stdout))
        {
            if (!block.TryGetValue("change", out var id) || id.Length == 0) continue;

            var paths = new SortedDictionary<int, string>();
            var actions = new Dictionary<int, string>();
            var revisions = new Dictionary<int, string>();

            foreach (var (key, value) in block)
            {
                if (TryIndexedField(key, "depotFile", out var pathIndex)) paths[pathIndex] = value.Trim();
                else if (TryIndexedField(key, "action", out var actionIndex)) actions[actionIndex] = value.Trim();
                else if (TryIndexedField(key, "rev", out var revIndex)) revisions[revIndex] = value.Trim();
            }

            byChange[id] = paths
                .Select(p => new ChangedFile(
                    p.Value,
                    actions.GetValueOrDefault(p.Key, ""),
                    revisions.GetValueOrDefault(p.Key, "")))
                .ToList();
        }

        return byChange;
    }

    /// <summary>Matches a numbered ztag field such as "depotFile12" and hands back the 12.</summary>
    private static bool TryIndexedField(string key, string prefix, out int index)
    {
        index = 0;
        if (!key.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var digits = key[prefix.Length..];
        return digits.Length > 0 && int.TryParse(digits, out index);
    }

    /// <summary>
    /// Splits `p4 -ztag` output into one field bag per changelist. Every field is written as
    /// "... name value", and a value running over several lines (a description) continues on the
    /// lines after it, so anything unprefixed is appended to the field last seen.
    /// </summary>
    public static IEnumerable<Dictionary<string, string>> ZtagBlocks(string stdout)
    {
        Dictionary<string, string>? block = null;
        string? field = null;

        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("... ", StringComparison.Ordinal))
            {
                var body = line[4..];
                var space = body.IndexOf(' ');
                var key = space < 0 ? body : body[..space];
                var value = space < 0 ? "" : body[(space + 1)..];

                // "change" is the first field of every record, so it is what starts a new one.
                if (key == "change" && block is not null) { yield return block; block = null; }

                block ??= new Dictionary<string, string>(StringComparer.Ordinal);
                block[key] = value;
                field = key;
            }
            else if (block is not null && field is not null)
            {
                block[field] += "\n" + line;
            }
        }

        if (block is not null) yield return block;
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
