using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using UnrealManager.Core;
using UnrealManager.Models;
using UnrealManager.Services;

namespace UnrealManager.ViewModels;

/// <summary>
/// A page that works on the selected .uproject. The project is picked from the ones found on this
/// PC rather than typed, and picking one repoints the app at the engine that project was made with —
/// launching a project against the wrong engine version is the fastest way to a broken editor.
/// </summary>
public abstract class ProjectPageViewModel : PageViewModel
{
    /// <summary>Every project found on this PC, most recently opened first.</summary>
    public ObservableCollection<UnrealProject> DetectedProjects { get; } = [];

    /// <summary>False until a real project is in the list — an empty picker is worse than none.</summary>
    public bool HasDetectedProjects => DetectedProjects.Any(p => !p.IsNone);

    /// <summary>
    /// Whether the picker offers "no project". True for pages whose work is optional on the
    /// project (Sync &amp; Launch opens the editor on its own); false where one is required.
    /// </summary>
    protected virtual bool AllowNoProject => false;

    private UnrealProject? _selectedProject;

    public UnrealProject? SelectedProject
    {
        get => _selectedProject;
        set
        {
            if (!Set(ref _selectedProject, value) || value is null) return;
            if (!string.Equals(ProjectPath, value.Path, StringComparison.OrdinalIgnoreCase))
                ProjectPath = value.Path;

            // "No project" names no engine, so the selected one stays as it is.
            if (value.IsNone) ProjectEngineStatus = "";
            else _ = ApplyProjectEngineAsync(value);
        }
    }

    /// <summary>The project every page acts on — one setting, shared by the Build and Sync tabs.</summary>
    public string ProjectPath
    {
        get => ConfigService.Config.ProjectPath;
        set => ConfigService.SetProjectPath(value);
    }

    private string _projectDiscoveryStatus = "Looking for Unreal projects on this PC…";

    public string ProjectDiscoveryStatus
    {
        get => _projectDiscoveryStatus;
        private set => Set(ref _projectDiscoveryStatus, value);
    }

    private string _projectEngineStatus = "";

    /// <summary>What happened to the engine when the project was picked — shown under the picker.</summary>
    public string ProjectEngineStatus
    {
        get => _projectEngineStatus;
        private set => Set(ref _projectEngineStatus, value);
    }

    public ICommand BrowseProjectCommand { get; }
    public ICommand RefreshProjectsCommand { get; }

    protected ProjectPageViewModel()
    {
        BrowseProjectCommand = new RelayCommand(_ => BrowseProject());
        RefreshProjectsCommand = new AsyncRelayCommand(_ => RefreshProjectsAsync(force: true));
        ConfigService.ProjectChanged += OnProjectChanged;

        _ = RefreshProjectsAsync(force: false);
    }

    /// <summary>The project changed here or on the other project page — follow it either way.</summary>
    private void OnProjectChanged()
    {
        OnPropertyChanged(nameof(ProjectPath));
        SyncSelectedProject();
        OnProjectPathChanged();
    }

    /// <summary>Called after the selected project changed. Override to refresh anything derived from it.</summary>
    protected virtual void OnProjectPathChanged() { }

    protected override void OnBranchChanged()
    {
        base.OnBranchChanged();
        // The branch brought its own project and engine with it: follow it in the picker, but do
        // not switch the engine again — the branch already chose one.
        SyncSelectedProject();
        ProjectEngineStatus = "";
        OnProjectPathChanged();
    }

    /// <summary>
    /// Scans for projects and refreshes the picker. The scan itself walks the disk, so it runs off
    /// the UI thread and is shared with the other project page rather than repeated per tab.
    /// </summary>
    protected async Task RefreshProjectsAsync(bool force)
    {
        try
        {
            ProjectDiscoveryStatus = "Looking for Unreal projects on this PC…";
            var projects = await ProjectDiscoveryService.GetAsync(force);

            DetectedProjects.Clear();
            if (AllowNoProject) DetectedProjects.Add(UnrealProject.None);
            foreach (var project in projects) DetectedProjects.Add(project);

            ProjectDiscoveryStatus = projects.Count == 0
                ? "No .uproject found on this PC — use Browse… to point at one."
                : $"Found {projects.Count} project(s). Picking one also switches to the engine it was made with.";

            SyncSelectedProject();
            OnPropertyChanged(nameof(HasDetectedProjects));
        }
        catch (Exception ex)
        {
            ProjectDiscoveryStatus = "Could not scan for projects: " + ex.Message;
        }
    }

    /// <summary>
    /// Keeps the picker showing whatever the config points at, adding an entry for a project the
    /// scan did not find (one picked with Browse…, or one on a drive that was not swept).
    /// </summary>
    private void SyncSelectedProject()
    {
        var configured = ConfigService.Config.ProjectPath;
        if (string.IsNullOrWhiteSpace(configured))
        {
            var none = DetectedProjects.FirstOrDefault(p => p.IsNone);
            if (ReferenceEquals(none, _selectedProject)) return;
            _selectedProject = none;
            OnPropertyChanged(nameof(SelectedProject));
            return;
        }

        var normalized = UnrealProject.Normalize(configured);
        var match = DetectedProjects.FirstOrDefault(
            p => string.Equals(p.Path, normalized, StringComparison.OrdinalIgnoreCase));

        if (match is null && File.Exists(normalized))
        {
            match = new UnrealProject
            {
                Path = normalized,
                EngineAssociation = ProjectDiscoveryService.ReadEngineAssociation(normalized),
                Origin = "Selected by hand",
            };
            DetectedProjects.Insert(AllowNoProject ? 1 : 0, match); // keep "no project" at the top
            OnPropertyChanged(nameof(HasDetectedProjects));
        }

        if (ReferenceEquals(match, _selectedProject)) return;
        _selectedProject = match;
        OnPropertyChanged(nameof(SelectedProject));
    }

    /// <summary>
    /// Points the app at the engine the project names in its EngineAssociation. When no engine on
    /// this PC matches, the current one is kept and the mismatch is spelled out instead — silently
    /// building against the wrong version is what this is here to prevent.
    /// </summary>
    private async Task ApplyProjectEngineAsync(UnrealProject project)
    {
        try
        {
            var engines = await Task.Run(EngineDiscoveryService.Discover);
            var match = ProjectDiscoveryService.MatchEngine(project, engines);

            if (match is null)
            {
                ProjectEngineStatus =
                    $"{project.Name} was made with {project.WantedEngineDescription}, which is not installed on " +
                    "this PC — keeping the current engine. Pick one on the 'Get Source' tab if that is wrong.";
                Log($"Project {project.Name}: no engine on this PC matches '{project.EngineAssociation}' — " +
                    "engine left unchanged.");
                return;
            }

            if (string.Equals(EngineInstall.Normalize(ConfigService.Config.EngineRoot), match.Root,
                    StringComparison.OrdinalIgnoreCase))
            {
                ProjectEngineStatus = $"{project.Name} uses the engine already selected — {match.Display}";
                return;
            }

            Log($"Project {project.Name} → switching engine to {match.Display}");
            ConfigService.SetEngineRoot(match.Root);
            ProjectEngineStatus = $"Engine switched to match {project.Name} — {match.Display}";
        }
        catch (Exception ex)
        {
            ProjectEngineStatus = "Could not work out this project's engine: " + ex.Message;
        }
    }

    private void BrowseProject()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select an Unreal project (.uproject)",
            Filter = "Unreal Project (*.uproject)|*.uproject",
        };
        if (Directory.Exists(Path.GetDirectoryName(ProjectPath)))
            dialog.InitialDirectory = Path.GetDirectoryName(ProjectPath);

        if (dialog.ShowDialog() != true) return;

        ProjectPath = dialog.FileName;
        if (_selectedProject is not null) _ = ApplyProjectEngineAsync(_selectedProject);
    }
}
