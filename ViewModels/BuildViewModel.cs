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
