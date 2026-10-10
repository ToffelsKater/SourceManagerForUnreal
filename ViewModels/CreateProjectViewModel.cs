using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using UnrealManager.Core;
using UnrealManager.Services;

namespace UnrealManager.ViewModels;

/// <summary>
/// Starts a new project from one of the selected engine's templates and makes it the project
/// every other tab works on — so "new project" is one step away from Sync &amp; Launch.
/// </summary>
public sealed class CreateProjectViewModel : PageViewModel
{
    public override string Title => "Create Project";
    public override string Icon => "✨"; // sparkles

    public ObservableCollection<ProjectTemplate> Templates { get; } = [];

    private ProjectTemplate? _selectedTemplate;
    public ProjectTemplate? SelectedTemplate { get => _selectedTemplate; set => Set(ref _selectedTemplate, value); }

    private string _templateSummary = "";
    public string TemplateSummary { get => _templateSummary; private set => Set(ref _templateSummary, value); }

    private string _projectName = "MyProject";
    public string ProjectName
    {
        get => _projectName;
        set { if (Set(ref _projectName, value.Trim())) OnPropertyChanged(nameof(TargetSummary)); }
    }

    public string Location
    {
        get => ConfigService.Config.NewProjectLocation;
        set
        {
            ConfigService.Config.NewProjectLocation = value;
            ConfigService.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(TargetSummary));
        }
    }

    private bool _openWhenDone = true;
    public bool OpenWhenDone { get => _openWhenDone; set => Set(ref _openWhenDone, value); }

    /// <summary>Where the project will land, or what is wrong with the name — follows the fields as they are typed.</summary>
    public string TargetSummary => Problem() ?? $"Creates {Path.Combine(Location, ProjectName, ProjectName + ".uproject")}";

    public ICommand CreateCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand BrowseCommand { get; }

    public CreateProjectViewModel()
    {
        CreateCommand = new AsyncRelayCommand(_ => CreateAsync(),
            _ => !IsBusy && SelectedTemplate is not null && Problem() is null);
        CancelCommand = new RelayCommand(_ => Cancel(), _ => IsBusy);
        BrowseCommand = new RelayCommand(_ => Browse());
        LoadTemplates();
    }

    /// <summary>Templates come from the engine folder, which can change outside the app.</summary>
    public override void OnActivated()
    {
        if (!IsBusy) LoadTemplates();
    }

    protected override void OnEngineChanged()
    {
        base.OnEngineChanged();
        if (!IsBusy) LoadTemplates();
    }

    /// <summary>The editor's New Project dialog rules, so a project made here opens there too.</summary>
    private string? Problem()
    {
        if (!ProjectCreationService.ValidName().IsMatch(ProjectName))
            return "A project name starts with a letter and uses only letters, digits and underscores — 20 characters at most.";
        if (string.IsNullOrWhiteSpace(Location))
            return "Pick a folder to create the project in.";

        var folder = Path.Combine(Location, ProjectName);
        return Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any()
            ? $"{folder} already exists and is not empty — pick another name."
            : null;
    }

    private void LoadTemplates()
    {
        var keep = SelectedTemplate?.Name;
        try
        {
            Templates.Clear();
            foreach (var template in ProjectCreationService.FindTemplates(EngineRootPath)) Templates.Add(template);
            TemplateSummary = Templates.Count == 0
                ? "No templates found in this engine's Templates folder — pick another engine on the 'Get Source' tab."
                : $"{Templates.Count} templates from {Path.Combine(EngineRootPath, "Templates")}.";
        }
        catch (Exception ex)
        {
            TemplateSummary = "Could not read the engine's templates: " + ex.Message;
        }

        SelectedTemplate = Templates.FirstOrDefault(t => t.Name == keep)
                           ?? Templates.FirstOrDefault(t => t.Name == "TP_BlankBP")
                           ?? Templates.FirstOrDefault();
    }

    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select the folder to create the project in" };
        if (Directory.Exists(Location)) dialog.InitialDirectory = Location;
        if (dialog.ShowDialog() == true) Location = dialog.FolderName;
    }

    private Task CreateAsync() => RunBusyAsync($"Create project {ProjectName}", async ct =>
    {
        // Captured up front: the fields stay editable while the copy runs.
        var template = SelectedTemplate!;
        var name = ProjectName;
        var location = Location;
        var root = EngineRootPath;

        Status = $"Creating {name} from {template.Display}…";
        string project;
        try
        {
            project = await Task.Run(() => ProjectCreationService.Create(template, location, name, root, Log, ct), ct);
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled — the half-made project folder was removed.";
            return;
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex.Message);
            Status = "Could not create the project: " + ex.Message;
            return;
        }

        // The new project is what the user works on next, on every tab.
        ConfigService.SetProjectPath(project);
        OnPropertyChanged(nameof(TargetSummary));

        if (template.IsCode)
        {
            Status = "Generating Visual Studio project files…";
            var generated = await EngineService.GenerateProjectFilesAsync(root, project, Log, ct);
            if (!generated.Success)
            {
                Status = $"{name} was created, but generating project files failed (exit code {generated.ExitCode}) — see the log.";
                return;
            }
        }

        if (!OpenWhenDone)
        {
            Status = $"Created {project} — it is now the selected project.";
            return;
        }

        // A C++ project has no editor modules yet; the editor would stop and ask to build them.
        if (template.IsCode)
        {
            Status = $"Compiling {name}…";
            var built = await EngineService.BuildProjectAsync(root, project, "Win64", "Development", Log, ct);
            if (!built.Success)
            {
                Status = $"{name} was created, but compiling it failed (exit code {built.ExitCode}) — see the log.";
                return;
            }
        }

        try
        {
            EngineService.LaunchEditor(root, project, null);
            Status = $"Created {project} and opened it in the editor.";
        }
        catch (Exception ex)
        {
            Status = $"{name} was created, but the editor could not be started: {ex.Message}";
        }
    });
}
