using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace UnrealManager.Services;

/// <summary>
/// Full from-scratch recompile of a project against the configured engine:
/// close locking processes -> delete generated folders and Visual Studio artifacts
/// -> verify enabled plugins exist -> regenerate project files -> build.
///
/// This is deliberately heavier than UnrealBuildTool's -Rebuild flag, which only
/// recompiles binaries and leaves Intermediate/DDC/stale .sln files behind. Those
/// leftovers are exactly what keeps a launcher-built project from loading in a
/// source engine.
/// </summary>
public static class ProjectRebuildService
{
    /// <summary>Processes that hold locks on the project's Binaries/Intermediate.</summary>
    public static readonly string[] UnrealProcesses =
    [
        "UnrealEditor", "UnrealEditor-Cmd", "UE4Editor", "UE4Editor-Cmd",
        "UnrealBuildTool", "AutomationTool", "ShaderCompileWorker", "CrashReportClient",
    ];

    /// <summary>Processes that lock .vs / .sln / .vcxproj files.</summary>
    public static readonly string[] VisualStudioProcesses =
    [
        "devenv", "MSBuild", "VBCSCompiler",
    ];

    private static readonly string[] GeneratedFolders =
    [
        "Intermediate", "DerivedDataCache", "Binaries", ".vs",
    ];

    private static readonly string[] VsArtifactPatterns =
    [
        "*.sln", "*.vcxproj", "*.vcxproj.filters", "*.vcxproj.user",
        "*.suo", "*.opensdf", "*.sdf", "*.VC.db", ".vsconfig",
    ];

    public static string[] GetRunning(IEnumerable<string> names) =>
        names.SelectMany(n => Process.GetProcessesByName(n))
             .Select(p => p.ProcessName)
             .Distinct(StringComparer.OrdinalIgnoreCase)
             .ToArray();

    public static void KillProcesses(IEnumerable<string> names, Action<string> log)
    {
        foreach (var name in names)
        {
            foreach (var proc in Process.GetProcessesByName(name))
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(10_000);
                    log($"    closed {proc.ProcessName} [{proc.Id}]");
                }
                catch (Exception ex)
                {
                    log($"    could not close {name}: {ex.Message}");
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// Clears the read-only attribute on a file/folder and everything below it.
    /// Perforce marks unopened files read-only, so deletes fail without this.
    /// </summary>
    private static void ClearReadOnly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var attrs = File.GetAttributes(path);
                if (attrs.HasFlag(FileAttributes.ReadOnly))
                    File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
                return;
            }
            if (!Directory.Exists(path)) return;

            var dir = new DirectoryInfo(path);
            if (dir.Attributes.HasFlag(FileAttributes.ReadOnly))
                dir.Attributes &= ~FileAttributes.ReadOnly;

            foreach (var entry in dir.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
            {
                try
                {
                    if (entry.Attributes.HasFlag(FileAttributes.ReadOnly))
                        entry.Attributes &= ~FileAttributes.ReadOnly;
                }
                catch { /* individual entry may vanish mid-scan */ }
            }
        }
        catch { /* best effort — the delete below reports the real failure */ }
    }

    private static void DeleteDirectory(string path, Action<string> log)
    {
        if (!Directory.Exists(path))
        {
            log($"    not found (skip): {Path.GetFileName(path)}");
            return;
        }

        log($"    deleting: {path}");
        ClearReadOnly(path);
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            log($"    first attempt failed ({ex.Message}) — clearing attributes and retrying…");
            ClearReadOnly(path);
            try { Directory.Delete(path, recursive: true); } catch { /* reported below */ }
        }

        if (Directory.Exists(path))
            throw new IOException(
                $"Could not delete '{path}'. Something still holds a lock on it — " +
                "close Unreal Editor / Visual Studio / Explorer windows in that folder and try again.");
    }

    private static void DeleteVsArtifacts(string projectDir, Action<string> log)
    {
        foreach (var pattern in VsArtifactPatterns)
        {
            foreach (var file in Directory.EnumerateFiles(projectDir, pattern, SearchOption.TopDirectoryOnly))
            {
                ClearReadOnly(file);
                try
                {
                    File.Delete(file);
                    log($"    deleted: {Path.GetFileName(file)}");
                }
                catch (Exception ex)
                {
                    log($"    could not delete {Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Enabled plugins in the .uproject that have no .uplugin in the project or the engine.
    /// These are the Fab/Marketplace plugins that were installed into a different engine —
    /// the real cause of most "Missing Modules" dialogs.
    /// </summary>
    public static List<string> FindMissingEnabledPlugins(string projectPath, string engineRoot)
    {
        var missing = new List<string>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(File.ReadAllText(projectPath)); }
        catch { return missing; }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("Plugins", out var plugins) ||
                plugins.ValueKind != JsonValueKind.Array)
                return missing;

            var available = AvailablePluginNames(Path.GetDirectoryName(projectPath)!, engineRoot);

            foreach (var plugin in plugins.EnumerateArray())
            {
                if (!plugin.TryGetProperty("Name", out var nameEl)) continue;
                var name = nameEl.GetString();
                if (string.IsNullOrWhiteSpace(name)) continue;

                var enabled = plugin.TryGetProperty("Enabled", out var en) && en.ValueKind == JsonValueKind.True;
                if (enabled && !available.Contains(name))
                    missing.Add(name);
            }
        }
        return missing;
    }

    private static HashSet<string> AvailablePluginNames(string projectDir, string engineRoot)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Scan(string dir)
        {
            if (!Directory.Exists(dir)) return;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.uplugin", SearchOption.AllDirectories))
                    names.Add(Path.GetFileNameWithoutExtension(file));
            }
            catch { /* unreadable subtree — treat as no plugins there */ }
        }
        Scan(Path.Combine(projectDir, "Plugins"));
        Scan(Path.Combine(engineRoot, "Engine", "Plugins"));
        return names;
    }

    /// <summary>
    /// Runs the whole clean-and-rebuild. Returns null on success, or a message describing the failure.
    /// </summary>
    public static async Task<string?> FullRecompileAsync(
        string engineRoot, string projectPath, string platform, string configuration,
        bool closeVisualStudio, Action<string> log, Action<string> status, CancellationToken ct)
    {
        var projectDir = Path.GetDirectoryName(projectPath)!;

        // 1. Release file locks.
        status("Full recompile: closing Unreal processes…");
        log("Closing Unreal processes that lock project binaries…");
        KillProcesses(UnrealProcesses, log);
        if (closeVisualStudio)
        {
            log("Closing Visual Studio processes that lock .vs/.sln files…");
            KillProcesses(VisualStudioProcesses, log);
        }
        await Task.Delay(750, ct);

        // 2 + 3. Delete generated output and stale Visual Studio files.
        status("Full recompile: deleting generated folders…");
        log("Deleting generated folders (read-only flags cleared first — Perforce marks files read-only):");
        try
        {
            foreach (var folder in GeneratedFolders)
                DeleteDirectory(Path.Combine(projectDir, folder), log);

            log("Removing stale Visual Studio project files:");
            DeleteVsArtifacts(projectDir, log);
        }
        catch (IOException ex)
        {
            return ex.Message;
        }

        // 4. Preflight: plugins the project enables must actually exist somewhere.
        status("Full recompile: checking enabled plugins…");
        var missing = FindMissingEnabledPlugins(projectPath, engineRoot);
        if (missing.Count > 0)
        {
            var list = string.Join(", ", missing.OrderBy(m => m));
            log("PREFLIGHT FAILED — enabled plugin(s) not found: " + list);
            log("Copy them into the project's Plugins folder (e.g. from the launcher engine's");
            log("Engine\\Plugins\\Marketplace), or disable them in the .uproject, then run again.");
            return $"Missing enabled plugin(s): {list}. Copy them into the project's Plugins folder " +
                   "or disable them in the .uproject — the build would fail on them anyway.";
        }
        log("All enabled plugins found.");

        // 5. Regenerate project files against this engine.
        status("Full recompile: generating project files…");
        var gen = await EngineService.GenerateProjectFilesAsync(engineRoot, projectPath, log, ct);
        if (!gen.Success)
            return $"GenerateProjectFiles failed (exit code {gen.ExitCode}).";

        // 6. Build. Everything was deleted above, so this is a full compile by definition.
        var target = EngineService.FindEditorTargetName(projectPath);
        status($"Full recompile: building {target} ({configuration})…");
        var build = await EngineService.BuildProjectAsync(
            engineRoot, projectPath, platform, configuration, log, ct);
        if (!build.Success)
            return $"Build failed (exit code {build.ExitCode}) — see the log for the compiler errors.";

        return null;
    }
}
