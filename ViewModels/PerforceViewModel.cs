using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
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
        set
        {
            ConfigService.Config.P4Stream = value;
            ConfigService.Save();
            OnPropertyChanged();
            RaiseHistoryScope();
        }
    }

    public string SyncPath
    {
        get => ConfigService.Config.P4SyncPath;
        set
        {
            ConfigService.Config.P4SyncPath = value;
            ConfigService.Save();
            OnPropertyChanged();
            RaiseHistoryScope();
        }
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

    /* ---- commit history: what has been submitted to the branch's stream lately ---- */

    /// <summary>How many submitted changelists the history shows; also what the picker offers.</summary>
    public ObservableCollection<int> HistoryLimits { get; } = [25, 50, 100, 200];

    private int _historyLimit = 25;
    public int HistoryLimit
    {
        get => _historyLimit;
        set { if (Set(ref _historyLimit, value)) OnPropertyChanged(nameof(HistoryHeader)); }
    }

    public ObservableCollection<SubmittedChange> Commits { get; } = [];

    /// <summary>The history grouped by day, so it reads as "Today / Yesterday / date" like a log.</summary>
    public ICollectionView CommitsView { get; }

    /// <summary>Files of the selected commit — already loaded with the history, never re-fetched.</summary>
    public ObservableCollection<ChangedFile> CommitFiles { get; } = [];

    /// <summary>The same files grouped under their depot folder, which is how a long list stays readable.</summary>
    public ICollectionView CommitFilesView { get; }

    private SubmittedChange? _selectedCommit;
    public SubmittedChange? SelectedCommit
    {
        get => _selectedCommit;
        set
        {
            if (!Set(ref _selectedCommit, value)) return;

            CommitFiles.Clear();
            foreach (var file in value?.Files ?? []) CommitFiles.Add(file);

            OnPropertyChanged(nameof(HasSelectedCommit));
            OnPropertyChanged(nameof(CommitFilesHeader));
            OnPropertyChanged(nameof(CommitDescription));
            OnPropertyChanged(nameof(CommitTruncationNote));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool HasSelectedCommit => SelectedCommit is not null;

    /// <summary>The selected commit's description in full — the row only shows its first line.</summary>
    public string CommitDescription => SelectedCommit?.Description.Trim() ?? "";

    public string CommitFilesHeader => SelectedCommit is null
        ? ""
        : $"Changelist {SelectedCommit.Id} by {SelectedCommit.User} · " +
          $"{SelectedCommit.TimeDisplay} · {SelectedCommit.FileSummary}";

    /// <summary>Shown only when a changelist was too big to list in full.</summary>
    public string CommitTruncationNote => SelectedCommit?.FilesTruncated == true
        ? $"Only the first {PerforceService.MaxFilesPerChange} files are listed — open the changelist in P4V to see them all."
        : "";

    /// <summary>What the history is filtered to: the branch's stream, else the sync path, else everything.</summary>
    public string HistoryScope => !string.IsNullOrWhiteSpace(Stream)
        ? Stream
        : !string.IsNullOrWhiteSpace(SyncPath) ? SyncPath : "the whole depot";

    /// <summary>Says what is on screen once loaded, and what pressing the button would fetch before that.</summary>
    public string HistoryHeader => Commits.Count == 0
        ? $"Press 'Load history' to fetch the last {HistoryLimit} submitted changelists on {HistoryScope}."
        : $"Showing the last {Commits.Count} submitted changelists on {HistoryScope}, newest first.";

    /// <summary>The scope line is built from the stream and sync path, so it follows them as they are typed.</summary>
    private void RaiseHistoryScope()
    {
        OnPropertyChanged(nameof(HistoryScope));
        OnPropertyChanged(nameof(HistoryHeader));
    }

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
    public ICommand RefreshHistoryCommand { get; }
    public ICommand SyncToCommitCommand { get; }

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
        RefreshHistoryCommand = new AsyncRelayCommand(_ => RefreshHistoryAsync(), _ => !IsBusy);
        SyncToCommitCommand = new RelayCommand(_ => SyncToCommit(), _ => SelectedCommit is not null);

        CommitsView = CollectionViewSource.GetDefaultView(Commits);
        CommitsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SubmittedChange.DayGroup)));

        CommitFilesView = CollectionViewSource.GetDefaultView(CommitFiles);
        CommitFilesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ChangedFile.Folder)));

        RefreshBranches();
    }

    /// <summary>How long a just-loaded page counts as fresh, so flipping between tabs is not four p4 calls each time.</summary>
    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(15);

    private DateTime _lastAutoRefresh = DateTime.MinValue;

    /// <summary>
    /// Everything on this page comes from the server and goes stale while another tab is up, so
    /// opening the tab reloads the lot. Read-only: it lists, it never syncs or submits. With no
    /// login ticket it stops at the status line instead of firing four commands that would fail.
    /// </summary>
    public override void OnActivated()
    {
        if (IsBusy) return;
        if (DateTime.UtcNow - _lastAutoRefresh < AutoRefreshInterval) return;
        if (string.IsNullOrWhiteSpace(Port) || string.IsNullOrWhiteSpace(User)) return;
        _ = RefreshAllAsync();
    }

    private Task RefreshAllAsync() => RunBusyAsync("Perforce refresh", async ct =>
    {
        // A failed check still counts as an attempt, or every tab click retries a missing p4.
        _lastAutoRefresh = DateTime.UtcNow;

        if (await ProcessRunner.WhereAsync("p4") is null)
        {
            Status = "Perforce CLI (p4) not found — install it from the Dependencies tab.";
            return;
        }

        var login = await PerforceService.LoginStatusAsync(Conn, ct);
        if (!login.Success)
        {
            Status = "Not logged in — enter your password and press 'Log in' to load workspaces, " +
                     "streams, changelists and history.";
            return;
        }

        await LoadEverythingAsync(ct);
    });

    /// <summary>
    /// Loads every list on the page from the server. Assumes a ticket is already in hand — both
    /// callers (opening the tab, logging in) have just established one.
    /// </summary>
    private async Task LoadEverythingAsync(CancellationToken ct)
    {
        _lastAutoRefresh = DateTime.UtcNow;
        Status = "Refreshing from Perforce…";

        var clients = await LoadClientsAsync(ct);
        var streams = await LoadStreamsAsync(ct);

        // Pending changelists belong to one workspace, so they only mean anything once one is picked.
        var pending = string.IsNullOrWhiteSpace(Client) ? 0 : await LoadChangesAsync(ct);
        var commits = await LoadHistoryAsync(ct);

        Status = $"Refreshed: {clients} workspace(s), {streams} stream(s), {pending} pending changelist(s), " +
                 $"{commits} submitted changelist(s) on {HistoryScope}." +
                 (CurrentStream.Length == 0 ? "" : $" Workspace '{Client}' is on {CurrentStream}.");
    }

    protected override void OnBranchChanged()
    {
        base.OnBranchChanged();
        RefreshBranches();

        // These all belong to the workspace and stream we just navigated away from.
        CurrentStream = "";
        PendingChanges.Clear();
        SelectedChange = null;
        Commits.Clear();
        SelectedCommit = null;
        OnPropertyChanged(nameof(HistoryHeader));

        // The new branch's workspace and stream have not been looked at yet.
        _lastAutoRefresh = DateTime.MinValue;
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
        if (!result.Success)
        {
            Status = "Login failed — check credentials.";
            return;
        }

        // The page was empty until the ticket existed, so fill it now rather than making the user
        // press four buttons after logging in.
        Log("Logged in — ticket acquired.");
        await LoadEverythingAsync(ct);
    });

    private Task ListClientsAsync() => RunBusyAsync("p4 clients", async ct =>
    {
        Status = "Listing workspaces…";
        var clients = await LoadClientsAsync(ct);
        Status = clients == 0
            ? "No workspaces found for this user. Create one in P4V ('Open P4V' button)."
            : $"Found {clients} workspace(s).";
    });

    /// <summary>Reloads the workspace picker. Returns how many the server listed.</summary>
    private async Task<int> LoadClientsAsync(CancellationToken ct)
    {
        var clients = await PerforceService.ListClientsAsync(Conn, ct);
        Clients.Clear();
        foreach (var c in clients) Clients.Add(c);
        return clients.Count;
    }

    private Task ListStreamsAsync() => RunBusyAsync("p4 streams", async ct =>
    {
        Status = "Listing streams…";
        var streams = await LoadStreamsAsync(ct);
        Status = streams == 0
            ? "No streams found — this depot may not use streams (leave the stream field empty)."
            : $"Found {streams} stream(s)." +
              (CurrentStream.Length == 0 ? "" : $" Workspace '{Client}' is on {CurrentStream}.");
    });

    /// <summary>
    /// Reloads the stream picker and re-reads what the workspace is really on — the one fact on
    /// this page that another tool (P4V, a teammate's switch) can change behind our back.
    /// </summary>
    private async Task<int> LoadStreamsAsync(CancellationToken ct)
    {
        var streams = await PerforceService.ListStreamsAsync(Conn, ct);
        Streams.Clear();
        foreach (var s in streams) Streams.Add(s);

        CurrentStream = string.IsNullOrWhiteSpace(Client)
            ? ""
            : await PerforceService.GetClientStreamAsync(Conn, Client, ct) ?? "";
        return streams.Count;
    }

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

    private Task RefreshHistoryAsync() => RunBusyAsync("p4 changes -s submitted", async ct =>
    {
        var scope = HistoryScope;
        Status = $"Loading the last {HistoryLimit} submitted changelists on {scope}…";
        var count = await LoadHistoryAsync(ct);

        Status = count == 0
            ? $"No submitted changelists found on {scope} — check the stream path, or log in first."
            : $"Loaded {count} changelist(s) on {scope}, newest first.";
    });

    /// <summary>Reloads the history in place, keeping the changelist being read selected.</summary>
    private async Task<int> LoadHistoryAsync(CancellationToken ct)
    {
        // The stream wins over the sync path: the history is meant to answer "what landed on my branch".
        var path = !string.IsNullOrWhiteSpace(Stream) ? Stream : SyncPath;
        var commits = await PerforceService.ListSubmittedChangesAsync(Conn, path, HistoryLimit, ct);

        // A refresh must not move the reader: the changelist being read is looked up again after
        // the merge rather than the newest one being forced back on.
        var previous = SelectedCommit?.Id;
        MergeCommits(commits);
        SelectedCommit = Commits.FirstOrDefault(c => c.Id == previous) ?? Commits.FirstOrDefault();
        OnPropertyChanged(nameof(HistoryHeader));
        return commits.Count;
    }

    /// <summary>
    /// Applies a freshly loaded history to the rows already on screen instead of rebuilding them.
    /// Clearing the collection resets the list — it scrolls back to the top and drops the row being
    /// read — so rows that are already right are left untouched, which makes a refresh that found
    /// nothing new move nothing at all.
    /// </summary>
    private void MergeCommits(List<SubmittedChange> commits)
    {
        for (var i = 0; i < commits.Count; i++)
        {
            if (i >= Commits.Count) Commits.Add(commits[i]);
            else if (Commits[i].Id != commits[i].Id) Commits[i] = commits[i];
        }

        while (Commits.Count > commits.Count) Commits.RemoveAt(Commits.Count - 1);
    }

    /// <summary>Points the sync fields at the selected commit, so "Sync now" reproduces that exact state.</summary>
    private void SyncToCommit()
    {
        var commit = SelectedCommit;
        if (commit is null) return;

        Changelist = commit.Id;
        Status = $"Sync target set to changelist {commit.Id} — press 'Sync now' to get the workspace to that state.";
    }

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
