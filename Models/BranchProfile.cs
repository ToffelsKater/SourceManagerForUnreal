namespace UnrealManager.Models;

/// <summary>
/// One branch of the game as it is worked on day to day: the stream it lives in, the workspace
/// that maps it, and everything downstream that changes with it (project, engine, launch args).
/// Switching branches is a single choice instead of five fields edited by hand — which also stops
/// the classic mistake of syncing one branch and launching the .uproject of another.
/// </summary>
public sealed class BranchProfile
{
    /// <summary>Display name, and the key the active branch is remembered by.</summary>
    public string Name { get; set; } = "";

    /// <summary>Stream depot path (e.g. //Game/Main). Empty means "leave the workspace where it is".</summary>
    public string Stream { get; set; } = "";

    public string Client { get; set; } = "";
    public string SyncPath { get; set; } = "";

    /// <summary>Changelist or label to sync to. Empty means head revision.</summary>
    public string Changelist { get; set; } = "";

    public string ProjectPath { get; set; } = "";

    /// <summary>Engine this branch builds against. Empty means "keep whatever engine is selected".</summary>
    public string EngineRoot { get; set; } = "";

    public string LaunchArgs { get; set; } = "";
}
