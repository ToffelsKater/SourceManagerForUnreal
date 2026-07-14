using System.IO;
using System.Text.Json;

namespace UnrealManager.Services;

public sealed class VsInstance
{
    public string InstallationPath { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string InstallationVersion { get; init; } = "";

    public override string ToString() =>
        $"{DisplayName} ({InstallationVersion})";
}

/// <summary>One dependency Unreal needs from Visual Studio. Satisfied if ANY of the ids is installed.</summary>
public sealed record VsRequirement(string Name, string[] AnyOfIds, bool Required);

public static class VisualStudioService
{
    public static readonly string VsInstallerDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        "Microsoft Visual Studio", "Installer");

    public static string VsWherePath => Path.Combine(VsInstallerDir, "vswhere.exe");
    public static string VsSetupPath => Path.Combine(VsInstallerDir, "setup.exe");

    /// <summary>Workloads/components Unreal Engine 5 source builds need (per Epic's documentation).</summary>
    public static readonly VsRequirement[] Requirements =
    [
        new("Workload: Desktop development with C++",
            ["Microsoft.VisualStudio.Workload.NativeDesktop"], Required: true),
        new("Workload: Game development with C++",
            ["Microsoft.VisualStudio.Workload.NativeGame"], Required: true),
        new("Workload: .NET desktop development",
            ["Microsoft.VisualStudio.Workload.ManagedDesktop"], Required: false),
        new("MSVC v143 C++ x64/x86 build tools",
            ["Microsoft.VisualStudio.Component.VC.Tools.x86.x64"], Required: true),
        new("Windows 10/11 SDK",
            ["Microsoft.VisualStudio.Component.Windows11SDK.26100",
             "Microsoft.VisualStudio.Component.Windows11SDK.22621",
             "Microsoft.VisualStudio.Component.Windows10SDK.20348",
             "Microsoft.VisualStudio.Component.Windows10SDK.19041"], Required: true),
        new(".NET SDK (in-VS component)",
            ["Microsoft.NetCore.Component.SDK"], Required: false),
        new(".NET Framework 4.6.2+ targeting pack",
            ["Microsoft.Net.Component.4.6.2.TargetingPack",
             "Microsoft.Net.Component.4.8.TargetingPack"], Required: false),
        new("Unreal Engine IDE support (installer component)",
            ["Component.Unreal"], Required: false),
    ];

    public static bool IsVsWhereAvailable => File.Exists(VsWherePath);

    public static async Task<List<VsInstance>> GetInstancesAsync()
    {
        var instances = new List<VsInstance>();
        if (!IsVsWhereAvailable) return instances;

        var result = await ProcessRunner.RunAsync(VsWherePath, "-products * -format json -utf8");
        if (!result.Success || string.IsNullOrWhiteSpace(result.StdOut)) return instances;

        try
        {
            using var doc = JsonDocument.Parse(result.StdOut);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                instances.Add(new VsInstance
                {
                    InstallationPath = el.TryGetProperty("installationPath", out var p) ? p.GetString() ?? "" : "",
                    DisplayName = el.TryGetProperty("displayName", out var d) ? d.GetString() ?? "" : "Visual Studio",
                    InstallationVersion = el.TryGetProperty("installationVersion", out var v) ? v.GetString() ?? "" : "",
                });
            }
        }
        catch (JsonException)
        {
            // vswhere returned something unexpected; treat as no instances.
        }
        return instances;
    }

    /// <summary>True if ANY installed VS instance has the given component/workload.</summary>
    public static async Task<bool> AnyInstanceHasComponentAsync(string componentId)
    {
        if (!IsVsWhereAvailable) return false;
        var result = await ProcessRunner.RunAsync(
            VsWherePath, $"-products * -requires {componentId} -property installationPath");
        return result.Success && !string.IsNullOrWhiteSpace(result.StdOut);
    }

    /// <summary>Checks whether a specific VS instance has a given component/workload installed.</summary>
    public static async Task<bool> HasComponentAsync(VsInstance instance, string componentId)
    {
        var result = await ProcessRunner.RunAsync(
            VsWherePath, $"-products * -requires {componentId} -property installationPath");
        if (!result.Success) return false;

        return result.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Any(line => string.Equals(line.Trim(), instance.InstallationPath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Returns the ids (first alternative of each unsatisfied requirement) missing from the instance.</summary>
    public static async Task<List<(VsRequirement Req, bool Installed)>> CheckRequirementsAsync(VsInstance instance)
    {
        var results = new List<(VsRequirement, bool)>();
        foreach (var req in Requirements)
        {
            var installed = false;
            foreach (var id in req.AnyOfIds)
            {
                if (await HasComponentAsync(instance, id)) { installed = true; break; }
            }
            results.Add((req, installed));
        }
        return results;
    }

    /// <summary>
    /// Launches the Visual Studio Installer to add the given components to an existing instance.
    /// The installer elevates itself (UAC) and shows its own progress UI.
    /// </summary>
    public static void LaunchModify(VsInstance instance, IEnumerable<string> componentIds)
    {
        var adds = string.Join(" ", componentIds.Select(id => $"--add {id}"));
        var args = $"modify --installPath \"{instance.InstallationPath}\" {adds} --includeRecommended --passive --norestart";
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = VsSetupPath,
            Arguments = args,
            UseShellExecute = true,
        });
    }

    /// <summary>Installs VS 2022 Community with all UE-required workloads via winget.</summary>
    public static Task<ProcessResult> InstallVsCommunityAsync(Action<string> onOutput, CancellationToken ct)
    {
        const string overrideArgs =
            "--add Microsoft.VisualStudio.Workload.NativeDesktop " +
            "--add Microsoft.VisualStudio.Workload.NativeGame " +
            "--add Microsoft.VisualStudio.Workload.ManagedDesktop " +
            "--add Microsoft.VisualStudio.Component.VC.Tools.x86.x64 " +
            "--add Microsoft.VisualStudio.Component.Windows11SDK.22621 " +
            "--includeRecommended --passive --wait";

        var args = "install --id Microsoft.VisualStudio.2022.Community --accept-source-agreements " +
                   $"--accept-package-agreements --override \"{overrideArgs}\"";
        return ProcessRunner.RunAsync("winget", args, onOutput: onOutput, ct: ct);
    }
}
