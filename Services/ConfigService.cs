using System.IO;
using System.Text.Json;
using UnrealManager.Models;

namespace UnrealManager.Services;

public static class ConfigService
{
    private static readonly string ConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UnrealManager");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppConfig Config { get; private set; } = Load();

    /// <summary>
    /// Raised after the selected engine changes. Every page listens, because which actions make
    /// sense depends on the kind of engine: a precompiled Epic Games Launcher build has no engine
    /// source to clone, set up or compile.
    /// </summary>
    public static event Action? EngineChanged;

    /// <summary>
    /// Raised after the active branch changes. The Perforce settings, project and launch arguments
    /// all belong to a branch, so every page bound to them has to re-read its values.
    /// </summary>
    public static event Action? BranchChanged;

    /// <summary>
    /// Raised after the selected project changes. The project is one app-wide setting shared by the
    /// Build and Sync &amp; Launch tabs, so both have to follow a change made on the other.
    /// </summary>
    public static event Action? ProjectChanged;

    /// <summary>The branch whose settings are currently live in <see cref="Config"/>.</summary>
    public static BranchProfile ActiveBranch => FindBranch(Config.ActiveBranch) ?? EnsureBranches();

    /// <summary>Every branch, with the default one created on first use.</summary>
    public static IReadOnlyList<BranchProfile> BranchList
    {
        get
        {
            EnsureBranches();
            return Config.Branches;
        }
    }

    private static BranchProfile? FindBranch(string name) =>
        Config.Branches.FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Guarantees there is always exactly one active branch to write live edits back into. On the
    /// first run after upgrading, the settings already in the config become the "Default" branch,
    /// so nothing is lost and nothing has to be re-entered.
    /// </summary>
    private static BranchProfile EnsureBranches()
    {
        if (Config.Branches.Count == 0)
            Config.Branches.Add(new BranchProfile { Name = "Default" });

        var active = FindBranch(Config.ActiveBranch) ?? Config.Branches[0];
        Config.ActiveBranch = active.Name;
        return active;
    }

    /// <summary>Adds a branch seeded from the current settings and makes it active.</summary>
    public static BranchProfile AddBranch(string name)
    {
        var unique = name.Trim();
        if (unique.Length == 0) unique = "New branch";
        for (var n = 2; FindBranch(unique) is not null; n++) unique = $"{name.Trim()} ({n})";

        CaptureLiveIntoActiveBranch();
        var branch = new BranchProfile
        {
            Name = unique,
            Stream = Config.P4Stream,
            Client = Config.P4Client,
            SyncPath = Config.P4SyncPath,
            Changelist = Config.P4Changelist,
            ProjectPath = Config.ProjectPath,
            EngineRoot = Config.EngineRoot,
            LaunchArgs = Config.LaunchArgs,
        };
        Config.Branches.Add(branch);
        Config.ActiveBranch = branch.Name;
        Save();
        BranchChanged?.Invoke();
        return branch;
    }

    /// <summary>Deletes a branch. The last one is kept — there always has to be somewhere to save edits.</summary>
    public static void RemoveBranch(string name)
    {
        if (Config.Branches.Count <= 1) return;
        var branch = FindBranch(name);
        if (branch is null) return;

        Config.Branches.Remove(branch);
        if (string.Equals(Config.ActiveBranch, branch.Name, StringComparison.OrdinalIgnoreCase))
        {
            Config.ActiveBranch = Config.Branches[0].Name;
            ApplyBranch(Config.Branches[0]);
        }
        Save();
        BranchChanged?.Invoke();
    }

    /// <summary>Renames the active branch, keeping it active.</summary>
    public static void RenameActiveBranch(string newName)
    {
        var trimmed = newName.Trim();
        if (trimmed.Length == 0) return;
        var active = ActiveBranch;
        if (string.Equals(active.Name, trimmed, StringComparison.Ordinal)) return;
        if (FindBranch(trimmed) is not null) return;

        active.Name = trimmed;
        Config.ActiveBranch = trimmed;
        Save();
        BranchChanged?.Invoke();
    }

    /// <summary>
    /// Makes another branch live: the current edits are written back first, then the whole pipeline
    /// (workspace, stream, project, engine, launch arguments) is repointed in one go.
    /// </summary>
    public static void ActivateBranch(string name)
    {
        var branch = FindBranch(name);
        if (branch is null || string.Equals(branch.Name, Config.ActiveBranch, StringComparison.Ordinal)) return;

        CaptureLiveIntoActiveBranch();
        Config.ActiveBranch = branch.Name;
        ApplyBranch(branch);
        Save();
        BranchChanged?.Invoke();
    }

    /// <summary>Copies a branch's settings into the live config, switching the engine with it.</summary>
    private static void ApplyBranch(BranchProfile branch)
    {
        Config.P4Stream = branch.Stream;
        Config.P4Client = branch.Client;
        Config.P4SyncPath = branch.SyncPath;
        Config.P4Changelist = branch.Changelist;
        Config.ProjectPath = branch.ProjectPath;
        Config.LaunchArgs = branch.LaunchArgs;

        // An empty engine root means the branch has no preference — keep the one already selected.
        if (!string.IsNullOrWhiteSpace(branch.EngineRoot)) SetEngineRoot(branch.EngineRoot);
    }

    /// <summary>
    /// Mirrors the live settings back into the active branch. Called from <see cref="Save"/> so that
    /// editing any field on the Perforce or Sync &amp; Launch page updates the branch it belongs to,
    /// rather than being silently dropped at the next branch switch.
    /// </summary>
    private static void CaptureLiveIntoActiveBranch()
    {
        var branch = ActiveBranch;
        branch.Stream = Config.P4Stream;
        branch.Client = Config.P4Client;
        branch.SyncPath = Config.P4SyncPath;
        branch.Changelist = Config.P4Changelist;
        branch.ProjectPath = Config.ProjectPath;
        branch.LaunchArgs = Config.LaunchArgs;
        branch.EngineRoot = Config.EngineRoot;
    }

    /// <summary>Points the whole app at another .uproject and tells every page bound to it.</summary>
    public static void SetProjectPath(string path)
    {
        if (string.Equals(Config.ProjectPath, path, StringComparison.Ordinal)) return;
        Config.ProjectPath = path;
        Save();
        ProjectChanged?.Invoke();
    }

    /// <summary>Points the whole app at another engine folder and tells every page to re-evaluate.</summary>
    public static void SetEngineRoot(string root)
    {
        if (string.Equals(Config.EngineRoot, root, StringComparison.Ordinal)) return;
        Config.EngineRoot = root;
        Save();
        EngineChanged?.Invoke();
    }

    /// <summary>
    /// Re-evaluates the engine without changing the path — for when the folder's contents changed
    /// under us (a clone finished, Setup.bat ran, an engine was uninstalled).
    /// </summary>
    public static void NotifyEngineChanged() => EngineChanged?.Invoke();

    private static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath)) ?? new AppConfig();
        }
        catch
        {
            // Corrupt config: fall back to defaults.
        }
        return new AppConfig();
    }

    public static void Save()
    {
        try
        {
            CaptureLiveIntoActiveBranch();
            Directory.CreateDirectory(ConfigDir);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(Config, JsonOptions));
        }
        catch (Exception ex)
        {
            LogService.Instance.Log("WARNING: could not save config: " + ex.Message);
        }
    }
}
