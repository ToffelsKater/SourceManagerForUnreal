using System.Diagnostics;
using System.IO;
using System.Text.Json;
using UnrealManager.Models;

namespace UnrealManager.Services;

/// <summary>
/// Operations on an Unreal Engine installation (clone, setup, generate, build, launch).
/// Two kinds are supported: a GitHub source tree, and a precompiled build installed by the
/// Epic Games Launcher. Everything that compiles engine code applies to source trees only —
/// use <see cref="IsSourceBuild"/> before offering it.
/// </summary>
public static class EngineService
{
    public static string SetupBat(string root) => Path.Combine(root, "Setup.bat");
    public static string GenerateBat(string root) => Path.Combine(root, "GenerateProjectFiles.bat");
    public static string BuildBat(string root) => Path.Combine(root, "Engine", "Build", "BatchFiles", "Build.bat");
    public static string EditorExe(string root) => Path.Combine(root, "Engine", "Binaries", "Win64", "UnrealEditor.exe");
    public static string ShaderCompileWorkerExe(string root) => Path.Combine(root, "Engine", "Binaries", "Win64", "ShaderCompileWorker.exe");
    public static string SolutionPath(string root) => Path.Combine(root, "UE5.sln");

    /// <summary>Epic's marker for a precompiled ("installed") engine build.</summary>
    public static string InstalledBuildMarker(string root) => Path.Combine(root, "Engine", "Build", "InstalledBuild.txt");

    public static string BuildVersionFile(string root) => Path.Combine(root, "Engine", "Build", "Build.version");

    /// <summary>UnrealBuildTool itself — the only way to generate project files in a launcher build.</summary>
    public static string UnrealBuildToolExe(string root) =>
        Path.Combine(root, "Engine", "Binaries", "DotNET", "UnrealBuildTool", "UnrealBuildTool.exe");

    /// <summary>A GitHub source tree: Setup.bat is shipped by source distributions only.</summary>
    public static bool IsSourceBuild(string root) =>
        !string.IsNullOrWhiteSpace(root) && File.Exists(SetupBat(root));

    /// <summary>
    /// A precompiled engine installed by the Epic Games Launcher. Epic marks these with
    /// Engine/Build/InstalledBuild.txt; they ship no engine source, so engine targets
    /// cannot be compiled against them.
    /// </summary>
    public static bool IsLauncherBuild(string root) =>
        !string.IsNullOrWhiteSpace(root) && File.Exists(InstalledBuildMarker(root));

    public static bool IsEngineRoot(string root) => IsSourceBuild(root) || IsLauncherBuild(root);

    /// <summary>Only meaningful for a folder <see cref="IsEngineRoot"/> already accepted.</summary>
    public static EngineKind DetectKind(string root) =>
        IsSourceBuild(root) ? EngineKind.Source : EngineKind.Launcher;

    /// <summary>"5.8.1" read from Engine/Build/Build.version, or null when it cannot be read.</summary>
    public static string? ReadVersion(string root)
    {
        try
        {
            var file = BuildVersionFile(root);
            if (!File.Exists(file)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var element = doc.RootElement;
            if (!element.TryGetProperty("MajorVersion", out var major) ||
                !element.TryGetProperty("MinorVersion", out var minor))
                return null;

            var patch = element.TryGetProperty("PatchVersion", out var p) ? p.GetInt32() : 0;
            return $"{major.GetInt32()}.{minor.GetInt32()}.{patch}";
        }
        catch
        {
            return null;
        }
    }

    public static bool IsEditorBuilt(string root) => File.Exists(EditorExe(root));

    public static Task<ProcessResult> TestGitHubAccessAsync(string remoteUrl, Action<string> onOutput, CancellationToken ct)
        => ProcessRunner.RunAsync("git", $"ls-remote --heads \"{remoteUrl}\" release", onOutput: onOutput, ct: ct);

    public static async Task<List<string>> ListBranchesAsync(string remoteUrl, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync("git", $"ls-remote --heads \"{remoteUrl}\"", ct: ct);
        if (!result.Success) return [];

        return result.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('\t').LastOrDefault()?.Trim() ?? "")
            .Where(r => r.StartsWith("refs/heads/"))
            .Select(r => r["refs/heads/".Length..])
            .Where(b => b is "release" or "ue5-main" || b.EndsWith("-release") || b.StartsWith("5."))
            .OrderDescending()
            .ToList();
    }

    public static Task<ProcessResult> CloneAsync(
        string remoteUrl, string branch, string destination, bool shallow,
        Action<string> onOutput, CancellationToken ct)
    {
        var depth = shallow ? "--depth 1 " : "";
        var args = $"clone --branch {branch} {depth}--progress \"{remoteUrl}\" \"{destination}\"";
        return ProcessRunner.RunAsync("git", args, onOutput: onOutput, ct: ct);
    }

    public static Task<ProcessResult> RunSetupAsync(string root, Action<string> onOutput, CancellationToken ct)
        => ProcessRunner.RunBatchAsync(SetupBat(root), "--force", root, onOutput, ct);

    public static Task<ProcessResult> GenerateProjectFilesAsync(string root, Action<string> onOutput, CancellationToken ct)
        => ProcessRunner.RunBatchAsync(GenerateBat(root), "", root, onOutput, ct);

    public static Task<ProcessResult> BuildAsync(
        string root, string target, string platform, string configuration,
        Action<string> onOutput, CancellationToken ct)
    {
        var args = $"{target} {platform} {configuration} -WaitMutex";
        return ProcessRunner.RunBatchAsync(BuildBat(root), args, root, onOutput, ct);
    }

    /// <summary>
    /// Builds ShaderCompileWorker — the editor refuses to start without it, and building the
    /// UnrealEditor target alone does not produce it. Always Development config (Epic's convention).
    /// Incremental: near-instant when already up to date.
    /// </summary>
    public static Task<ProcessResult> BuildShaderCompileWorkerAsync(
        string root, Action<string> onOutput, CancellationToken ct)
        => BuildAsync(root, "ShaderCompileWorker", "Win64", "Development", onOutput, ct);

    /// <summary>
    /// Builds a project's own modules and plugins against this engine.
    /// Callers wanting a clean, from-scratch compile should use
    /// <see cref="ProjectRebuildService.FullRecompileAsync"/>, which wipes the generated
    /// folders first — deleting them is what actually clears another engine version's leftovers.
    /// </summary>
    public static Task<ProcessResult> BuildProjectAsync(
        string root, string projectPath, string platform, string configuration,
        Action<string> onOutput, CancellationToken ct)
    {
        var targetName = FindEditorTargetName(projectPath);
        var args = $"{targetName} {platform} {configuration} -Project=\"{projectPath}\" -WaitMutex -FromMsBuild";
        return ProcessRunner.RunBatchAsync(BuildBat(root), args, root, onOutput, ct);
    }

    /// <summary>
    /// Regenerates project files for a specific .uproject against this engine.
    /// Source trees have GenerateProjectFiles.bat; launcher builds do not, so those go straight to
    /// UnrealBuildTool with the arguments Epic's own UnrealVersionSelector uses ("-rocket" is what
    /// tells UBT the engine is an installed build, and "-engine" is meaningless without source).
    /// </summary>
    public static Task<ProcessResult> GenerateProjectFilesAsync(
        string root, string projectPath, Action<string> onOutput, CancellationToken ct)
    {
        if (IsSourceBuild(root))
            return ProcessRunner.RunBatchAsync(
                GenerateBat(root), $"-project=\"{projectPath}\" -game -engine", root, onOutput, ct);

        var ubt = UnrealBuildToolExe(root);
        if (!File.Exists(ubt))
        {
            onOutput("ERROR: UnrealBuildTool.exe not found at " + ubt);
            onOutput("This engine install looks incomplete — verify it in the Epic Games Launcher.");
            return Task.FromResult(new ProcessResult(-1, "", "UnrealBuildTool.exe not found"));
        }

        return ProcessRunner.RunAsync(
            ubt, $"-projectfiles -project=\"{projectPath}\" -game -rocket -progress", root, onOutput, ct);
    }

    /// <summary>Builds an arbitrary project target (e.g. the dedicated server) against this engine.</summary>
    public static Task<ProcessResult> BuildProjectTargetAsync(
        string root, string projectPath, string targetName, string platform, string configuration,
        Action<string> onOutput, CancellationToken ct)
    {
        var args = $"{targetName} {platform} {configuration} -Project=\"{projectPath}\" -WaitMutex -FromMsBuild";
        return ProcessRunner.RunBatchAsync(BuildBat(root), args, root, onOutput, ct);
    }

    /// <summary>True when the project has C++ targets (dedicated servers require a code project).</summary>
    public static bool ProjectHasSource(string projectPath)
    {
        var sourceDir = Path.Combine(Path.GetDirectoryName(projectPath)!, "Source");
        return Directory.Exists(sourceDir) &&
               Directory.EnumerateFiles(sourceDir, "*.Target.cs").Any();
    }

    /// <summary>
    /// Where UnrealBuildTool puts the built server exe. Development builds have no suffix;
    /// every other configuration is "&lt;Target&gt;-&lt;Platform&gt;-&lt;Configuration&gt;.exe".
    /// </summary>
    public static string ServerExePath(string projectPath, string targetName, string platform, string configuration)
    {
        var exe = configuration == "Development"
            ? targetName + ".exe"
            : $"{targetName}-{platform}-{configuration}.exe";
        return Path.Combine(Path.GetDirectoryName(projectPath)!, "Binaries", platform, exe);
    }

    /// <summary>
    /// Launches the dedicated server detached, passing the .uproject first (uncooked/dev run)
    /// followed by the user's arguments (map name, -log, -port=..., etc.).
    /// </summary>
    public static void LaunchServer(string projectPath, string exePath, string? extraArgs)
    {
        if (!File.Exists(exePath))
            throw new FileNotFoundException("Server executable not found - build the dedicated server first.", exePath);

        var args = $"\"{projectPath}\"";
        if (!string.IsNullOrWhiteSpace(extraArgs)) args += " " + extraArgs;

        Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = args,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exePath)!,
        });
    }

    /// <summary>The project's dedicated-server target from Source/*Server.Target.cs, or null when absent.</summary>
    public static string? FindServerTargetName(string projectPath)
    {
        var sourceDir = Path.Combine(Path.GetDirectoryName(projectPath)!, "Source");
        if (!Directory.Exists(sourceDir)) return null;
        var target = Directory.EnumerateFiles(sourceDir, "*Server.Target.cs").FirstOrDefault();
        return target is null ? null : Path.GetFileName(target)[..^".Target.cs".Length];
    }

    /// <summary>
    /// Creates Source/&lt;Name&gt;Server.Target.cs for the project. Derives it from the project's own
    /// game target when possible so engine-version-specific settings (BuildSettingsVersion,
    /// IncludeOrderVersion) carry over; falls back to a generic template otherwise.
    /// Returns the created file's path.
    /// </summary>
    public static string CreateServerTarget(string projectPath)
    {
        var name = Path.GetFileNameWithoutExtension(projectPath);
        var sourceDir = Path.Combine(Path.GetDirectoryName(projectPath)!, "Source");
        if (!Directory.Exists(sourceDir))
            throw new InvalidOperationException(
                "The project has no Source folder. Dedicated servers need a C++ project - " +
                "add a C++ class from the editor first (Tools > New C++ Class), then retry.");

        var serverFile = Path.Combine(sourceDir, name + "Server.Target.cs");
        if (File.Exists(serverFile)) return serverFile;

        var gameTarget = Path.Combine(sourceDir, name + ".Target.cs");
        string content;
        if (File.Exists(gameTarget))
        {
            content = File.ReadAllText(gameTarget)
                .Replace(name + "Target", name + "ServerTarget")
                .Replace("TargetType.Game", "TargetType.Server");
        }
        else
        {
            content =
                "using UnrealBuildTool;\r\n\r\n" +
                $"public class {name}ServerTarget : TargetRules\r\n" +
                "{\r\n" +
                $"\tpublic {name}ServerTarget(TargetInfo Target) : base(Target)\r\n" +
                "\t{\r\n" +
                "\t\tType = TargetType.Server;\r\n" +
                "\t\tDefaultBuildSettings = BuildSettingsVersion.Latest;\r\n" +
                "\t\tIncludeOrderVersion = EngineIncludeOrderVersion.Latest;\r\n" +
                $"\t\tExtraModuleNames.Add(\"{name}\");\r\n" +
                "\t}\r\n" +
                "}\r\n";
        }

        File.WriteAllText(serverFile, content);
        return serverFile;
    }

    /// <summary>The project's editor target: from Source/*Editor.Target.cs when present, else "&lt;Name&gt;Editor".</summary>
    public static string FindEditorTargetName(string projectPath)
    {
        var sourceDir = Path.Combine(Path.GetDirectoryName(projectPath)!, "Source");
        if (Directory.Exists(sourceDir))
        {
            var editorTarget = Directory.EnumerateFiles(sourceDir, "*Editor.Target.cs").FirstOrDefault();
            if (editorTarget is not null)
                return Path.GetFileName(editorTarget)[..^".Target.cs".Length];
        }
        return Path.GetFileNameWithoutExtension(projectPath) + "Editor";
    }

    /// <summary>Launches the editor detached (optionally with a .uproject and extra args).</summary>
    public static void LaunchEditor(string root, string? projectPath, string? extraArgs)
    {
        var exe = EditorExe(root);
        if (!File.Exists(exe))
            throw new FileNotFoundException(
                IsLauncherBuild(root)
                    ? "UnrealEditor.exe not found — this launcher install is incomplete; verify it in the Epic Games Launcher."
                    : "UnrealEditor.exe not found — build the engine first.",
                exe);

        var args = "";
        if (!string.IsNullOrWhiteSpace(projectPath)) args += $"\"{projectPath}\" ";
        if (!string.IsNullOrWhiteSpace(extraArgs)) args += extraArgs;

        Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args.Trim(),
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        });
    }

    /// <summary>
    /// Free space on the drive holding <paramref name="path"/>. The ~200 GB hint only makes sense
    /// while a source build is on the table, so callers pointing at a precompiled engine turn it off.
    /// </summary>
    public static string GetFreeSpaceInfo(string path, bool includeSourceBuildHint = true)
    {
        try
        {
            var rootPath = Path.GetPathRoot(Path.GetFullPath(path));
            if (rootPath is null) return "";
            var drive = new DriveInfo(rootPath);
            var freeGb = drive.AvailableFreeSpace / (1024.0 * 1024 * 1024);
            var hint = includeSourceBuildHint ? " (a full source build needs ~200 GB)" : "";
            return $"{freeGb:F0} GB free on {drive.Name}{hint}";
        }
        catch
        {
            return "";
        }
    }
}
