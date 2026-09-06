using System.IO;

namespace UnrealManager.Models;

/// <summary>One .uproject found on this machine.</summary>
public sealed class UnrealProject : IEquatable<UnrealProject>
{
    /// <summary>Full path to the .uproject file.</summary>
    public required string Path { get; init; }

    /// <summary>
    /// The project's "EngineAssociation": a version like "5.6" for a launcher engine, a
    /// {GUID} for a registered source build, or empty for a project living inside an engine tree.
    /// </summary>
    public string EngineAssociation { get; init; } = "";

    /// <summary>Where this project turned up ("Recently opened", "Unreal Projects folder", …).</summary>
    public string Origin { get; init; } = "";

    /// <summary>When the editor last opened it, when a recent-projects list said so.</summary>
    public DateTime? LastOpened { get; init; }

    /// <summary>
    /// The "no project" entry: the Sync &amp; Launch tab can open the editor on its own, so the
    /// picker needs a way back to nothing once a project has been chosen.
    /// </summary>
    public static UnrealProject None { get; } = new() { Path = "", Origin = "No project" };

    /// <summary>True for <see cref="None"/> — nothing to read off disk, nothing to match an engine to.</summary>
    public bool IsNone => Path.Length == 0;

    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);

    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";

    /// <summary>
    /// The engine on this PC this project belongs to, worked out by
    /// <c>ProjectDiscoveryService</c> — null when none of the installed engines fit.
    /// </summary>
    public EngineInstall? ResolvedEngine { get; internal set; }

    /// <summary>The engine the project asks for, in the .uproject's own terms.</summary>
    public string WantedEngineDescription =>
        EngineAssociation.Length == 0 ? "an engine folder above the project"
        : EngineAssociation.StartsWith('{') ? "a registered source build"
        : "Unreal Engine " + EngineAssociation;

    /// <summary>
    /// Short description of the engine for the picker: the install actually matched (two engines
    /// can share a version, so the kind matters), or what the project asked for when none fits.
    /// </summary>
    public string EngineLabel
    {
        get
        {
            if (ResolvedEngine is null) return WantedEngineDescription + ", not installed";
            var version = ResolvedEngine.Version.Length == 0 ? "" : " " + ResolvedEngine.Version;
            return $"UE{version}, {ResolvedEngine.KindLabel.ToLowerInvariant()}";
        }
    }

    /// <summary>What the picker shows, e.g. "Survivors  (UE 5.7.4, epic games launcher)  —  D:\…\Survivors.uproject".</summary>
    public string Display => IsNone
        ? "(no project — just launch the editor)"
        : $"{Name}  ({EngineLabel})  —  {Path}";

    public bool Equals(UnrealProject? other) =>
        other is not null && string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => Equals(obj as UnrealProject);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Path);

    public override string ToString() => Display;

    /// <summary>Full path with native separators, so registry and .ini spellings collapse onto one entry.</summary>
    public static string Normalize(string path)
    {
        try
        {
            return System.IO.Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch
        {
            return path.Trim().Trim('"');
        }
    }
}
