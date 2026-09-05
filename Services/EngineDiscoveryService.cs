using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using UnrealManager.Models;

namespace UnrealManager.Services;

/// <summary>
/// Finds every Unreal Engine on this machine — the precompiled builds the Epic Games Launcher
/// installs as well as source trees — by reading the same places Epic's own UnrealVersionSelector
/// reads. Every candidate is verified on disk, because the launcher manifest keeps pointing at
/// folders long after they have been uninstalled.
/// </summary>
public static class EngineDiscoveryService
{
    /// <summary>The launcher's manifest of everything it installed (engines and marketplace content alike).</summary>
    private static string LauncherManifestPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat");

    /// <summary>Source builds first, then launcher builds, newest version first within each group.</summary>
    public static List<EngineInstall> Discover()
    {
        var found = new Dictionary<string, EngineInstall>(StringComparer.OrdinalIgnoreCase);

        AddFromLauncherManifest(found);
        AddFromLauncherRegistry(found);
        AddFromRegisteredBuilds(found);
        AddFromCommonFolders(found);

        return found.Values
            .OrderBy(e => e.Kind)
            .ThenByDescending(ParseVersion)
            .ThenBy(e => e.Root, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Version ParseVersion(EngineInstall install) =>
        Version.TryParse(install.Version, out var v) ? v : new Version(0, 0);

    /// <summary>Adds a candidate folder, ignoring duplicates and anything that is not really an engine.</summary>
    private static void Add(Dictionary<string, EngineInstall> found, string? root, string origin)
    {
        if (string.IsNullOrWhiteSpace(root)) return;

        var normalized = EngineInstall.Normalize(root);
        if (found.ContainsKey(normalized)) return;
        if (!EngineService.IsEngineRoot(normalized)) return;

        found[normalized] = new EngineInstall
        {
            Root = normalized,
            Kind = EngineService.DetectKind(normalized),
            Version = EngineService.ReadVersion(normalized) ?? "",
            Origin = origin,
        };
    }

    private static void AddFromLauncherManifest(Dictionary<string, EngineInstall> found)
    {
        try
        {
            if (!File.Exists(LauncherManifestPath)) return;

            using var doc = JsonDocument.Parse(File.ReadAllText(LauncherManifestPath));
            if (!doc.RootElement.TryGetProperty("InstallationList", out var list) ||
                list.ValueKind != JsonValueKind.Array)
                return;

            foreach (var entry in list.EnumerateArray())
            {
                // The manifest also lists marketplace content, whose InstallLocation is the engine
                // folder it was installed into. Engines are the entries in the "ue" namespace and
                // are named "UE_<version>".
                var ns = entry.TryGetProperty("NamespaceId", out var n) ? n.GetString() : null;
                var app = entry.TryGetProperty("AppName", out var a) ? a.GetString() : null;
                var isEngine = ns == "ue" || (app?.StartsWith("UE_", StringComparison.OrdinalIgnoreCase) ?? false);
                if (!isEngine) continue;

                if (entry.TryGetProperty("InstallLocation", out var location))
                    Add(found, location.GetString(), "Epic Games Launcher");
            }
        }
        catch
        {
            // A missing or malformed manifest simply means no launcher engines to report.
        }
    }

    /// <summary>Engines the launcher registered under HKLM\SOFTWARE\EpicGames\Unreal Engine\&lt;version&gt;.</summary>
    private static void AddFromLauncherRegistry(Dictionary<string, EngineInstall> found)
    {
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var key = hive.OpenSubKey(@"SOFTWARE\EpicGames\Unreal Engine");
                if (key is null) continue;

                foreach (var name in key.GetSubKeyNames())
                {
                    using var sub = key.OpenSubKey(name);
                    Add(found, sub?.GetValue("InstalledDirectory") as string, "Epic Games Launcher");
                }
            }
            catch
            {
                // Key absent or unreadable — nothing to add from this hive.
            }
        }
    }

    /// <summary>Source builds registered by UnrealVersionSelector (HKCU\Software\Epic Games\Unreal Engine\Builds).</summary>
    private static void AddFromRegisteredBuilds(Dictionary<string, EngineInstall> found)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Epic Games\Unreal Engine\Builds");
            if (key is null) return;

            foreach (var name in key.GetValueNames())
                Add(found, key.GetValue(name) as string, "Registered build");
        }
        catch
        {
            // Key absent or unreadable.
        }
    }

    /// <summary>
    /// Sweeps the launcher's default install folder and the folder each known engine lives in —
    /// engines are almost always kept side by side, and this catches ones the manifest forgot.
    /// </summary>
    private static void AddFromCommonFolders(Dictionary<string, EngineInstall> found)
    {
        var epicFolders = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            }
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => Path.Combine(p, "Epic Games"))
            .ToList();

        // Materialised before scanning, because Add mutates the dictionary we are reading from.
        var siblingFolders = found.Values
            .Select(e => Path.GetDirectoryName(e.Root))
            .Where(d => !string.IsNullOrEmpty(d))
            .Select(d => d!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Scan(epicFolders, "Epic Games folder");
        Scan(siblingFolders, "Next to another engine");

        void Scan(IEnumerable<string> folders, string origin)
        {
            foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (!Directory.Exists(folder)) continue;
                    foreach (var candidate in Directory.EnumerateDirectories(folder))
                        Add(found, candidate, origin);
                }
                catch
                {
                    // Unreadable folder — skip it.
                }
            }
        }
    }
}
