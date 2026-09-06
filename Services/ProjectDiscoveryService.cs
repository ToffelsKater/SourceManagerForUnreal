using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using UnrealManager.Models;

namespace UnrealManager.Services;

/// <summary>
/// Finds the Unreal projects on this machine and works out which engine each one belongs to.
///
/// The reliable sources come first — the editor's own recent-projects list and the folder it
/// creates projects in — and a bounded sweep of the fixed drives catches everything else
/// (a Perforce workspace, a cloned repo). The sweep is depth- and time-limited on purpose:
/// projects live near the top of a drive, and this runs while the user is looking at the page.
/// </summary>
public static partial class ProjectDiscoveryService
{
    /// <summary>How deep below a scanned folder a .uproject is still looked for.</summary>
    private const int ScanDepth = 3;

    /// <summary>Ceilings for the drive sweep, so a huge or slow disk cannot hang the picker.</summary>
    private const int MaxDirectories = 20000;

    private static readonly TimeSpan TimeBudget = TimeSpan.FromSeconds(12);

    /// <summary>How much of a generated project file is searched for the engine it names.</summary>
    private const int MaxEvidenceChars = 2_000_000;

    /// <summary>Folders that never hold a project of the user's and are expensive to walk.</summary>
    private static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "windows", "program files", "program files (x86)", "programdata", "$recycle.bin",
        "system volume information", "windowsapps", "appdata", "recovery", "perflogs",
        "intermediate", "saved", "binaries", "build", "deriveddatacache", "content",
        "engine", "templates", "samples", "featurepacks", "node_modules",
        // Fab/marketplace downloads: every asset pack unpacks a .uproject nobody opens directly.
        "vaultcache",
        ".git", ".svn", ".vs", "obj", "bin", "packages", "epic games",
    };

    [GeneratedRegex("ProjectName\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex RecentProjectRegex();

    [GeneratedRegex("LastOpenTime\\s*=\\s*([\\d.\\-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex LastOpenTimeRegex();

    [GeneratedRegex("^\\s*CreatedProjectPaths\\s*=\\s*(.+?)\\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex CreatedProjectPathsRegex();

    /// <summary>
    /// Captures the engine root out of the paths UnrealBuildTool writes into generated project
    /// files: that engine's Build.bat, and the UnrealEditor.exe the debugger is pointed at.
    /// </summary>
    [GeneratedRegex("([A-Za-z]:[\\\\/][^\"'<>|\\r\\n]*?)[\\\\/]Engine[\\\\/]" +
        "(?:Build[\\\\/]BatchFiles[\\\\/]Build\\.bat|Binaries[\\\\/]Win64[\\\\/]UnrealEditor\\.exe)",
        RegexOptions.IgnoreCase)]
    private static partial Regex EnginePathRegex();

    /// <summary>The last scan's result, so a second page does not have to rescan the disk.</summary>
    public static IReadOnlyList<UnrealProject> Cached { get; private set; } = [];

    private static Task<IReadOnlyList<UnrealProject>>? _inFlight;

    /// <summary>
    /// The discovered projects, scanned off the UI thread on first use. Concurrent callers share
    /// one scan; <paramref name="force"/> starts a fresh one (the "Rescan" button).
    /// </summary>
    public static Task<IReadOnlyList<UnrealProject>> GetAsync(bool force = false)
    {
        if (!force && _inFlight is not null) return _inFlight;

        var scan = Task.Run<IReadOnlyList<UnrealProject>>(() =>
        {
            var projects = Discover();
            Cached = projects;
            return projects;
        });
        _inFlight = scan;
        return scan;
    }

    /// <summary>Recently opened first (most recent at the top), then everything else by name.</summary>
    public static List<UnrealProject> Discover()
    {
        var found = new Dictionary<string, UnrealProject>(StringComparer.OrdinalIgnoreCase);
        var scanRoots = new List<(string Folder, string Origin)>();

        AddFromEditorSettings(found, scanRoots);
        AddDefaultFolders(scanRoots);
        AddConfiguredProject(found, scanRoots);

        var budget = new Budget();
        foreach (var (folder, origin) in scanRoots)
            ScanFolder(found, folder, ScanDepth, origin, budget);

        ScanFixedDrives(found, budget);

        // Resolved here, with one engine scan for the whole list: the picker then names the engine
        // each project will actually be pointed at, not just the version string in its .uproject.
        var engines = EngineDiscoveryService.Discover();
        foreach (var project in found.Values) project.ResolvedEngine = MatchEngine(project, engines);

        return found.Values
            .OrderByDescending(p => p.LastOpened.HasValue)
            .ThenByDescending(p => p.LastOpened ?? DateTime.MinValue)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Reads a project's "EngineAssociation", or "" when the file cannot be read.</summary>
    public static string ReadEngineAssociation(string projectPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(projectPath), new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            return doc.RootElement.TryGetProperty("EngineAssociation", out var value)
                ? value.GetString()?.Trim() ?? ""
                : "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// The engine a project belongs to, matched the way UnrealVersionSelector matches it: a {GUID}
    /// names a registered source build, a version string names an installed engine, and an empty
    /// association means the engine is a folder the project sits inside. Returns null when nothing
    /// on this machine fits — the caller then keeps the engine it already had.
    /// </summary>
    public static EngineInstall? MatchEngine(UnrealProject project, IReadOnlyList<EngineInstall> engines)
    {
        var association = project.EngineAssociation.Trim();

        if (association.Length == 0) return MatchByFolder(project, engines);
        if (association.StartsWith('{')) return MatchByGuid(association, engines);
        return MatchByVersion(association, project, engines);
    }

    /// <summary>A project inside an engine tree ("foreign" project): the engine is a folder above it.</summary>
    private static EngineInstall? MatchByFolder(UnrealProject project, IReadOnlyList<EngineInstall> engines)
    {
        var folder = project.Folder;
        while (!string.IsNullOrEmpty(folder))
        {
            if (EngineService.IsEngineRoot(folder)) return Describe(folder, engines);
            folder = Path.GetDirectoryName(folder);
        }
        return null;
    }

    /// <summary>A source build registered under HKCU\Software\Epic Games\Unreal Engine\Builds.</summary>
    private static EngineInstall? MatchByGuid(string guid, IReadOnlyList<EngineInstall> engines)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Epic Games\Unreal Engine\Builds");
            var root = key?.GetValue(guid) as string;
            if (!string.IsNullOrWhiteSpace(root))
            {
                var normalized = EngineInstall.Normalize(root);
                if (EngineService.IsEngineRoot(normalized)) return Describe(normalized, engines);
            }
        }
        catch
        {
            // Key absent or unreadable — fall through to "no match".
        }
        return null;
    }

    /// <summary>
    /// "5.6" or "5.6.1": the engine has to be the same major.minor. That is often ambiguous — a
    /// source build and a launcher install of 5.8 both answer to "5.8" — so the project's own
    /// generated files and last build decide which of them it belongs to. Only when they say
    /// nothing does the launcher install win, as the conventional reading of a bare version.
    /// </summary>
    private static EngineInstall? MatchByVersion(
        string association, UnrealProject project, IReadOnlyList<EngineInstall> engines)
    {
        var wanted = ParseVersion(association);
        if (wanted is null) return null;

        var candidates = engines
            .Select(e => (Engine: e, Version: ParseVersion(e.Version)))
            .Where(x => x.Version is not null &&
                        x.Version.Major == wanted.Major &&
                        x.Version.Minor == wanted.Minor)
            .ToList();

        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0].Engine;

        // The engine that generated this project's Visual Studio files is the one it was last set
        // up against — the only evidence that tells two installs of the same version apart.
        var generated = ReadGeneratedEngineRoot(project.Folder);
        if (generated is not null)
        {
            var hit = candidates.FirstOrDefault(
                x => string.Equals(x.Engine.Root, generated, StringComparison.OrdinalIgnoreCase));
            if (hit.Engine is not null) return hit.Engine;
        }

        // Failing that, the exact version stamped into the last editor build ("5.8.2" vs "5.8.1").
        var built = ParseVersion(ReadBuiltEngineVersion(project.Folder) ?? "");
        if (built is not null)
        {
            var hit = candidates.FirstOrDefault(x => x.Version!.Equals(built));
            if (hit.Engine is not null) return hit.Engine;
        }

        var patchGiven = association.Count(c => c == '.') >= 2;
        return candidates
            .OrderByDescending(x => !patchGiven || x.Version!.Build == wanted.Build)
            .ThenByDescending(x => x.Engine.IsLauncher)
            .ThenByDescending(x => x.Version)
            .Select(x => x.Engine)
            .First();
    }

    /// <summary>
    /// The engine root the project's generated Visual Studio files point at — UnrealBuildTool writes
    /// that engine's Build.bat path into them. Null when project files were never generated, or when
    /// the engine they name is gone.
    /// </summary>
    public static string? ReadGeneratedEngineRoot(string projectFolder)
    {
        var projectFiles = Path.Combine(projectFolder, "Intermediate", "ProjectFiles");
        if (!Directory.Exists(projectFiles)) return null;

        try
        {
            // Cheapest and most reliable first: UECommon.props holds those paths in current engine
            // versions and the .user file names the editor the debugger launches. Older engines
            // write the same command lines straight into the .vcxproj, which can run to tens of MB.
            var files = Directory.EnumerateFiles(projectFiles, "*.props")
                .Concat(Directory.EnumerateFiles(projectFiles, "*.vcxproj.user"))
                .Concat(Directory.EnumerateFiles(projectFiles, "*.vcxproj"))
                .Take(20);

            foreach (var file in files)
            {
                var found = FindEnginePath(file);
                if (found is null) continue;

                var root = EngineInstall.Normalize(found);
                if (EngineService.IsEngineRoot(root)) return root;
            }
        }
        catch
        {
            // Unreadable intermediates say nothing about the engine — fall through.
        }
        return null;
    }

    /// <summary>
    /// The first engine path in a generated project file, read in chunks and given up on after
    /// <see cref="MaxEvidenceChars"/> — this runs once per project during a scan.
    /// </summary>
    private static string? FindEnginePath(string file)
    {
        try
        {
            using var reader = new StreamReader(file);
            var buffer = new char[64 * 1024];
            var carried = "";
            var read = 0;

            int count;
            while (read < MaxEvidenceChars && (count = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                read += count;
                var chunk = carried + new string(buffer, 0, count);

                var match = EnginePathRegex().Match(chunk);
                if (match.Success) return match.Groups[1].Value;

                // A path can straddle two chunks, so the tail is carried into the next one.
                carried = chunk[^Math.Min(512, chunk.Length)..];
            }
        }
        catch
        {
            // Locked or unreadable file — no evidence here.
        }
        return null;
    }

    /// <summary>
    /// "5.8.2" — the engine version this project's editor modules were last built against, read from
    /// the .target manifest UnrealBuildTool writes next to them. Null when it was never built here.
    /// </summary>
    public static string? ReadBuiltEngineVersion(string projectFolder)
    {
        var binaries = Path.Combine(projectFolder, "Binaries", "Win64");
        if (!Directory.Exists(binaries)) return null;

        try
        {
            foreach (var file in Directory.EnumerateFiles(binaries, "*.target").Take(10))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (!doc.RootElement.TryGetProperty("Version", out var version)) continue;
                if (!version.TryGetProperty("MajorVersion", out var major) ||
                    !version.TryGetProperty("MinorVersion", out var minor))
                    continue;

                var patch = version.TryGetProperty("PatchVersion", out var p) ? p.GetInt32() : 0;
                return $"{major.GetInt32()}.{minor.GetInt32()}.{patch}";
            }
        }
        catch
        {
            // A half-written or unreadable manifest is simply no evidence.
        }
        return null;
    }

    /// <summary>Parses "5.6" / "5.6.1" leniently; anything else (a branch name) is not a version.</summary>
    private static Version? ParseVersion(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var parts = text.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        if (!int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor)) return null;

        var patch = parts.Length > 2 && int.TryParse(parts[2], out var p) ? p : 0;
        return new Version(major, minor, patch);
    }

    /// <summary>The already-discovered install for this folder, or a fresh description of it.</summary>
    private static EngineInstall Describe(string root, IReadOnlyList<EngineInstall> engines)
    {
        var normalized = EngineInstall.Normalize(root);
        var known = engines.FirstOrDefault(
            e => string.Equals(e.Root, normalized, StringComparison.OrdinalIgnoreCase));

        return known ?? new EngineInstall
        {
            Root = normalized,
            Kind = EngineService.DetectKind(normalized),
            Version = EngineService.ReadVersion(normalized) ?? "",
            Origin = "Named by the project",
        };
    }

    /* ---------------- sources ---------------- */

    /// <summary>
    /// The editor's own settings, one file per engine version:
    /// %LOCALAPPDATA%\UnrealEngine\&lt;version&gt;\Saved\Config\WindowsEditor\EditorSettings.ini.
    /// It holds the recent-projects list and the folder new projects are created in.
    /// </summary>
    private static void AddFromEditorSettings(
        Dictionary<string, UnrealProject> found, List<(string Folder, string Origin)> scanRoots)
    {
        var unrealAppData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnrealEngine");
        if (!Directory.Exists(unrealAppData)) return;

        foreach (var versionFolder in SafeDirectories(unrealAppData))
        {
            var settings = Path.Combine(versionFolder, "Saved", "Config", "WindowsEditor", "EditorSettings.ini");
            if (!File.Exists(settings)) continue;

            string text;
            try { text = File.ReadAllText(settings); }
            catch { continue; }

            foreach (var line in text.Split('\n'))
            {
                var name = RecentProjectRegex().Match(line);
                if (!name.Success) continue;

                DateTime? opened = null;
                var stamp = LastOpenTimeRegex().Match(line);
                if (stamp.Success &&
                    DateTime.TryParseExact(stamp.Groups[1].Value, "yyyy.MM.dd-HH.mm.ss",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var parsed))
                    opened = parsed;

                Add(found, name.Groups[1].Value, "Recently opened", opened);
            }

            foreach (Match path in CreatedProjectPathsRegex().Matches(text))
                scanRoots.Add((path.Groups[1].Value.Trim(), "Unreal Projects folder"));
        }
    }

    /// <summary>The folder the launcher and the editor default to for new projects.</summary>
    private static void AddDefaultFolders(List<(string Folder, string Origin)> scanRoots)
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrEmpty(documents))
            scanRoots.Add((Path.Combine(documents, "Unreal Projects"), "Unreal Projects folder"));
    }

    /// <summary>
    /// The project already selected, plus the folder holding it — a Perforce workspace or a cloned
    /// repo is usually a folder full of projects, and it may sit outside every default location.
    /// </summary>
    private static void AddConfiguredProject(
        Dictionary<string, UnrealProject> found, List<(string Folder, string Origin)> scanRoots)
    {
        var configured = ConfigService.Config.ProjectPath;
        if (string.IsNullOrWhiteSpace(configured)) return;

        Add(found, configured, "Currently selected");

        var siblings = Path.GetDirectoryName(Path.GetDirectoryName(UnrealProject.Normalize(configured)));
        if (!string.IsNullOrEmpty(siblings)) scanRoots.Add((siblings, "Next to the current project"));
    }

    /// <summary>Sweeps the top of every fixed drive — bounded, so it stays a few seconds at worst.</summary>
    private static void ScanFixedDrives(Dictionary<string, UnrealProject> found, Budget budget)
    {
        foreach (var drive in SafeDrives())
            ScanFolder(found, drive, ScanDepth, $"Found on {drive.TrimEnd('\\')}", budget);
    }

    /* ---------------- scanning ---------------- */

    /// <summary>Keeps the sweep inside its directory count and wall-clock limits.</summary>
    private sealed class Budget
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private int _directories;

        public bool Exhausted => _directories >= MaxDirectories || _clock.Elapsed > TimeBudget;

        public void CountDirectory() => _directories++;
    }

    private static void ScanFolder(
        Dictionary<string, UnrealProject> found, string folder, int depth, string origin, Budget budget)
    {
        if (depth < 0 || budget.Exhausted) return;

        string[] projects;
        try
        {
            if (!Directory.Exists(folder)) return;
            budget.CountDirectory();
            projects = Directory.GetFiles(folder, "*.uproject");
        }
        catch
        {
            return; // Unreadable folder (permissions, offline drive) — not worth reporting.
        }

        if (projects.Length > 0)
        {
            foreach (var project in projects) Add(found, project, origin);
            return; // A project folder holds no other projects, and its Content tree is huge.
        }

        foreach (var child in SafeDirectories(folder))
        {
            if (IsSkipped(child)) continue;
            ScanFolder(found, child, depth - 1, origin, budget);
        }
    }

    /// <summary>Skips system, hidden and engine folders, and anything named in <see cref="SkipNames"/>.</summary>
    private static bool IsSkipped(string folder)
    {
        var name = Path.GetFileName(folder);
        if (name.Length == 0 || name.StartsWith('.')) return true;
        if (SkipNames.Contains(name)) return true;

        try
        {
            var attributes = File.GetAttributes(folder);
            // Junctions and symlinks lead back into folders already walked, and can loop.
            if (attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
            if (attributes.HasFlag(FileAttributes.System)) return true;
        }
        catch
        {
            return true;
        }

        // An engine tree ships template projects, which are not the user's projects.
        return EngineService.IsEngineRoot(folder);
    }

    private static void Add(
        Dictionary<string, UnrealProject> found, string? path, string origin, DateTime? lastOpened = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        var normalized = UnrealProject.Normalize(path);
        if (!normalized.EndsWith(".uproject", StringComparison.OrdinalIgnoreCase)) return;

        // Recent-project lists keep naming projects long after they were deleted or moved.
        if (!File.Exists(normalized)) return;

        // The first source to report a project wins, so the recent list keeps its LastOpenTime.
        if (found.ContainsKey(normalized)) return;

        found[normalized] = new UnrealProject
        {
            Path = normalized,
            EngineAssociation = ReadEngineAssociation(normalized),
            Origin = origin,
            LastOpened = lastOpened,
        };
    }

    private static IEnumerable<string> SafeDirectories(string folder)
    {
        try { return Directory.EnumerateDirectories(folder); }
        catch { return []; }
    }

    private static IEnumerable<string> SafeDrives()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .Select(d => d.RootDirectory.FullName)
                .ToList();
        }
        catch
        {
            return [];
        }
    }
}
