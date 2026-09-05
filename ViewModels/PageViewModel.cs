using UnrealManager.Core;
using UnrealManager.Services;

namespace UnrealManager.ViewModels;

public abstract class PageViewModel : ObservableObject
{
    public abstract string Title { get; }
    public abstract string Icon { get; }

    protected PageViewModel() => ConfigService.EngineChanged += OnEngineChanged;

    /// <summary>The engine folder every page works against.</summary>
    protected static string EngineRootPath => ConfigService.Config.EngineRoot;

    /// <summary>True for a GitHub source tree — the only kind whose engine code can be compiled here.</summary>
    public bool IsSourceEngine => EngineService.IsSourceBuild(EngineRootPath);

    /// <summary>True for a precompiled engine installed by the Epic Games Launcher.</summary>
    public bool IsLauncherEngine => EngineService.IsLauncherBuild(EngineRootPath);

    /// <summary>One line naming the selected engine and what it can do — shown on every page that acts on it.</summary>
    public string EngineSummary
    {
        get
        {
            var root = EngineRootPath;
            if (string.IsNullOrWhiteSpace(root))
                return "No engine selected — pick one on the 'Get Source' tab.";
            if (IsLauncherEngine)
            {
                var version = EngineService.ReadVersion(root);
                return $"Engine: {root} — Epic Games Launcher build{(version is null ? "" : " " + version)}, " +
                       "precompiled. Engine-compilation steps do not apply and are hidden.";
            }
            if (IsSourceEngine)
                return $"Engine: {root} — source build. Engine targets can be compiled.";
            return $"Engine: {root} — not a recognised engine folder.";
        }
    }

    /// <summary>Refreshes everything that depends on which engine is selected. Override to add your own.</summary>
    protected virtual void OnEngineChanged()
    {
        OnPropertyChanged(nameof(IsSourceEngine));
        OnPropertyChanged(nameof(IsLauncherEngine));
        OnPropertyChanged(nameof(EngineSummary));
    }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; protected set => Set(ref _isBusy, value); }

    private string _status = "";
    public string Status { get => _status; protected set => Set(ref _status, value); }

    protected CancellationTokenSource? Cts;

    protected static void Log(string line) => LogService.Instance.Log(line);
    protected static void LogHeader(string title) => LogService.Instance.LogHeader(title);

    public void Cancel() => Cts?.Cancel();

    /// <summary>Runs a long operation with busy state + a fresh cancellation token.</summary>
    protected async Task RunBusyAsync(string header, Func<CancellationToken, Task> work)
    {
        Cts = new CancellationTokenSource();
        IsBusy = true;
        LogHeader(header);
        try
        {
            await work(Cts.Token);
        }
        finally
        {
            IsBusy = false;
            Cts.Dispose();
            Cts = null;
        }
    }
}
