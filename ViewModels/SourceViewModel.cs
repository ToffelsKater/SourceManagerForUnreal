using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using UnrealManager.Core;
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
            ConfigService.Config.EngineRoot = value;
            ConfigService.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(DiskInfo));
            OnPropertyChanged(nameof(EngineRootState));
        }
    }

    public string DiskInfo => string.IsNullOrWhiteSpace(EngineRoot) ? "" : EngineService.GetFreeSpaceInfo(EngineRoot);

    public string EngineRootState =>
        EngineService.IsEngineRoot(EngineRoot) ? "✔ Engine source found at this location"
        : Directory.Exists(EngineRoot) && Directory.EnumerateFileSystemEntries(EngineRoot).Any()
            ? "⚠ Folder exists and is not empty (clone needs an empty/new folder)"
            : "Folder is empty or will be created by the clone";

    public ObservableCollection<string> Branches { get; } =
        ["release", "5.6", "5.5", "5.4", "ue5-main"];

    public ICommand BrowseCommand { get; }
    public ICommand TestAccessCommand { get; }
    public ICommand FetchBranchesCommand { get; }
    public ICommand CloneCommand { get; }
    public ICommand SetupCommand { get; }
    public ICommand GenerateCommand { get; }
    public ICommand RunAllCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand OpenGitHubDocsCommand { get; }

    public SourceViewModel()
    {
        BrowseCommand = new RelayCommand(_ => Browse());
        TestAccessCommand = new AsyncRelayCommand(_ => TestAccessAsync(), _ => !IsBusy);
        FetchBranchesCommand = new AsyncRelayCommand(_ => FetchBranchesAsync(), _ => !IsBusy);
        CloneCommand = new AsyncRelayCommand(_ => CloneAsync(), _ => !IsBusy);
        SetupCommand = new AsyncRelayCommand(_ => SetupAsync(), _ => !IsBusy && EngineService.IsEngineRoot(EngineRoot));
        GenerateCommand = new AsyncRelayCommand(_ => GenerateAsync(), _ => !IsBusy && EngineService.IsEngineRoot(EngineRoot));
        RunAllCommand = new AsyncRelayCommand(_ => RunAllAsync(), _ => !IsBusy);
        CancelCommand = new RelayCommand(_ => Cancel(), _ => IsBusy);
        OpenGitHubDocsCommand = new RelayCommand(_ =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://www.unrealengine.com/ue-on-github",
                UseShellExecute = true,
            }));
    }

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
