using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using UnrealManager.Core;
using UnrealManager.Services;

namespace UnrealManager.ViewModels;

public sealed class SyncLaunchViewModel : ProjectPageViewModel
{
    public override string Title => "Sync & Launch";
    public override string Icon => "🚀"; // rocket

    /// <summary>Page subtitle — there is no engine to rebuild on a precompiled launcher build.</summary>
    public string Intro => IsSourceEngine
        ? "One button: pull the latest from Perforce, rebuild changed engine code, and open the editor — " +
          "the classic UGS workflow."
        : "One button: pull the latest from Perforce, rebuild your project against the precompiled engine, " +
          "and open the editor.";

    /* ---- branch: one choice repoints the workspace, stream, project and launch arguments ---- */

    public ObservableCollection<string> Branches { get; } = [];

    public string ActiveBranch
    {
        get => ConfigService.ActiveBranch.Name;
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            ConfigService.ActivateBranch(value);
        }
    }

    /// <summary>One line naming the branch and where it syncs from — the header of the whole run.</summary>
    public string BranchSummary
    {
        get
        {
            var cfg = ConfigService.Config;
            var stream = string.IsNullOrWhiteSpace(cfg.P4Stream) ? "no stream set" : cfg.P4Stream;
            var client = string.IsNullOrWhiteSpace(cfg.P4Client) ? "no workspace set" : cfg.P4Client;
            var revision = PerforceService.NormalizeRevision(cfg.P4Changelist);
            return $"Branch '{ConfigService.ActiveBranch.Name}': {stream} via workspace {client}" +
                   (revision.Length == 0 ? "" : $", pinned to {revision}") +
                   " — edit these on the Perforce tab.";
        }
    }

    /// <summary>Step 4 can open the editor with no project at all, so the picker offers that too.</summary>
    protected override bool AllowNoProject => true;

    public bool StepSync
    {
        get => ConfigService.Config.StepSync;
        set { ConfigService.Config.StepSync = value; ConfigService.Save(); OnPropertyChanged(); }
    }

    public bool StepBuild
    {
        get => ConfigService.Config.StepBuild;
        set { ConfigService.Config.StepBuild = value; ConfigService.Save(); OnPropertyChanged(); }
    }

    public bool StepBuildProject
    {
        get => ConfigService.Config.StepBuildProject;
        set { ConfigService.Config.StepBuildProject = value; ConfigService.Save(); OnPropertyChanged(); }
    }

    public bool StepLaunch
    {
        get => ConfigService.Config.StepLaunch;
        set { ConfigService.Config.StepLaunch = value; ConfigService.Save(); OnPropertyChanged(); }
    }

    public string LaunchArgs
    {
        get => ConfigService.Config.LaunchArgs;
        set { ConfigService.Config.LaunchArgs = value; ConfigService.Save(); OnPropertyChanged(); }
    }

    private string _stepStatus = "";
    public string StepStatus { get => _stepStatus; private set => Set(ref _stepStatus, value); }

    /* ---- step labels: the engine build step disappears for a precompiled launcher engine,
            so the remaining steps have to renumber themselves. ---- */

    public string StepSyncLabel
    {
        get
        {
            var cfg = ConfigService.Config;
            var label = "1.  Sync latest from Perforce";
            if (!string.IsNullOrWhiteSpace(cfg.P4Stream))
                label += $" — switching the workspace to {cfg.P4Stream} first if it is on another stream";
            return label;
        }
    }

    public string StepBuildLabel => "2.  Build engine target";

    public string StepBuildProjectLabel =>
        (IsSourceEngine ? "3." : "2.") +
        "  Full project recompile — deletes Intermediate / Binaries / DDC / .vs, regenerates project files, " +
        "rebuilds (fixes 'Missing Modules')";

    public string StepLaunchLabel => (IsSourceEngine ? "4." : "3.") + "  Launch editor";

    /// <summary>The engine build step only ever runs against a source tree.</summary>
    private bool WillBuildEngine => StepBuild && IsSourceEngine;

    public ObservableCollection<CheckItem> SanityResults { get; } = [];

    public ICommand RunCommand { get; }
    public ICommand SanityCheckCommand { get; }
    public ICommand CancelCommand { get; }

    public SyncLaunchViewModel()
    {
        RunCommand = new AsyncRelayCommand(_ => RunAsync(),
            _ => !IsBusy && (StepSync || WillBuildEngine || StepBuildProject || StepLaunch));
        SanityCheckCommand = new AsyncRelayCommand(_ => SanityCheckAsync(), _ => !IsBusy);
        CancelCommand = new RelayCommand(_ => Cancel(), _ => IsBusy);

        RefreshBranches();
    }

    protected override void OnEngineChanged()
    {
        base.OnEngineChanged();
        OnPropertyChanged(nameof(Intro));
        OnPropertyChanged(nameof(StepBuildProjectLabel));
        OnPropertyChanged(nameof(StepLaunchLabel));
    }

    protected override void OnBranchChanged()
    {
        base.OnBranchChanged();
        RefreshBranches();
        // The previous branch's results describe a workspace this page no longer points at.
        SanityResults.Clear();
        StepStatus = "";
    }

    private void RefreshBranches()
    {
        Branches.Clear();
        foreach (var branch in ConfigService.BranchList) Branches.Add(branch.Name);
        OnPropertyChanged(nameof(ActiveBranch));
        OnPropertyChanged(nameof(BranchSummary));
        OnPropertyChanged(nameof(StepSyncLabel));
    }

    /// <summary>
    /// Checks every prerequisite of the enabled steps without changing anything,
    /// and lists what is missing along with where to fix it.
    /// </summary>
    private Task SanityCheckAsync() => RunBusyAsync("Sanity check", async ct =>
    {
        SanityResults.Clear();
        var cfg = ConfigService.Config;
        var issues = 0;

        void Add(string name, bool ok, string okDetail, string fixHint, bool blocking = true)
        {
            var status = ok ? CheckStatus.Ok : blocking ? CheckStatus.Missing : CheckStatus.OptionalMissing;
            SanityResults.Add(new CheckItem { Name = name, StatusValue = status, Detail = ok ? okDetail : fixHint });
            if (!ok && blocking) issues++;
            Log((ok ? "  [ok]      " : blocking ? "  [MISSING] " : "  [warn]    ") + name + (ok ? "" : " — " + fixHint));
        }

        // Engine tree — needed by build and launch steps.
        var engineOk = EngineService.IsEngineRoot(cfg.EngineRoot);
        Add("Engine folder", engineOk,
            $"{cfg.EngineRoot} ({(IsLauncherEngine ? "Epic Games Launcher build" : "source build")})",
            "Pick an installed engine or clone the source on the 'Get Source' tab");

        if (engineOk && IsSourceEngine)
        {
            // Marker file left by GitDependencies: ".uedependencies" since UE 5.8, ".ue4dependencies" before.
            Add("Engine dependencies (Setup.bat)",
                File.Exists(Path.Combine(cfg.EngineRoot, ".uedependencies")) ||
                File.Exists(Path.Combine(cfg.EngineRoot, ".ue4dependencies")),
                "Setup.bat has been run",
                "Run '2. Setup.bat' on the 'Get Source' tab");
        }

        if (StepSync)
        {
            var p4Path = await ProcessRunner.WhereAsync("p4");
            Add("Perforce CLI (p4)", p4Path is not null, p4Path ?? "",
                "Install it (winget install Perforce.P4V) — see the Dependencies tab");

            var connOk = !string.IsNullOrWhiteSpace(cfg.P4Port) &&
                         !string.IsNullOrWhiteSpace(cfg.P4User) &&
                         !string.IsNullOrWhiteSpace(cfg.P4Client);
            Add("Perforce connection settings", connOk,
                $"{cfg.P4User}@{cfg.P4Port} / {cfg.P4Client}",
                "Fill in server, user and workspace on the 'Perforce' tab");

            if (p4Path is not null && connOk)
            {
                var conn = new P4Connection { Port = cfg.P4Port, User = cfg.P4User, Client = cfg.P4Client };
                var login = await PerforceService.LoginStatusAsync(conn, ct);
                Add("Perforce login ticket", login.Success,
                    "logged in",
                    "Log in on the 'Perforce' tab (ticket missing or expired)");

                if (login.Success)
                {
                    var clients = await PerforceService.ListClientsAsync(conn, ct);
                    var clientExists = clients.Contains(cfg.P4Client);
                    Add("Perforce workspace exists", clientExists,
                        cfg.P4Client,
                        $"Workspace '{cfg.P4Client}' not found for user {cfg.P4User} — pick one on the 'Perforce' tab");

                    // Syncing one branch and launching another branch's .uproject is the classic
                    // way to lose an afternoon, so the branch's stream is checked against reality.
                    if (clientExists && !string.IsNullOrWhiteSpace(cfg.P4Stream))
                    {
                        var current = await PerforceService.GetClientStreamAsync(conn, cfg.P4Client, ct);
                        if (current is null)
                        {
                            Add($"Workspace is on {cfg.P4Stream}", false, "",
                                $"'{cfg.P4Client}' is not a stream workspace — use a stream workspace for this " +
                                "branch, or clear the stream on the 'Perforce' tab");
                        }
                        else
                        {
                            var onStream = string.Equals(current, cfg.P4Stream, StringComparison.OrdinalIgnoreCase);
                            Add($"Workspace is on {cfg.P4Stream}", onStream,
                                current,
                                $"currently on {current} — the sync step will switch it", blocking: false);
                        }
                    }
                }
            }
        }

        if (WillBuildEngine || StepBuildProject)
        {
            var vsInstances = await VisualStudioService.GetInstancesAsync();
            Add("Visual Studio 2022", vsInstances.Count > 0,
                vsInstances.FirstOrDefault()?.DisplayName ?? "",
                "Install it from the 'Dependencies' tab");

            if (vsInstances.Count > 0)
            {
                var missing = new List<string>();
                foreach (var req in VisualStudioService.Requirements.Where(r => r.Required))
                {
                    var found = false;
                    foreach (var id in req.AnyOfIds)
                    {
                        if (await VisualStudioService.AnyInstanceHasComponentAsync(id)) { found = true; break; }
                    }
                    if (!found) missing.Add(req.Name);
                }
                Add("Visual Studio C++ components", missing.Count == 0,
                    "all required workloads installed",
                    $"Missing: {string.Join("; ", missing)} — fix on the 'Dependencies' tab");
            }
        }

        if (StepBuildProject)
        {
            Add("Project file (.uproject)",
                !string.IsNullOrWhiteSpace(ProjectPath) && File.Exists(ProjectPath),
                ProjectPath,
                "Pick your project above (or untick step 3)");
        }

        if (StepLaunch && engineOk)
        {
            if (!WillBuildEngine)
            {
                Add("UnrealEditor.exe present", EngineService.IsEditorBuilt(cfg.EngineRoot),
                    "found",
                    IsLauncherEngine
                        ? "Missing from this launcher install — verify or reinstall it in the Epic Games Launcher"
                        : "Build UnrealEditor on the 'Build' tab, or enable the engine build step");
            }
            Add("ShaderCompileWorker.exe", File.Exists(EngineService.ShaderCompileWorkerExe(cfg.EngineRoot)),
                "found",
                IsSourceEngine
                    ? "will be built automatically before launch"
                    : "missing from this launcher install — verify it in the Epic Games Launcher",
                blocking: false);
        }

        if ((WillBuildEngine || StepBuildProject) && engineOk)
        {
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(cfg.EngineRoot))!);
                var freeGb = drive.AvailableFreeSpace / (1024.0 * 1024 * 1024);
                Add("Disk space for building", freeGb >= 50,
                    $"{freeGb:F0} GB free on {drive.Name}",
                    $"only {freeGb:F0} GB free on {drive.Name} — builds can need 50+ GB", blocking: false);
            }
            catch { /* unreadable drive info is not itself a blocker */ }
        }

        StepStatus = issues == 0
            ? "Sanity check passed — ready to go."
            : $"Sanity check found {issues} blocking issue(s) — see the list below.";
    });

    private Task RunAsync() => RunBusyAsync("Sync & Launch", async ct =>
    {
        var cfg = ConfigService.Config;

        if (StepSync)
        {
            StepStatus = "Step: Perforce sync…";
            var conn = new P4Connection { Port = cfg.P4Port, User = cfg.P4User, Client = cfg.P4Client };
            if (string.IsNullOrWhiteSpace(conn.Client))
            {
                StepStatus = "Perforce workspace not configured — set it on the Perforce tab.";
                return;
            }

            // Put the workspace on this branch's stream before syncing, so the sync (and everything
            // built and launched after it) can only ever come from the branch that was selected.
            if (!string.IsNullOrWhiteSpace(cfg.P4Stream))
            {
                StepStatus = $"Step: switching workspace to {cfg.P4Stream}…";
                var current = await PerforceService.GetClientStreamAsync(conn, conn.Client, ct);
                if (current is null)
                {
                    StepStatus = $"Branch '{ConfigService.ActiveBranch.Name}' expects stream {cfg.P4Stream}, but " +
                                 $"'{conn.Client}' is not a stream workspace — aborting rather than syncing the " +
                                 "wrong branch. Use a stream workspace, or clear the stream on the Perforce tab.";
                    return;
                }

                if (string.Equals(current, cfg.P4Stream, StringComparison.OrdinalIgnoreCase))
                {
                    Log($"Workspace '{conn.Client}' is already on {cfg.P4Stream}.");
                }
                else
                {
                    Log($"Switching workspace '{conn.Client}' from {current} to {cfg.P4Stream}…");
                    var switched = await PerforceService.SwitchStreamAsync(conn, cfg.P4Stream, Log, ct);
                    if (!switched.Success)
                    {
                        StepStatus = $"Stream switch failed (exit code {switched.ExitCode}) — aborting. " +
                                     "Files left open for edit in the workspace will block it.";
                        return;
                    }
                    Log($"Workspace switched to {cfg.P4Stream}.");
                }
            }

            var revision = PerforceService.NormalizeRevision(cfg.P4Changelist);
            StepStatus = revision.Length == 0 ? "Step: Perforce sync…" : $"Step: Perforce sync to {revision}…";
            var sync = await PerforceService.SyncAsync(
                conn, cfg.P4SyncPath, cfg.P4Changelist, cfg.P4ForceSync, cfg.P4ParallelSync, Log, ct);
            if (!sync.Success && !sync.StdErr.Contains("up-to-date"))
            {
                StepStatus = $"Sync failed (exit code {sync.ExitCode}) — aborting.";
                return;
            }
            Log("Sync done.");
        }

        if (StepBuild && IsLauncherEngine)
        {
            Log("Precompiled Epic Games Launcher engine — nothing to compile; skipping the engine build step.");
        }
        else if (StepBuild)
        {
            StepStatus = $"Step: building {cfg.BuildTarget} {cfg.BuildConfiguration}…";
            if (!EngineService.IsSourceBuild(cfg.EngineRoot))
            {
                StepStatus = "Engine source not valid — set it on the Get Source tab.";
                return;
            }
            var build = await EngineService.BuildAsync(
                cfg.EngineRoot, cfg.BuildTarget, cfg.BuildPlatform, cfg.BuildConfiguration, Log, ct);
            if (!build.Success)
            {
                StepStatus = $"Build failed (exit code {build.ExitCode}) — aborting.";
                return;
            }
            if (cfg.BuildTarget == "UnrealEditor")
            {
                StepStatus = "Step: building ShaderCompileWorker (required by the editor)…";
                var scw = await EngineService.BuildShaderCompileWorkerAsync(cfg.EngineRoot, Log, ct);
                if (!scw.Success)
                {
                    StepStatus = $"ShaderCompileWorker build failed (exit code {scw.ExitCode}) — aborting.";
                    return;
                }
            }
            Log("Build done.");
        }

        if (StepBuildProject)
        {
            if (string.IsNullOrWhiteSpace(ProjectPath))
            {
                Log("No project set — skipping full project recompile.");
            }
            else if (!File.Exists(ProjectPath))
            {
                StepStatus = "Project file not found: " + ProjectPath;
                return;
            }
            else
            {
                if (!EngineService.IsEngineRoot(cfg.EngineRoot))
                {
                    StepStatus = "Engine root not valid — set it on the Get Source tab.";
                    return;
                }

                // Closing Visual Studio can destroy unsaved work, so never do it silently.
                var vsRunning = ProjectRebuildService.GetRunning(ProjectRebuildService.VisualStudioProcesses);
                var closeVs = false;
                if (vsRunning.Length > 0)
                {
                    var answer = MessageBox.Show(
                        $"A full recompile deletes this project's Visual Studio files, but {string.Join(", ", vsRunning)} " +
                        "is running and holds them locked.\n\nClose it now? Any unsaved changes will be lost.\n\n" +
                        "Cancel stops the recompile so you can save your work first.",
                        "Full project recompile", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                    if (answer != MessageBoxResult.OK)
                    {
                        StepStatus = "Cancelled — close Visual Studio yourself, then run again.";
                        return;
                    }
                    closeVs = true;
                }

                LogHeader("Full project recompile");
                var error = await ProjectRebuildService.FullRecompileAsync(
                    cfg.EngineRoot, ProjectPath, cfg.BuildPlatform, cfg.BuildConfiguration,
                    closeVs, Log, s => StepStatus = s, ct);
                if (error is not null)
                {
                    StepStatus = "Full recompile failed — aborting. " + error;
                    return;
                }
                Log("Project fully recompiled.");
            }
        }

        if (StepLaunch)
        {
            if (!string.IsNullOrWhiteSpace(ProjectPath) && !File.Exists(ProjectPath))
            {
                StepStatus = "Project file not found: " + ProjectPath;
                return;
            }

            // Safety net: the editor refuses to start without ShaderCompileWorker. A launcher build
            // ships it prebuilt, so a missing one there means a broken install, not a missing build.
            if (!File.Exists(EngineService.ShaderCompileWorkerExe(cfg.EngineRoot)))
            {
                if (IsLauncherEngine)
                {
                    StepStatus = "ShaderCompileWorker.exe missing from this launcher engine — " +
                                 "verify the installation in the Epic Games Launcher.";
                    return;
                }

                StepStatus = "ShaderCompileWorker missing — building it first…";
                Log("ShaderCompileWorker.exe not found; building it (the editor cannot start without it).");
                var scw = await EngineService.BuildShaderCompileWorkerAsync(cfg.EngineRoot, Log, ct);
                if (!scw.Success)
                {
                    StepStatus = $"ShaderCompileWorker build failed (exit code {scw.ExitCode}) — aborting launch.";
                    return;
                }
            }

            StepStatus = "Step: launching UnrealEditor…";
            EngineService.LaunchEditor(cfg.EngineRoot, ProjectPath, LaunchArgs);
            Log("UnrealEditor launched.");
        }

        StepStatus = "All steps completed.";
    });
}
