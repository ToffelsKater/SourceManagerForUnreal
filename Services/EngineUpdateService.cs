using System.Diagnostics;
using System.IO;

namespace UnrealManager.Services;

/// <summary>One "X.Y.Z-release" tag on the remote.</summary>
public sealed record ReleaseTag(Version Version, string Sha);

/// <summary>What the GitHub tag lookup said about one engine install.</summary>
public sealed class EngineUpdateStatus
{
    /// <summary>"5.8.0" as read from Engine/Build/Build.version, or "" when it could not be read.</summary>
    public required string LocalVersion { get; init; }

    /// <summary>
    /// The branch a source clone follows ("5.8", "release"), when it is not sitting on a release tag.
    /// Build.version on a branch tip does not track hotfix numbers, so the comparison is a hint there
    /// rather than a fact, and the message has to say so.
    /// </summary>
    public string? Branch { get; init; }

    /// <summary>Newest hotfix in the same X.Y line, e.g. "5.8.2" for a local 5.8.0. Null when already newest.</summary>
    public string? NewerPatch { get; init; }

    /// <summary>Newest release overall when it is a later X.Y, e.g. "5.9.0". Informational — a minor bump is a migration.</summary>
    public string? NewerMinor { get; init; }

    /// <summary>False when the lookup itself failed (no GitHub access, no git, offline).</summary>
    public required bool Ok { get; init; }

    public required string Message { get; init; }

    public bool HasUpdate => NewerPatch is not null;

    /// <summary>Colour for the message line — amber when an update is waiting, grey when the check failed.</summary>
    public string Color => !Ok ? "#9AA0AE" : HasUpdate ? "#E2B341" : "#4CC38A";

    public static EngineUpdateStatus Unknown(string message) =>
        new() { LocalVersion = "", Ok = false, Message = message };
}

/// <summary>
/// Compares an engine install against the release tags in Epic's GitHub repository.
/// Epic tags every shipped build "X.Y.Z-release", so one `git ls-remote --tags` — no clone, no API
/// token, the same linked-account credentials the rest of the GitHub workflow already needs — is
/// enough to tell a local 5.8.0 that 5.8.2 exists.
///
/// The comparison is meaningful for precompiled launcher builds too (Epic's hotfix numbering matches
/// the tags), but only a source tree can be updated here; launcher engines are updated by the launcher.
/// </summary>
public static class EngineUpdateService
{
    private const string ReleaseSuffix = "-release";

    /// <summary>
    /// Never let a credential prompt block the check: it runs unattended at startup, so a missing
    /// GitHub link has to surface as a failed check rather than a hung process or a stray dialog.
    /// </summary>
    private static readonly Dictionary<string, string> NonInteractiveGit = new()
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GCM_INTERACTIVE"] = "never",
    };

    /// <summary>Tag lists are cached per remote: the answer changes a few times a year, not per click.</summary>
    private static readonly Dictionary<string, (DateTime FetchedUtc, List<ReleaseTag> Tags)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(6);

    /// <summary>Every "X.Y.Z-release" tag on the remote, oldest first. Empty when GitHub could not be reached.</summary>
    public static async Task<List<ReleaseTag>> ListReleaseTagsAsync(
        string remoteUrl, bool force, CancellationToken ct)
    {
        lock (Cache)
        {
            if (!force && Cache.TryGetValue(remoteUrl, out var hit) &&
                DateTime.UtcNow - hit.FetchedUtc < CacheLifetime)
                return hit.Tags;
        }

        // --refs drops the "^{}" peeled duplicates; the glob keeps the reply to the release tags only.
        var args = $"-c credential.interactive=never ls-remote --tags --refs \"{remoteUrl}\" \"*{ReleaseSuffix}\"";
        var result = await ProcessRunner.RunAsync("git", args, ct: ct, environment: NonInteractiveGit);
        if (!result.Success) return [];

        var tags = result.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('\t'))
            .Where(parts => parts.Length >= 2)
            .Select(parts => (Sha: parts[0].Trim(), Ref: parts[^1].Trim()))
            .Where(t => t.Ref.StartsWith("refs/tags/") && t.Ref.EndsWith(ReleaseSuffix))
            .Select(t => (t.Sha, Version: ParseRelease(t.Ref["refs/tags/".Length..^ReleaseSuffix.Length])))
            .Where(t => t.Version is not null)
            .Select(t => new ReleaseTag(t.Version!, t.Sha))
            .DistinctBy(t => t.Version)
            .OrderBy(t => t.Version)
            .ToList();

        lock (Cache) Cache[remoteUrl] = (DateTime.UtcNow, tags);
        return tags;
    }

    /// <summary>"5.8.2" -> 5.8.2. Anything without exactly three numeric parts (previews, EA tags) is rejected.</summary>
    private static Version? ParseRelease(string tag) =>
        Version.TryParse(tag, out var v) && v.Build >= 0 && v.Revision < 0 ? v : null;

    /// <summary>Compares the engine at <paramref name="root"/> against the remote's release tags.</summary>
    public static async Task<EngineUpdateStatus> CheckAsync(
        string root, string remoteUrl, bool force, CancellationToken ct)
    {
        if (!EngineService.IsEngineRoot(root))
            return EngineUpdateStatus.Unknown("No engine selected — pick one above first.");

        var localText = EngineService.ReadVersion(root);
        if (localText is null || !Version.TryParse(localText, out var local))
            return EngineUpdateStatus.Unknown(
                "This engine's version could not be read (Engine/Build/Build.version is missing or unreadable).");

        var tags = await ListReleaseTagsAsync(remoteUrl, force, ct);
        if (tags.Count == 0)
            return new EngineUpdateStatus
            {
                LocalVersion = localText,
                Ok = false,
                Message = $"Installed version {localText}. Could not read the release tags from GitHub — " +
                          "use 'Test GitHub access' below to check your linked account.",
            };

        // A clone checked out at a release tag states its version exactly; one following a branch does
        // not — Epic leaves PatchVersion at 0 on a hotfix branch, so a 5.8 tip still reports 5.8.0.
        var (branch, headTag) = await DescribeCheckoutAsync(root, tags, ct);
        if (headTag is not null)
        {
            local = headTag.Version;
            localText = local.ToString(3);
        }

        var newerPatch = tags
            .Where(t => t.Version.Major == local.Major && t.Version.Minor == local.Minor && t.Version > local)
            .Select(t => t.Version)
            .LastOrDefault();

        var newest = tags[^1].Version;
        var newerMinor = newest.Major > local.Major || (newest.Major == local.Major && newest.Minor > local.Minor)
            ? newest
            : null;

        string message;
        if (newerPatch is null)
            message = $"Up to date: {localText} is the latest {local.Major}.{local.Minor} release.";
        else if (branch is not null)
            message = $"{newerPatch.ToString(3)} is the latest {local.Major}.{local.Minor} release. " +
                      $"This tree follows branch '{branch}', which reports {localText} — a branch tip does not " +
                      $"carry the hotfix number, so it may already contain those fixes. Updating checks out the " +
                      $"{newerPatch.ToString(3)} release tag, which pins it exactly.";
        else
            message = $"Update available: {newerPatch.ToString(3)} — you have {localText}.";

        if (newerMinor is not null)
            message += $" A newer engine version, {newerMinor.ToString(3)}, has also shipped — " +
                       "moving to it is a project migration, not an update.";

        return new EngineUpdateStatus
        {
            LocalVersion = localText,
            Branch = newerPatch is null ? null : branch,
            NewerPatch = newerPatch?.ToString(3),
            NewerMinor = newerMinor?.ToString(3),
            Ok = true,
            Message = message,
        };
    }

    /// <summary>
    /// What a source clone is actually sitting on: the release tag when HEAD is exactly one (the sha
    /// comparison works in a shallow clone, where `git describe` has no history to search), otherwise
    /// the branch name. Both null for a launcher build or a tree without git.
    /// </summary>
    private static async Task<(string? Branch, ReleaseTag? Tag)> DescribeCheckoutAsync(
        string root, List<ReleaseTag> tags, CancellationToken ct)
    {
        if (!IsGitWorkTree(root)) return (null, null);

        try
        {
            var head = await ProcessRunner.RunAsync("git", $"-C \"{root}\" rev-parse HEAD", ct: ct);
            if (head.Success)
            {
                var sha = head.StdOut.Trim();
                var match = tags.FirstOrDefault(t => t.Sha == sha);
                if (match is not null) return (null, match);
            }

            var name = await ProcessRunner.RunAsync("git", $"-C \"{root}\" rev-parse --abbrev-ref HEAD", ct: ct);
            var branch = name.Success ? name.StdOut.Trim() : "";

            // "HEAD" means detached at something that is not a release tag — no branch to name.
            return (branch is "" or "HEAD" ? null : branch, null);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>
    /// True when the engine folder is a git clone we can move to another tag. A source tree unpacked
    /// from a zip has the source but no history, so it can only be replaced, not updated.
    /// </summary>
    public static bool IsGitWorkTree(string root) =>
        !string.IsNullOrWhiteSpace(root) &&
        (Directory.Exists(Path.Combine(root, ".git")) || File.Exists(Path.Combine(root, ".git")));

    /// <summary>
    /// Tracked files the user changed under the engine folder — the reason not to touch the tree.
    /// Untracked files are ignored: Setup.bat drops tens of thousands of downloaded binaries in here.
    /// </summary>
    public static async Task<List<string>> ListLocalChangesAsync(string root, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(
            "git", $"-C \"{root}\" status --porcelain --untracked-files=no", ct: ct);
        if (!result.Success) return [];

        return result.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd())
            .Where(l => l.Length > 0)
            .ToList();
    }

    /// <summary>
    /// Moves a source tree onto the "X.Y.Z-release" tag. Shallow clones fetch that single tag at depth 1
    /// (the app clones shallow by default), full clones fetch it normally; either way the checkout leaves
    /// a detached HEAD, which is what an engine build tree wants.
    /// Setup.bat and GenerateProjectFiles.bat must run afterwards — the caller sequences those.
    /// </summary>
    public static async Task<ProcessResult> CheckoutReleaseAsync(
        string root, string version, Action<string> onOutput, CancellationToken ct)
    {
        var tag = version + ReleaseSuffix;

        var shallowProbe = await ProcessRunner.RunAsync(
            "git", $"-C \"{root}\" rev-parse --is-shallow-repository", ct: ct);
        var isShallow = shallowProbe.Success && shallowProbe.StdOut.Trim() == "true";
        var depth = isShallow ? "--depth 1 " : "";

        onOutput($"Fetching tag {tag}{(isShallow ? " (shallow clone - fetching that tag only)" : "")}...");
        var fetch = await ProcessRunner.RunAsync(
            "git", $"-C \"{root}\" fetch {depth}--progress origin tag {tag}", onOutput: onOutput, ct: ct);
        if (!fetch.Success) return fetch;

        onOutput($"Checking out {tag}...");
        return await ProcessRunner.RunAsync(
            "git", $"-C \"{root}\" checkout --force {tag}", onOutput: onOutput, ct: ct);
    }

    /// <summary>Opens the Epic Games Launcher on its library — the only place a precompiled engine updates.</summary>
    public static void OpenEpicLauncher()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "com.epicgames.launcher://ue/library",
            UseShellExecute = true,
        });
    }
}
