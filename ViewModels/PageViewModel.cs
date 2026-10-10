using System.Collections.ObjectModel;
using UnrealManager.Core;
using UnrealManager.Services;

namespace UnrealManager.ViewModels;

public abstract class PageViewModel : ObservableObject
{
    public abstract string Title { get; }
    public abstract string Icon { get; }

    protected PageViewModel()
    {
        ConfigService.EngineChanged += OnEngineChanged;
        ConfigService.BranchChanged += OnBranchChanged;
    }

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

    /// <summary>
    /// Called after the active branch changed. Branch settings are read straight out of the config,
    /// so a page only has to re-raise the properties bound to them. Passing null refreshes them all.
    /// </summary>
    protected virtual void OnBranchChanged() => OnPropertyChanged(null);

    /// <summary>
    /// Called when this page becomes the visible one. Override to bring a page up to date with
    /// whatever it shows from outside the app — a tab nobody is looking at goes stale.
    /// </summary>
    public virtual void OnActivated() { }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; protected set => Set(ref _isBusy, value); }

    private string _status = "";
    public string Status { get => _status; protected set => Set(ref _status, value); }

    protected CancellationTokenSource? Cts;

    protected static void Log(string line) => LogService.Instance.Log(line);
    protected static void LogHeader(string title) => LogService.Instance.LogHeader(title);

    /// <summary>
    /// Refills the list behind an editable ComboBox in place. Clearing it would drop the combo's
    /// selected item, and an editable combo then blanks its text — writing "" back into the bound
    /// setting (the saved workspace, stream or branch) on every refresh. So entries are only added
    /// and removed, and the one matching <paramref name="current"/> stays even when the server
    /// leaves it out.
    /// </summary>
    protected static void Refill(ObservableCollection<string> list, IReadOnlyCollection<string> items, string current)
    {
        for (var i = list.Count - 1; i >= 0; i--)
            if (!items.Contains(list[i]) && !string.Equals(list[i], current, StringComparison.OrdinalIgnoreCase))
                list.RemoveAt(i);

        foreach (var item in items)
            if (!list.Contains(item)) list.Add(item);
    }

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
