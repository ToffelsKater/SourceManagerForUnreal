using System.IO;
using System.Text.Json;
using UnrealManager.Models;

namespace UnrealManager.Services;

public static class ConfigService
{
    private static readonly string ConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UnrealManager");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppConfig Config { get; private set; } = Load();

    /// <summary>
    /// Raised after the selected engine changes. Every page listens, because which actions make
    /// sense depends on the kind of engine: a precompiled Epic Games Launcher build has no engine
    /// source to clone, set up or compile.
    /// </summary>
    public static event Action? EngineChanged;

    /// <summary>Points the whole app at another engine folder and tells every page to re-evaluate.</summary>
    public static void SetEngineRoot(string root)
    {
        if (string.Equals(Config.EngineRoot, root, StringComparison.Ordinal)) return;
        Config.EngineRoot = root;
        Save();
        EngineChanged?.Invoke();
    }

    /// <summary>
    /// Re-evaluates the engine without changing the path — for when the folder's contents changed
    /// under us (a clone finished, Setup.bat ran, an engine was uninstalled).
    /// </summary>
    public static void NotifyEngineChanged() => EngineChanged?.Invoke();

    private static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath)) ?? new AppConfig();
        }
        catch
        {
            // Corrupt config: fall back to defaults.
        }
        return new AppConfig();
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(Config, JsonOptions));
        }
        catch (Exception ex)
        {
            LogService.Instance.Log("WARNING: could not save config: " + ex.Message);
        }
    }
}
