using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using UnrealManager.Core;
using UnrealManager.Models;
using UnrealManager.Services;

namespace UnrealManager.ViewModels;

public sealed class SourceViewModel : PageViewModel
{
    public override string Title => "Get Source";
    public override string Icon => "⬇"; // download arrow

    public string RemoteUrl
    {
        get => ConfigService.Config.GitRemoteUrl;
        set { ConfigService.Config.GitRemoteUrl = value; ConfigService.Save(); OnPropertyChanged(); }
    }

    public string Branch
    {
        get => ConfigService.Config.GitBranch;
        set { ConfigService.Config.GitBranch = value; ConfigService.Save(); OnPropertyChanged(); }
    }

    public bool ShallowClone
    {
        get => ConfigService.Config.ShallowClone;
        set { ConfigService.Config.ShallowClone = value; ConfigService.Save(); OnPropertyChanged(); }
    }

    public string EngineRoot
    {
        get => ConfigService.Config.EngineRoot;
        set
        {
            ConfigService.SetEngineRoot(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(DiskInfo));
            OnPropertyChanged(nameof(EngineRootState));
            SyncSelectedEngine();
        }
    }

    public string DiskInfo => string.IsNullOrWhiteSpace(EngineRoot)
        ? ""
        : EngineService.GetFreeSpaceInfo(EngineRoot, includeSourceBuildHint: !IsLauncherEngine);

    public string EngineRootState =>
        EngineService.IsLauncherBuild(EngineRoot)
            ? "✔ Epic Games Launcher engine (precompiled) — nothing to clone or compile here"
        : EngineService.IsSourceBuild(EngineRoot) ? "✔ Engine source found at this location"
        : Directory.Exists(EngineRoot) && Directory.EnumerateFileSystemEntries(EngineRoot).Any()
            ? "⚠ Folder exists and is not empty (clone needs an empty/new folder)"
            : "Folder is empty or will be created by the clone";

    /// <summary>Cloning, Setup.bat and GenerateProjectFiles.bat exist only for source engines.</summary>
    public bool ShowSourceWorkflow => !IsLauncherEngine;

    public ObservableCollection<string> Branches { get; } =
        ["release", "5.6", "5.5", "5.4", "ue5-main"];

    /* ---------------- auto-discovered engines ---------------- */

    /// <summary>Every engine found on this PC — Epic Games Launcher installs and source trees alike.</summary>
    public ObservableCollection<EngineInstall> DetectedEngines { get; } = [];

    private EngineInstall? _selectedEngine;
    public EngineInstall? SelectedEngine
    {
        get => _selectedEngine;
        set
        {
            if (!Set(ref _selectedEngine, value)) return;
            if (value is not null) EngineRoot = value.Root;
        }
    }

    public bool HasDetectedEngines => DetectedEngines.Count > 0;

    private string _discoveryStatus = "Looking for installed engines…";
    public string DiscoveryStatus { get => _discoveryStatus; private set => Set(ref _discoveryStatus, value); }

    /* ---------------- engine updates ---------------- */

    /// <summary>Whether GitHub's release tags are consulted without being asked. Network access, so opt-out-able.</summary>
    public bool CheckUpdatesAutomatically
    {
        get => ConfigService.Config.CheckEngineUpdates;
        set
        {
            ConfigService.Config.CheckEngineUpdates = value;
            ConfigService.Save();
            OnPropertyChanged();
            if (value) _ = CheckUpdatesAsync(force: false);
        }
    }

    private EngineUpdateStatus? _update;
    private bool _checking;

    /// <summary>Bumped by every check so a slow one cannot overwrite the answer of the check that replaced it.</summary>
    private int _checkGeneration;

    public string UpdateMessage =>
        _checking ? "Checking GitHub for a newer release…"
        : _update?.Message ?? "Not checked yet.";

    public string UpdateColor => _checking ? "#9AA0AE" : _update?.Color ?? "#9AA0AE";

    /// <summary>A source clone can be moved onto the new tag right here; anything else can only be told about it.</summary>
    public bool CanUpdateInPlace =>
        _update?.HasUpdate == true && IsSourceEngine && EngineUpdateService.IsGitWorkTree(EngineRoot);

    /// <summary>A precompiled engine with an update waiting — the Epic Games Launcher has to do it.</summary>
    public bool ShowLauncherUpdate => _update?.HasUpdate == true && IsLauncherEngine;

    /// <summary>A source tree that is not a git clone (unpacked zip): nothing here can update it.</summary>
    public bool ShowUnmanagedUpdate =>
        _update?.HasUpdate == true && IsSourceEngine && !EngineUpdateService.IsGitWorkTree(EngineRoot);

    public string UpdateButtonText =>
        _update?.NewerPatch is null ? "⬇  Update engine" : $"⬇  Update to {_update.NewerPatch}";

    private void RaiseUpdateProperties()
    {
        OnPropertyChanged(nameof(UpdateMessage));
        OnPropertyChanged(nameof(UpdateColor));
        OnPropertyChanged(nameof(CanUpdateInPlace));
        OnPropertyChanged(nameof(ShowLauncherUpdate));
        OnPropertyChanged(nameof(ShowUnmanagedUpdate));
        OnPropertyChanged(nameof(UpdateButtonText));
    }

    public ICommand BrowseCommand { get; }
    public ICommand TestAccessCommand { get; }
    public ICommand FetchBranchesCommand { get; }
    public ICommand CloneCommand { get; }
    public ICommand SetupCommand { get; }
    public ICommand GenerateCommand { get; }
    public ICommand RunAllCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand OpenGitHubDocsCommand { get; }
    public ICommand RefreshEnginesCommand { get; }
    public ICommand CheckUpdatesCommand { get; }
    public ICommand UpdateEngineCommand { get; }
    public ICommand OpenLauncherCommand { get; }

    public SourceViewModel()
    {
        BrowseCommand = new RelayCommand(_ => Browse());
        TestAccessCommand = new AsyncRelayCommand(_ => TestAccessAsync(), _ => !IsBusy);
        FetchBranchesCommand = new AsyncRelayCommand(_ => FetchBranchesAsync(), _ => !IsBusy);
        CloneCommand = new AsyncRelayCommand(_ => CloneAsync(), _ => !IsBusy && !IsLauncherEngine);
        SetupCommand = new AsyncRelayCommand(_ => SetupAsync(), _ => !IsBusy && IsSourceEngine);
        GenerateCommand = new AsyncRelayCommand(_ => GenerateAsync(), _ => !IsBusy && IsSourceEngine);
        RunAllCommand = new AsyncRelayCommand(_ => RunAllAsync(), _ => !IsBusy && !IsLauncherEngine);
        CancelCommand = new RelayCommand(_ => Cancel(), _ => IsBusy);
        OpenGitHubDocsCommand = new RelayCommand(_ =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://www.unrealengine.com/ue-on-github",
                UseShellExecute = true,
            }));
        RefreshEnginesCommand = new AsyncRelayCommand(_ => RefreshEnginesAsync());
        CheckUpdatesCommand = new AsyncRelayCommand(_ => CheckUpdatesAsync(force: true), _ => !IsBusy);
        UpdateEngineCommand = new AsyncRelayCommand(_ => UpdateEngineAsync(), _ => !IsBusy && CanUpdateInPlace);
        OpenLauncherCommand = new RelayCommand(_ => EngineUpdateService.OpenEpicLauncher());

        _ = RefreshEnginesAsync();
    }

    protected override void OnEngineChanged()
    {
        base.OnEngineChanged();
        OnPropertyChanged(nameof(EngineRoot));
        OnPropertyChanged(nameof(DiskInfo));
        OnPropertyChanged(nameof(EngineRootState));
        OnPropertyChanged(nameof(ShowSourceWorkflow));

        // Something else repointed the app — picking a project on Sync & Launch or Build switches
        // to that project's engine, and a branch brings its own. The picker has to follow, or it
        // keeps naming an engine nobody is building against any more.
        SyncSelectedEngine();

        // The version belongs to the engine, so the previous answer is meaningless now.
        _update = null;
        RaiseUpdateProperties();
        if (CheckUpdatesAutomatically) _ = CheckUpdatesAsync(force: false);
    }

    /// <summary>
    /// Scans for engines and refreshes the picker. Runs once at startup and on demand; the scan
    /// itself is off the UI thread because it touches the registry and several folders.
    /// </summary>
    private async Task RefreshEnginesAsync()
    {
        try
        {
            DiscoveryStatus = "Looking for installed engines…";
            var engines = await Task.Run(EngineDiscoveryService.Discover);

            DetectedEngines.Clear();
            foreach (var engine in engines) DetectedEngines.Add(engine);
            OnPropertyChanged(nameof(HasDetectedEngines));

            var launcherCount = engines.Count(e => e.IsLauncher);
            var sourceCount = engines.Count - launcherCount;
            DiscoveryStatus = engines.Count == 0
                ? "No engine found on this PC. Install one from the Epic Games Launcher, or clone the source below."
                : $"Found {engines.Count} engine(s): {launcherCount} from the Epic Games Launcher, " +
                  $"{sourceCount} source build(s). Selecting one points the whole app at it.";

            // First run (or the saved folder was deleted): adopt the best engine we found rather
            // than leaving the app pointed at a path that is not an engine at all.
            if (!EngineService.IsEngineRoot(EngineRoot) && engines.Count > 0)
            {
                var pick = engines[0];
                Log("Auto-selected engine: " + pick.Display);
                EngineRoot = pick.Root;
            }

            SyncSelectedEngine();

            // Adopting an engine above raises EngineChanged, which starts the check on its own;
            // when the saved engine was already valid nothing fired, so start it here.
            if (CheckUpdatesAutomatically && _update is null && !_checking)
                _ = CheckUpdatesAsync(force: false);
        }
        catch (Exception ex)
        {
            DiscoveryStatus = "Could not scan for installed engines: " + ex.Message;
        }
    }

    /// <summary>Keeps the picker showing whichever detected engine the engine folder currently points at.</summary>
    private void SyncSelectedEngine()
    {
        var current = EngineInstall.Normalize(ConfigService.Config.EngineRoot);
        var match = DetectedEngines.FirstOrDefault(
            e => string.Equals(e.Root, current, StringComparison.OrdinalIgnoreCase));

        // An engine the scan never reported (browsed to by hand, or named by a project's
        // EngineAssociation) still belongs in the list — otherwise the picker would go blank
        // while the whole app is pointed at it.
        if (match is null && EngineService.IsEngineRoot(current))
        {
            match = new EngineInstall
            {
                Root = current,
                Kind = EngineService.DetectKind(current),
                Version = EngineService.ReadVersion(current) ?? "",
                Origin = "Selected by hand",
            };
            DetectedEngines.Insert(0, match);
            OnPropertyChanged(nameof(HasDetectedEngines));
        }

        if (ReferenceEquals(match, _selectedEngine)) return;
        _selectedEngine = match;
        OnPropertyChanged(nameof(SelectedEngine));
    }

    /// <summary>
    /// Asks GitHub which releases exist and compares them with the selected engine. Deliberately does
    /// not take the busy state: it is a one-second lookup that also runs unattended, and it must never
    /// stand between the user and the buttons below it.
    /// </summary>
    private async Task CheckUpdatesAsync(bool force)
    {
        var root = EngineRoot;
        if (!EngineService.IsEngineRoot(root))
        {
            _update = null;
            RaiseUpdateProperties();
            return;
        }

        var generation = ++_checkGeneration;
        _update = null;
        _checking = true;
        RaiseUpdateProperties();

        EngineUpdateStatus status;
        try
        {
            status = await EngineUpdateService.CheckAsync(root, RemoteUrl, force, CancellationToken.None);
        }
        catch (Exception ex)
        {
            status = EngineUpdateStatus.Unknown("Update check failed: " + ex.Message);
        }

        // A newer check has already started (the engine was switched while this one was in flight),
        // so this answer describes an engine nobody is looking at any more.
        if (generation != _checkGeneration) return;

        _update = status;
        _checking = false;
        if (force) Log("Engine update check: " + status.Message);
        RaiseUpdateProperties();
    }

    /// <summary>
    /// Moves the selected source tree onto the newer release tag and puts it back in a buildable
    /// state: checkout, Setup.bat (the binary dependencies are version-specific), then
    /// GenerateProjectFiles.bat. The editor itself still has to be rebuilt on the Build tab.
    /// </summary>
    private Task UpdateEngineAsync() => RunBusyAsync("Updating engine", async ct =>
    {
        var target = _update?.NewerPatch;
        var root = EngineRoot;
        if (target is null || !EngineUpdateService.IsGitWorkTree(root))
        {
            Status = "Nothing to update.";
            return;
        }

        // A source build exists to be modified, so engine edits are the one thing this must not eat.
        Status = "Checking the engine folder for local changes…";
        var changes = await EngineUpdateService.ListLocalChangesAsync(root, ct);
        if (changes.Count > 0)
        {
            var preview = string.Join(Environment.NewLine, changes.Take(15));
            if (changes.Count > 15) preview += $"{Environment.NewLine}… and {changes.Count - 15} more";
            MessageBox.Show(
                $"{changes.Count} tracked file(s) in {root} have local changes:{Environment.NewLine}{Environment.NewLine}" +
                $"{preview}{Environment.NewLine}{Environment.NewLine}" +
                "Updating would overwrite them. Commit, stash or revert them first.",
                "Engine has local changes", MessageBoxButton.OK, MessageBoxImage.Warning);
            Status = "Update cancelled — the engine folder has local changes.";
            foreach (var change in changes) Log("  local change: " + change);
            return;
        }

        var answer = MessageBox.Show(
            $"Update the engine in {root} from {_update!.LocalVersion} to {target}?" +
            $"{Environment.NewLine}{Environment.NewLine}" +
            $"1. git fetch + checkout {target}-release{Environment.NewLine}" +
            $"2. Setup.bat — re-downloads the binary dependencies for this version (tens of GB){Environment.NewLine}" +
            $"3. GenerateProjectFiles.bat{Environment.NewLine}{Environment.NewLine}" +
            "This takes a long time, and afterwards the editor has to be rebuilt on the Build tab " +
            "before it will start. Any project built against this engine needs a full recompile too.",
            "Update engine", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK)
        {
            Status = "Update cancelled.";
            return;
        }

        Status = $"Step 1/3: fetching and checking out {target}-release…";
        var checkout = await EngineUpdateService.CheckoutReleaseAsync(root, target, Log, ct);
        if (!checkout.Success)
        {
            Status = $"Checkout failed (exit code {checkout.ExitCode}) — the engine is unchanged.";
            return;
        }

        Status = "Step 2/3: Setup.bat (downloading this version's dependencies)…";
        var setup = await EngineService.RunSetupAsync(root, Log, ct);
        if (!setup.Success)
        {
            Status = $"Setup.bat failed (exit code {setup.ExitCode}) — re-run it before building.";
            return;
        }

        Status = "Step 3/3: GenerateProjectFiles.bat…";
        var generate = await EngineService.GenerateProjectFilesAsync(root, Log, ct);
        Status = generate.Success
            ? $"Engine updated to {target}. Rebuild the editor on the Build tab before launching it."
            : $"GenerateProjectFiles.bat failed (exit code {generate.ExitCode}).";

        await RefreshEnginesAsync();
        await CheckUpdatesAsync(force: false);
    });

    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select the Unreal Engine source folder (existing or clone destination)",
        };
        if (Directory.Exists(EngineRoot)) dialog.InitialDirectory = EngineRoot;
        if (dialog.ShowDialog() == true) EngineRoot = dialog.FolderName;
    }

    private Task TestAccessAsync() => RunBusyAsync("Testing GitHub access", async ct =>
    {
        Status = "Contacting GitHub…";
        var result = await EngineService.TestGitHubAccessAsync(RemoteUrl, Log, ct);
        if (result.Success)
        {
            Status = "Access OK — your GitHub account can read the Unreal Engine repository.";
        }
        else
        {
            Status = "Access denied. Link your GitHub account to Epic Games first (button above).";
            Log("Access failed. The UnrealEngine repository is private: you need a GitHub account");
            Log("linked to your Epic Games account, and you must have accepted the org invitation.");
        }
    });

    private Task FetchBranchesAsync() => RunBusyAsync("Fetching branches", async ct =>
    {
        Status = "Listing remote branches…";
        var branches = await EngineService.ListBranchesAsync(RemoteUrl, ct);
        if (branches.Count == 0)
        {
            Status = "Could not list branches (check GitHub access).";
            return;
        }
        Branches.Clear();
        foreach (var b in branches) Branches.Add(b);
        Status = $"Found {branches.Count} branches.";
    });

    private Task CloneAsync() => RunBusyAsync($"Cloning {RemoteUrl} ({Branch})", async ct =>
    {
        Status = $"Cloning branch '{Branch}' — this downloads tens of GB and can take a long time…";
        var result = await EngineService.CloneAsync(RemoteUrl, Branch, EngineRoot, ShallowClone, Log, ct);
        Status = result.Success ? "Clone finished." : $"git clone failed (exit code {result.ExitCode}).";
        OnPropertyChanged(nameof(EngineRootState));
    });

    private Task SetupAsync() => RunBusyAsync("Running Setup.bat", async ct =>
    {
        Status = "Setup.bat downloads ~20+ GB of binary dependencies — be patient…";
        var result = await EngineService.RunSetupAsync(EngineRoot, Log, ct);
        Status = result.Success ? "Setup.bat finished." : $"Setup.bat failed (exit code {result.ExitCode}).";
    });

    private Task GenerateAsync() => RunBusyAsync("Running GenerateProjectFiles.bat", async ct =>
    {
        Status = "Generating Visual Studio project files…";
        var result = await EngineService.GenerateProjectFilesAsync(EngineRoot, Log, ct);
        Status = result.Success ? "Project files generated (UE5.sln)." : $"Generation failed (exit code {result.ExitCode}).";
    });

    private Task RunAllAsync() => RunBusyAsync("Full source setup (clone → Setup.bat → GenerateProjectFiles.bat)", async ct =>
    {
        if (!EngineService.IsEngineRoot(EngineRoot))
        {
            Status = $"Step 1/3: cloning '{Branch}'…";
            var clone = await EngineService.CloneAsync(RemoteUrl, Branch, EngineRoot, ShallowClone, Log, ct);
            if (!clone.Success) { Status = $"Clone failed (exit code {clone.ExitCode}) — aborting."; return; }
            OnPropertyChanged(nameof(EngineRootState));
        }
        else
        {
            Log("Engine source already present — skipping clone.");
        }

        Status = "Step 2/3: Setup.bat (downloading dependencies)…";
        var setup = await EngineService.RunSetupAsync(EngineRoot, Log, ct);
        if (!setup.Success) { Status = $"Setup.bat failed (exit code {setup.ExitCode}) — aborting."; return; }

        Status = "Step 3/3: GenerateProjectFiles.bat…";
        var gen = await EngineService.GenerateProjectFilesAsync(EngineRoot, Log, ct);
        Status = gen.Success
            ? "Source setup complete — go to the Build tab."
            : $"GenerateProjectFiles.bat failed (exit code {gen.ExitCode}).";
    });
}
