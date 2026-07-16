using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Input;
using UnrealManager.Core;
using UnrealManager.Services;

namespace UnrealManager.ViewModels;

public sealed partial class BuildViewModel : PageViewModel
{
    public override string Title => "Build";
    public override string Icon => "🔨"; // hammer

    public ObservableCollection<string> Targets { get; } =
        ["UnrealEditor", "ShaderCompileWorker", "UnrealPak", "UnrealInsights", "CrashReportClient", "UnrealLightmass"];

    public ObservableCollection<string> Configurations { get; } =
        ["Development", "DebugGame", "Debug", "Test", "Shipping"];

    public string Target
    {
        get => ConfigService.Config.BuildTarget;
        set { ConfigService.Config.BuildTarget = value; ConfigService.Save(); OnPropertyChanged(); }
    }

    public string Configuration
    {
        get => ConfigService.Config.BuildConfiguration;
        set { ConfigService.Config.BuildConfiguration = value; ConfigService.Save(); OnPropertyChanged(); }
    }

    private double _progress;
    public double Progress { get => _progress; private set => Set(ref _progress, value); }

    private bool _progressKnown;
    public bool ProgressKnown { get => _progressKnown; private set => Set(ref _progressKnown, value); }

    public ICommand BuildCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand OpenSolutionCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand BrowseProjectCommand { get; }
    public ICommand CreateServerTargetCommand { get; }
    public ICommand BuildServerCommand { get; }
    public ICommand OpenServerOutputCommand { get; }

    [GeneratedRegex(@"^\[(\d+)/(\d+)\]")]
    private static partial Regex ProgressRegex();

    public BuildViewModel()
    {
        BuildCommand = new AsyncRelayCommand(_ => BuildAsync(),
            _ => !IsBusy && EngineService.IsEngineRoot(ConfigService.Config.EngineRoot));
        CancelCommand = new RelayCommand(_ => Cancel(), _ => IsBusy);
        OpenSolutionCommand = new RelayCommand(_ => OpenSolution(),
            _ => File.Exists(EngineService.SolutionPath(ConfigService.Config.EngineRoot)));
        OpenFolderCommand = new RelayCommand(_ => OpenFolder(),
            _ => Directory.Exists(ConfigService.Config.EngineRoot));

        BrowseProjectCommand = new RelayCommand(_ => BrowseProject());
        CreateServerTargetCommand = new RelayCommand(_ => CreateServerTarget(),
            _ => !IsBusy && ProjectSet && EngineService.ProjectHasSource(ProjectPath) &&
                 EngineService.FindServerTargetName(ProjectPath) is null);
        BuildServerCommand = new AsyncRelayCommand(_ => BuildServerAsync(),
            _ => !IsBusy && ProjectSet && EngineService.FindServerTargetName(ProjectPath) is not null &&
                 EngineService.IsEngineRoot(ConfigService.Config.EngineRoot));
        OpenServerOutputCommand = new RelayCommand(_ => OpenServerOutput(),
            _ => ProjectSet && Directory.Exists(ServerOutputDir));
        RefreshServerStatus();
    }

    /* ---------------- dedicated server (project) ---------------- */

    public string ProjectPath
    {
        get => ConfigService.Config.ProjectPath;
        set
        {
            ConfigService.Config.ProjectPath = value;
            ConfigService.Save();
            OnPropertyChanged();
            RefreshServerStatus();
        }
    }

    private bool ProjectSet => !string.IsNullOrWhiteSpace(ProjectPath) && File.Exists(ProjectPath);

    private string ServerOutputDir =>
        ProjectSet ? Path.Combine(Path.GetDirectoryName(ProjectPath)!, "Binaries", "Win64") : "";

    private string _serverStatus = "";
    public string ServerStatus { get => _serverStatus; private set => Set(ref _serverStatus, value); }

    private void RefreshServerStatus()
    {
        if (!ProjectSet)
        {
            ServerStatus = "Select your .uproject (shared with the Sync & Launch tab).";
            return;
        }
        if (!EngineService.ProjectHasSource(ProjectPath))
        {
            ServerStatus = "This project has no C++ source. Dedicated servers need a code project - " +
                           "add any C++ class in the editor (Tools > New C++ Class) first.";
            return;
        }
        var target = EngineService.FindServerTargetName(ProjectPath);
        ServerStatus = target is null
            ? "No server target yet - click 'Create server target' to add <Name>Server.Target.cs."
            : $"Server target found: {target}. Ready to build.";
    }

    private void BrowseProject()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select the Unreal project to build a dedicated server for",
            Filter = "Unreal Project (*.uproject)|*.uproject",
        };
        if (dialog.ShowDialog() == true) ProjectPath = dialog.FileName;
    }

    private void CreateServerTarget()
    {
        LogHeader("Create dedicated-server target");
        try
        {
            var file = EngineService.CreateServerTarget(ProjectPath);
            Log("Created: " + file);
            Log("Derived from the project's game target, so engine-version settings carry over.");
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex.Message);
        }
        RefreshServerStatus();
    }

    private Task BuildServerAsync()
    {
        var root = ConfigService.Config.EngineRoot;
        var project = ProjectPath;
        var config = Configuration;
        var platform = ConfigService.Config.BuildPlatform;
        var target = EngineService.FindServerTargetName(project)!;

        return RunBusyAsync($"Building dedicated server {target} {platform} {config}", async ct =>
        {
            Progress = 0;
            ProgressKnown = false;
            Status = $"Building {target} ({config})…";

            var result = await EngineService.BuildProjectTargetAsync(root, project, target, platform, config, line =>
            {
                Log(line);
                var match = ProgressRegex().Match(line.TrimStart());
                if (match.Success && double.TryParse(match.Groups[2].Value, out var total) && total > 0)
                {
                    ProgressKnown = true;
                    Progress = double.Parse(match.Groups[1].Value) / total * 100.0;
                }
            }, ct);

            if (!result.Success)
            {
                Status = $"Server build FAILED (exit code {result.ExitCode}) — see log for errors.";
                return;
            }

            Progress = 100;
            ProgressKnown = true;
            var exe = config == "Development" ? $"{target}.exe" : $"{target}-{platform}-{config}.exe";
            Status = $"Dedicated server built: Binaries\\Win64\\{exe}";
            Log("");
            Log($"Run it (uncooked, for development): {exe} \"{project}\" <MapName> -log");
            Log("For distribution you still need to cook/package content (UAT BuildCookRun -server).");
            RefreshServerStatus();
        });
    }

    private void OpenServerOutput()
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = ServerOutputDir,
            UseShellExecute = true,
        });
    }

    private Task BuildAsync()
    {
        var root = ConfigService.Config.EngineRoot;
        var target = Target;
        var config = Configuration;
        var platform = ConfigService.Config.BuildPlatform;

        return RunBusyAsync($"Building {target} {platform} {config}", async ct =>
        {
            Progress = 0;
            ProgressKnown = false;
            Status = $"Building {target} ({config})… a clean editor build can take 1–3 hours.";

            var result = await EngineService.BuildAsync(root, target, platform, config, line =>
            {
                Log(line);
                var match = ProgressRegex().Match(line.TrimStart());
                if (match.Success && double.TryParse(match.Groups[2].Value, out var total) && total > 0)
                {
                    ProgressKnown = true;
                    Progress = double.Parse(match.Groups[1].Value) / total * 100.0;
                }
            }, ct);

            if (!result.Success)
            {
                Status = $"Build FAILED (exit code {result.ExitCode}) — see log for errors.";
                return;
            }

            // The editor won't start without ShaderCompileWorker, and building the UnrealEditor
            // target alone doesn't produce it — build it too (incremental, fast when up to date).
            if (target == "UnrealEditor")
            {
                Status = "Building ShaderCompileWorker (required by the editor)…";
                ProgressKnown = false;
                var scw = await EngineService.BuildShaderCompileWorkerAsync(root, Log, ct);
                if (!scw.Success)
                {
                    Status = $"UnrealEditor built, but ShaderCompileWorker FAILED (exit code {scw.ExitCode}).";
                    return;
                }
            }

            Progress = 100;
            ProgressKnown = true;
            Status = $"Build succeeded: {target} {config}.";
        });
    }

    private void OpenSolution()
    {
        var sln = EngineService.SolutionPath(ConfigService.Config.EngineRoot);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = sln,
            UseShellExecute = true,
        });
    }

    private void OpenFolder()
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = ConfigService.Config.EngineRoot,
            UseShellExecute = true,
        });
    }
}
