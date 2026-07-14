using System.Collections.ObjectModel;
using System.Windows.Input;
using UnrealManager.Core;
using UnrealManager.Services;

namespace UnrealManager.ViewModels;

public enum CheckStatus { Unknown, Ok, Missing, OptionalMissing }

public sealed class CheckItem : ObservableObject
{
    public string Name { get; init; } = "";

    /// <summary>winget package id; when set and the tool is missing, an Install button is shown.</summary>
    public string? WingetId { get; init; }

    private CheckStatus _statusValue;
    public CheckStatus StatusValue
    {
        get => _statusValue;
        set
        {
            if (Set(ref _statusValue, value))
            {
                OnPropertyChanged(nameof(Glyph));
                OnPropertyChanged(nameof(GlyphColor));
                OnPropertyChanged(nameof(ShowInstall));
            }
        }
    }

    public bool ShowInstall => WingetId is not null &&
        StatusValue is CheckStatus.Missing or CheckStatus.OptionalMissing;

    private string _detail = "";
    public string Detail { get => _detail; set => Set(ref _detail, value); }

    public string Glyph => StatusValue switch
    {
        CheckStatus.Ok => "✔",              // check mark
        CheckStatus.Missing => "✖",         // cross
        CheckStatus.OptionalMissing => "○", // circle
        _ => "…",
    };

    public string GlyphColor => StatusValue switch
    {
        CheckStatus.Ok => "#3FBF6F",
        CheckStatus.Missing => "#E05555",
        CheckStatus.OptionalMissing => "#E0A030",
        _ => "#9AA0AE",
    };
}

public sealed class DependenciesViewModel : PageViewModel
{
    public override string Title => "Dependencies";
    public override string Icon => "🔧"; // wrench emoji

    public ObservableCollection<VsInstance> VsInstances { get; } = [];
    public ObservableCollection<CheckItem> VsChecks { get; } = [];
    public ObservableCollection<CheckItem> ToolChecks { get; } = [];

    private VsInstance? _selectedInstance;
    public VsInstance? SelectedInstance
    {
        get => _selectedInstance;
        set { if (Set(ref _selectedInstance, value) && value is not null) _ = RefreshVsChecksAsync(value); }
    }

    private readonly List<string> _missingComponentIds = [];

    public ICommand RefreshCommand { get; }
    public ICommand InstallMissingCommand { get; }
    public ICommand InstallVsCommand { get; }
    public ICommand InstallToolCommand { get; }

    public DependenciesViewModel()
    {
        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync());
        InstallMissingCommand = new RelayCommand(_ => InstallMissing(),
            _ => SelectedInstance is not null && _missingComponentIds.Count > 0);
        InstallVsCommand = new AsyncRelayCommand(_ => InstallVsAsync(), _ => VsInstances.Count == 0);
        InstallToolCommand = new AsyncRelayCommand(p => InstallToolAsync(p as CheckItem), _ => !IsBusy);
        _ = RefreshAsync();
    }

    private Task InstallToolAsync(CheckItem? item)
    {
        if (item?.WingetId is null) return Task.CompletedTask;
        return RunBusyAsync($"Installing {item.Name} (winget)", async ct =>
        {
            Status = $"Installing {item.Name}... approve the UAC prompt if one appears.";
            Log("If a Windows security (UAC) prompt appears, approve it to continue.");
            var result = await ProcessRunner.RunAsync("winget",
                $"install --id {item.WingetId} --accept-source-agreements --accept-package-agreements --silent",
                onOutput: Log, ct: ct);
            if (result.Success)
            {
                Status = $"{item.Name} installed.";
                await RefreshToolsAsync();
            }
            else
            {
                Status = $"winget exited with code {result.ExitCode} - see log ({item.Name}).";
            }
        });
    }

    private async Task RefreshAsync()
    {
        IsBusy = true;
        Status = "Checking environment...";
        try
        {
            await RefreshToolsAsync();

            VsInstances.Clear();
            if (!VisualStudioService.IsVsWhereAvailable)
            {
                Status = "Visual Studio Installer not found - no VS installed. Use 'Install VS 2022 Community'.";
                VsChecks.Clear();
                return;
            }

            foreach (var instance in await VisualStudioService.GetInstancesAsync())
                VsInstances.Add(instance);

            if (VsInstances.Count == 0)
            {
                Status = "No Visual Studio instance found. Use 'Install VS 2022 Community'.";
                VsChecks.Clear();
                return;
            }

            SelectedInstance = VsInstances[0];
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshVsChecksAsync(VsInstance instance)
    {
        IsBusy = true;
        Status = $"Checking components of {instance.DisplayName}...";
        try
        {
            VsChecks.Clear();
            _missingComponentIds.Clear();

            foreach (var (req, installed) in await VisualStudioService.CheckRequirementsAsync(instance))
            {
                VsChecks.Add(new CheckItem
                {
                    Name = req.Name,
                    StatusValue = installed ? CheckStatus.Ok
                        : req.Required ? CheckStatus.Missing : CheckStatus.OptionalMissing,
                    Detail = installed ? "installed" : req.Required ? "REQUIRED - missing" : "optional - missing",
                });
                if (!installed) _missingComponentIds.Add(req.AnyOfIds[0]);
            }

            var missingRequired = VsChecks.Count(c => c.StatusValue == CheckStatus.Missing);
            Status = missingRequired == 0
                ? "Visual Studio is ready for Unreal Engine source builds."
                : $"{missingRequired} required component(s) missing - click 'Install missing components'.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshToolsAsync()
    {
        // Pick up tools installed while the app is running (winget doesn't update our PATH).
        ProcessRunner.RefreshProcessPath();

        ToolChecks.Clear();

        async Task AddTool(string name, string exe, string? wingetId, string missingHint, bool required = true)
        {
            var path = await ProcessRunner.WhereAsync(exe);
            ToolChecks.Add(new CheckItem
            {
                Name = name,
                WingetId = wingetId,
                StatusValue = path is not null ? CheckStatus.Ok
                    : required ? CheckStatus.Missing : CheckStatus.OptionalMissing,
                Detail = path ?? missingHint,
            });
        }

        await AddTool("Git", "git", "Git.Git",
            "required to download the engine source");
        await AddTool("Perforce CLI (p4)", "p4", "Perforce.P4V",
            "required for the Perforce / Sync & Launch tabs");
        await AddTool("Perforce Visual Client (p4v)", "p4v", "Perforce.P4V",
            "optional but handy for managing workspaces", required: false);
        await AddTool(".NET SDK", "dotnet", null,
            "not required - Unreal and this app bring their own .NET", required: false);
    }

    private void InstallMissing()
    {
        if (SelectedInstance is null || _missingComponentIds.Count == 0) return;
        LogHeader("Visual Studio Installer");
        Log("Launching VS Installer to add: " + string.Join(", ", _missingComponentIds));
        Log("Approve the UAC prompt; the installer shows its own progress. Click Refresh when it finishes.");
        VisualStudioService.LaunchModify(SelectedInstance, _missingComponentIds);
    }

    private Task InstallVsAsync() => RunBusyAsync("Installing Visual Studio 2022 Community (winget)", async ct =>
    {
        Status = "Installing Visual Studio 2022 Community - this downloads several GB...";
        var result = await VisualStudioService.InstallVsCommunityAsync(Log, ct);
        Status = result.Success ? "Visual Studio installed. Refreshing..." : $"winget exited with code {result.ExitCode}.";
        if (result.Success) await RefreshAsync();
    });
}
