using System.Diagnostics;
using System.IO;

namespace UnrealManager.Services;

/// <summary>Operations on an Unreal Engine source tree (clone, setup, generate, build, launch).</summary>
public static class EngineService
{
    public static string SetupBat(string root) => Path.Combine(root, "Setup.bat");
    public static string GenerateBat(string root) => Path.Combine(root, "GenerateProjectFiles.bat");
    public static string BuildBat(string root) => Path.Combine(root, "Engine", "Build", "BatchFiles", "Build.bat");
    public static string EditorExe(string root) => Path.Combine(root, "Engine", "Binaries", "Win64", "UnrealEditor.exe");
    public static string ShaderCompileWorkerExe(string root) => Path.Combine(root, "Engine", "Binaries", "Win64", "ShaderCompileWorker.exe");
    public static string SolutionPath(string root) => Path.Combine(root, "UE5.sln");

    public static bool IsEngineRoot(string root) =>
        !string.IsNullOrWhiteSpace(root) && File.Exists(SetupBat(root));

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
    /// Rebuilds a project's own modules and plugins against this engine — needed whenever a
    /// project made with another engine version (e.g. a launcher build) is opened in a source build.
    /// </summary>
    public static Task<ProcessResult> BuildProjectAsync(
        string root, string projectPath, string platform, string configuration,
        Action<string> onOutput, CancellationToken ct)
    {
        var targetName = FindEditorTargetName(projectPath);
        var args = $"{targetName} {platform} {configuration} -Project=\"{projectPath}\" -WaitMutex";
        return ProcessRunner.RunBatchAsync(BuildBat(root), args, root, onOutput, ct);
    }

    /// <summary>The project's editor target: from Source/*Editor.Target.cs when present, else "&lt;Name&gt;Editor".</summary>
    private static string FindEditorTargetName(string projectPath)
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
            throw new FileNotFoundException("UnrealEditor.exe not found — build the engine first.", exe);

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

    public static string GetFreeSpaceInfo(string path)
    {
        try
        {
            var rootPath = Path.GetPathRoot(Path.GetFullPath(path));
            if (rootPath is null) return "";
            var drive = new DriveInfo(rootPath);
            var freeGb = drive.AvailableFreeSpace / (1024.0 * 1024 * 1024);
            return $"{freeGb:F0} GB free on {drive.Name} (a full source build needs ~200 GB)";
        }
        catch
        {
            return "";
        }
    }
}
