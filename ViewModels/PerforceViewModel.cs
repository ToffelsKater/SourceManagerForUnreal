using System.Collections.ObjectModel;
using System.Windows;
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

    public string Stream
    {
        get => ConfigService.Config.P4Stream;
        set { ConfigService.Config.P4Stream = value; ConfigService.Save(); OnPropertyChanged(); }
    }

    public string SyncPath
    {
        get => ConfigService.Config.P4SyncPath;
        set { ConfigService.Config.P4SyncPath = value; ConfigService.Save(); OnPropertyChanged(); }
    }

    public string Changelist
    {
        get => ConfigService.Config.P4Changelist;
        set { ConfigService.Config.P4Changelist = value; ConfigService.Save(); OnPropertyChanged(); }
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
    public ObservableCollection<string> Streams { get; } = [];

    /* ---- branches: each one owns the stream, workspace, project and launch arguments below ---- */

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

    /// <summary>The active branch's name, editable in place — setting it renames the branch.</summary>
    public string BranchName
    {
        get => ConfigService.ActiveBranch.Name;
        set
        {
            ConfigService.RenameActiveBranch(value);
            RefreshBranches();
        }
    }

    public bool CanRemoveBranch => ConfigService.BranchList.Count > 1;

    private string _currentStream = "";

    /// <summary>What the workspace is really on, as last observed — not what the branch asks for.</summary>
    public string CurrentStream { get => _currentStream; private set => Set(ref _currentStream, value); }

    private int _syncedFiles;
    public int SyncedFiles { get => _syncedFiles; private set => Set(ref _syncedFiles, value); }

    /* ---- pending changelists: pick one, review its files, submit it ---- */

    public ObservableCollection<PendingChange> PendingChanges { get; } = [];

    /// <summary>Files of the selected changelist — what a submit would actually publish.</summary>
    public ObservableCollection<string> ChangeFiles { get; } = [];

    private PendingChange? _selectedChange;
    public PendingChange? SelectedChange
    {
        get => _selectedChange;
        set
        {
            if (!Set(ref _selectedChange, value)) return;

            // Everything was loaded with the list, so showing a changelist costs no round trip.
            ChangeDescription = value?.IsDefault == true ? "" : value?.FullDescription ?? "";
            ChangeFiles.Clear();
            foreach (var file in value?.Files ?? []) ChangeFiles.Add(file);
            OnPropertyChanged(nameof(ChangeSummary));
            OnPropertyChanged(nameof(HasSelectedChange));

            // Selection can change without user input (after a refresh or a submit), and command
            // states only re-evaluate on input, so Submit is re-asked about explicitly.
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool HasSelectedChange => SelectedChange is not null;

    private string _changeDescription = "";
    public string ChangeDescription
    {
        get => _changeDescription;
        set { if (Set(ref _changeDescription, value)) OnPropertyChanged(nameof(ChangeSummary)); }
    }

    public string ChangeSummary => SelectedChange is null
        ? "No changelist selected."
        : $"{ChangeFiles.Count} file(s) would be submitted from " +
          (SelectedChange.IsDefault ? "the default changelist" : $"changelist {SelectedChange.Id}") + ".";

    public ICommand TestCommand { get; }
    public ICommand LoginCommand { get; }
    public ICommand ListClientsCommand { get; }
    public ICommand ListStreamsCommand { get; }
    public ICommand SwitchStreamCommand { get; }
    public ICommand SyncCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand OpenP4VCommand { get; }
    public ICommand AddBranchCommand { get; }
    public ICommand RemoveBranchCommand { get; }
    public ICommand RefreshChangesCommand { get; }
    public ICommand SubmitChangeCommand { get; }

    private P4Connection Conn => new() { Port = Port, User = User, Client = Client };

    public PerforceViewModel()
    {
        TestCommand = new AsyncRelayCommand(_ => TestAsync(), _ => !IsBusy);
        LoginCommand = new AsyncRelayCommand(p => LoginAsync(p), _ => !IsBusy);
        ListClientsCommand = new AsyncRelayCommand(_ => ListClientsAsync(),
            _ => !IsBusy && !string.IsNullOrWhiteSpace(User));
        ListStreamsCommand = new AsyncRelayCommand(_ => ListStreamsAsync(), _ => !IsBusy);
        SwitchStreamCommand = new AsyncRelayCommand(_ => SwitchStreamAsync(),
            _ => !IsBusy && !string.IsNullOrWhiteSpace(Client) && !string.IsNullOrWhiteSpace(Stream));
        SyncCommand = new AsyncRelayCommand(_ => SyncAsync(),
            _ => !IsBusy && !string.IsNullOrWhiteSpace(Client));
        CancelCommand = new RelayCommand(_ => Cancel(), _ => IsBusy);
        OpenP4VCommand = new RelayCommand(_ => PerforceService.OpenP4V(Conn));
        AddBranchCommand = new RelayCommand(_ => AddBranch());
        RemoveBranchCommand = new RelayCommand(_ => RemoveBranch(), _ => CanRemoveBranch);
        RefreshChangesCommand = new AsyncRelayCommand(_ => RefreshChangesAsync(),
            _ => !IsBusy && !string.IsNullOrWhiteSpace(Client));
        SubmitChangeCommand = new AsyncRelayCommand(_ => SubmitChangeAsync(),
            _ => !IsBusy && SelectedChange is not null && ChangeFiles.Count > 0);

        RefreshBranches();
    }

    protected override void OnBranchChanged()
    {
        base.OnBranchChanged();
        RefreshBranches();

        // Both belong to the workspace we just navigated away from.
        CurrentStream = "";
        PendingChanges.Clear();
        SelectedChange = null;
    }

    private void RefreshBranches()
    {
        Branches.Clear();
        foreach (var branch in ConfigService.BranchList) Branches.Add(branch.Name);
        OnPropertyChanged(nameof(ActiveBranch));
        OnPropertyChanged(nameof(BranchName));
        OnPropertyChanged(nameof(CanRemoveBranch));
    }

    private void AddBranch()
    {
        // Seeded from the current settings: a new branch usually differs from the old one by its stream alone.
        var branch = ConfigService.AddBranch("New branch");
        Status = $"Branch '{branch.Name}' added — rename it and pick its stream.";
    }

    private void RemoveBranch()
    {
        var removed = ActiveBranch;
        ConfigService.RemoveBranch(removed);
        Status = $"Branch '{removed}' removed.";
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

    private Task ListStreamsAsync() => RunBusyAsync("p4 streams", async ct =>
    {
        Status = "Listing streams…";
        var streams = await PerforceService.ListStreamsAsync(Conn, ct);
        Streams.Clear();
        foreach (var s in streams) Streams.Add(s);

        CurrentStream = await PerforceService.GetClientStreamAsync(Conn, Client, ct) ?? "";
        Status = streams.Count == 0
            ? "No streams found — this depot may not use streams (leave the stream field empty)."
            : $"Found {streams.Count} stream(s)." +
              (CurrentStream.Length == 0 ? "" : $" Workspace '{Client}' is on {CurrentStream}.");
    });

    private Task SwitchStreamAsync() => RunBusyAsync($"p4 switch {Stream}", async ct =>
    {
        Status = $"Checking workspace '{Client}'…";
        var current = await PerforceService.GetClientStreamAsync(Conn, Client, ct);
        if (current is null)
        {
            CurrentStream = "";
            Status = $"'{Client}' is not a stream workspace, so it cannot be switched. Use a stream " +
                     "workspace for this branch, or clear the stream field and map the branch in the workspace view.";
            return;
        }

        if (string.Equals(current, Stream, StringComparison.OrdinalIgnoreCase))
        {
            CurrentStream = current;
            Status = $"Already on {Stream} — nothing to switch.";
            return;
        }

        Log($"Switching workspace '{Client}' from {current} to {Stream}…");
        Status = $"Switching to {Stream}…";
        var result = await PerforceService.SwitchStreamAsync(Conn, Stream, Log, ct);
        if (!result.Success)
        {
            CurrentStream = current;
            Status = $"Switch failed (exit code {result.ExitCode}) — files left open for edit will block it.";
            return;
        }

        CurrentStream = await PerforceService.GetClientStreamAsync(Conn, Client, ct) ?? Stream;
        Status = $"Workspace switched to {CurrentStream}.";
    });

    private Task RefreshChangesAsync() => RunBusyAsync("p4 changes -s pending", async ct =>
    {
        Status = "Listing pending changelists…";
        var count = await LoadChangesAsync(ct);
        Status = count == 0
            ? $"No pending changelists in workspace '{Client}'."
            : $"Found {count} pending changelist(s) in '{Client}'.";
    });

    /// <summary>Reloads the changelist list in place, keeping the selection where it can be kept.</summary>
    private async Task<int> LoadChangesAsync(CancellationToken ct)
    {
        var previous = SelectedChange?.Id;
        var changes = await PerforceService.ListPendingChangesAsync(Conn, ct);

        PendingChanges.Clear();
        foreach (var change in changes) PendingChanges.Add(change);

        SelectedChange = PendingChanges.FirstOrDefault(c => c.Id == previous) ?? PendingChanges.FirstOrDefault();
        return changes.Count;
    }

    private Task SubmitChangeAsync() => RunBusyAsync("p4 submit", async ct =>
    {
        var change = SelectedChange;
        if (change is null || ChangeFiles.Count == 0)
        {
            Status = "Select a pending changelist with files in it first.";
            return;
        }

        var description = ChangeDescription.Trim();
        if (description.Length == 0)
        {
            Status = "Enter a description — Perforce will not accept a submit without one.";
            return;
        }

        // A submit publishes to the whole team and cannot be taken back, so it is always confirmed
        // against the exact file list, workspace and server it is going to.
        var preview = string.Join(Environment.NewLine, ChangeFiles.Take(15));
        if (ChangeFiles.Count > 15) preview += $"{Environment.NewLine}… and {ChangeFiles.Count - 15} more";

        var answer = MessageBox.Show(
            $"Submit {(change.IsDefault ? "the default changelist" : "changelist " + change.Id)} " +
            $"to {Port} as {User}?{Environment.NewLine}{Environment.NewLine}" +
            $"Workspace: {Client}{Environment.NewLine}" +
            $"Description: {description}{Environment.NewLine}{Environment.NewLine}" +
            $"{ChangeFiles.Count} file(s):{Environment.NewLine}{preview}{Environment.NewLine}{Environment.NewLine}" +
            "This publishes the files to everyone on the server and cannot be undone.",
            "Submit changelist", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK)
        {
            Status = "Submit cancelled.";
            return;
        }

        // A numbered changelist submits with the description it already stores, so an edited one
        // has to be written back before the submit rather than after it.
        if (!change.IsDefault && description != change.FullDescription.Trim())
        {
            Status = "Updating the changelist description…";
            var updated = await PerforceService.SetChangeDescriptionAsync(Conn, change.Id, description, ct);
            if (!updated.Success)
            {
                Status = $"Could not update the description (exit code {updated.ExitCode}) — nothing was submitted.";
                Log(updated.StdErr.Trim());
                return;
            }
        }

        Status = $"Submitting {(change.IsDefault ? "the default changelist" : "changelist " + change.Id)}…";
        var result = await PerforceService.SubmitAsync(Conn, change, description, Log, ct);
        if (!result.Success)
        {
            var output = result.StdOut + result.StdErr;
            Status = output.Contains("must be resolved") || output.Contains("out of date")
                ? "Submit rejected: files are out of date. Sync, resolve the conflicts in P4V, then submit again."
                : $"Submit failed (exit code {result.ExitCode}) — see the log.";
            return;
        }

        // p4 reports the number it actually committed under, which can differ from the pending one.
        var submitted = result.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .LastOrDefault(l => l.Contains("submitted"));

        Log(submitted ?? "Submit finished.");
        await LoadChangesAsync(ct);
        Status = submitted ?? "Submit finished.";
    });

    private Task SyncAsync() => RunBusyAsync($"p4 sync ({Client})", async ct =>
    {
        SyncedFiles = 0;
        var revision = PerforceService.NormalizeRevision(Changelist);
        Status = revision.Length == 0 ? "Syncing…" : $"Syncing to {revision}…";
        var result = await PerforceService.SyncAsync(Conn, SyncPath, Changelist, ForceSync, ParallelSync, line =>
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
