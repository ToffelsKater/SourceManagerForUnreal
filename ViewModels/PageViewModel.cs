using UnrealManager.Core;
using UnrealManager.Services;

namespace UnrealManager.ViewModels;

public abstract class PageViewModel : ObservableObject
{
    public abstract string Title { get; }
    public abstract string Icon { get; }

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
