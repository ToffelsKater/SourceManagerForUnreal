using System.IO;

namespace UnrealManager.Models;

/// <summary>How an engine got onto the disk — this is what decides whether engine code can be compiled at all.</summary>
public enum EngineKind
{
    /// <summary>A GitHub source tree: ships Setup.bat and the full engine source, so the editor is compiled here.</summary>
    Source,

    /// <summary>
    /// A precompiled build installed by the Epic Games Launcher, marked by Engine/Build/InstalledBuild.txt.
    /// The editor is already built and no engine source is shipped, so engine compilation is impossible.
    /// </summary>
    Launcher,
}

/// <summary>One Unreal Engine installation found on this machine.</summary>
public sealed class EngineInstall : IEquatable<EngineInstall>
{
    public required string Root { get; init; }
    public required EngineKind Kind { get; init; }

    /// <summary>"5.8.1" when Engine/Build/Build.version could be read, otherwise empty.</summary>
    public string Version { get; init; } = "";

    /// <summary>Where this install turned up ("Epic Games Launcher", "Registered build", …).</summary>
    public string Origin { get; init; } = "";

    public bool IsLauncher => Kind == EngineKind.Launcher;

    public string KindLabel => Kind == EngineKind.Launcher ? "Epic Games Launcher" : "Source build";

    /// <summary>What the picker shows, e.g. "Unreal Engine 5.8.1  (Epic Games Launcher)  —  C:\UnrealEngines\UE_5.8".</summary>
    public string Display =>
        (string.IsNullOrEmpty(Version) ? "Unreal Engine" : "Unreal Engine " + Version)
        + $"  ({KindLabel})  —  {Root}";

    public bool Equals(EngineInstall? other) =>
        other is not null && string.Equals(Root, other.Root, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => Equals(obj as EngineInstall);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Root);

    public override string ToString() => Display;

    /// <summary>
    /// Full path without a trailing separator, so "C:\UE\", "C:/UE" and "C:\UE" are recognised
    /// as one install. Registry entries in particular use forward slashes.
    /// </summary>
    public static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path)
                       .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim().TrimEnd('\\', '/');
        }
    }
}
