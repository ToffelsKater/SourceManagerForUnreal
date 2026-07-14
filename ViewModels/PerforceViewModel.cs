using System.Collections.ObjectModel;
using System.Windows.Controls;
using System.Windows.Input;
using UnrealManager.Core;
using UnrealManager.Services;

namespace UnrealManager.ViewModels;

public sealed class PerforceViewModel : PageViewModel
{
    public override string Title => "Perforce";
    public override string Icon => "🔁"; // sync arrows

    public string Port
    {
        get => ConfigService.Config.P4Port;
        set { ConfigService.Config.P4Port = value; ConfigService.Save(); OnPropertyChanged(); }
    }

    public string User
    {
        get => ConfigService.Config.P4User;
        set { ConfigService.Config.P4User = value; ConfigService.Save(); OnPropertyChanged(); }
    }

    public string Client
    {
        get => ConfigService.Config.P4Client;
        set { ConfigService.Config.P4Client = value; ConfigService.Save(); OnPropertyChanged(); }
    }

    public string SyncPath
    {
        get => ConfigService.Config.P4SyncPath;
        set { ConfigService.Config.P4SyncPath = value; ConfigService.Save(); OnPropertyChanged(); }
    }

    public bool ForceSync
    {
        get => ConfigService.Config.P4ForceSync;
        set { ConfigService.Config.P4ForceSync = value; ConfigService.Save(); OnPropertyChanged(); }
    }

    public bool ParallelSync
    {
        get => ConfigService.Config.P4ParallelSync;
        set { ConfigService.Config.P4ParallelSync = value; ConfigService.Save(); OnPropertyChanged(); }
    }

    public ObservableCollection<string> Clients { get; } = [];

    private int _syncedFiles;
    public int SyncedFiles { get => _syncedFiles; private set => Set(ref _syncedFiles, value); }

    public ICommand TestCommand { get; }
    public ICommand LoginCommand { get; }
    public ICommand ListClientsCommand { get; }
    public ICommand SyncCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand OpenP4VCommand { get; }

    private P4Connection Conn => new() { Port = Port, User = User, Client = Client };

    public PerforceViewModel()
    {
        TestCommand = new AsyncRelayCommand(_ => TestAsync(), _ => !IsBusy);
        LoginCommand = new AsyncRelayCommand(p => LoginAsync(p), _ => !IsBusy);
        ListClientsCommand = new AsyncRelayCommand(_ => ListClientsAsync(),
            _ => !IsBusy && !string.IsNullOrWhiteSpace(User));
        SyncCommand = new AsyncRelayCommand(_ => SyncAsync(),
            _ => !IsBusy && !string.IsNullOrWhiteSpace(Client));
        CancelCommand = new RelayCommand(_ => Cancel(), _ => IsBusy);
        OpenP4VCommand = new RelayCommand(_ => PerforceService.OpenP4V(Conn));
    }

    private Task TestAsync() => RunBusyAsync("p4 info", async ct =>
    {
        Status = "Contacting Perforce server…";
        var result = await PerforceService.InfoAsync(Conn, Log, ct);
        Status = result.Success ? "Connection OK." : "Connection failed — check port/user (and login if needed).";
    });

    private Task LoginAsync(object? parameter) => RunBusyAsync("p4 login", async ct =>
    {
        // The PasswordBox is passed as the command parameter so the password never sits in a bound property.
        var password = (parameter as PasswordBox)?.Password ?? "";
        if (password.Length == 0)
        {
            Status = "Enter your Perforce password first.";
            return;
        }
        Status = "Logging in…";
        var result = await PerforceService.LoginAsync(Conn, password, Log, ct);
        (parameter as PasswordBox)?.Clear();
        Status = result.Success ? "Logged in — ticket acquired." : "Login failed — check credentials.";
    });

    private Task ListClientsAsync() => RunBusyAsync("p4 clients", async ct =>
    {
        Status = "Listing workspaces…";
        var clients = await PerforceService.ListClientsAsync(Conn, ct);
        Clients.Clear();
        foreach (var c in clients) Clients.Add(c);
        Status = clients.Count == 0
            ? "No workspaces found for this user. Create one in P4V ('Open P4V' button)."
            : $"Found {clients.Count} workspace(s).";
    });

    private Task SyncAsync() => RunBusyAsync($"p4 sync ({Client})", async ct =>
    {
        SyncedFiles = 0;
        Status = "Syncing…";
        var result = await PerforceService.SyncAsync(Conn, SyncPath, ForceSync, ParallelSync, line =>
        {
            Log(line);
            SyncedFiles++;
            Status = $"Syncing… {SyncedFiles} files";
        }, ct);

        Status = result.Success || result.StdErr.Contains("up-to-date")
            ? $"Sync finished ({SyncedFiles} files updated)."
            : $"Sync failed (exit code {result.ExitCode}).";
    });
}
