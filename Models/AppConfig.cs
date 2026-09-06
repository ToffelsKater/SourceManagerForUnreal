namespace UnrealManager.Models;

public sealed class AppConfig
{
    // Source
    public string GitRemoteUrl { get; set; } = "https://github.com/EpicGames/UnrealEngine.git";
    public string GitBranch { get; set; } = "release";
    public bool ShallowClone { get; set; } = true;
    public string EngineRoot { get; set; } = @"D:\UnrealEngine";

    /// <summary>Look up the selected engine's version against GitHub's release tags on startup and on engine change.</summary>
    public bool CheckEngineUpdates { get; set; } = true;

    // Build
    public string BuildTarget { get; set; } = "UnrealEditor";
    public string BuildConfiguration { get; set; } = "Development";
    public string BuildPlatform { get; set; } = "Win64";

    // Perforce
    public string P4Port { get; set; } = "ssl:perforce:1666";
    public string P4User { get; set; } = "";
    public string P4Client { get; set; } = "";
    public string P4Stream { get; set; } = "";
    public string P4SyncPath { get; set; } = "";
    public string P4Changelist { get; set; } = "";
    public bool P4ForceSync { get; set; }
    public bool P4ParallelSync { get; set; } = true;

    // Sync & Launch
    public bool StepSync { get; set; } = true;
    public bool StepBuild { get; set; } = true;
    public bool StepBuildProject { get; set; } = true;
    public bool StepLaunch { get; set; } = true;
    public string ProjectPath { get; set; } = "";
    public string LaunchArgs { get; set; } = "";
    public string ServerLaunchArgs { get; set; } = "-log";

    // Branches: the Perforce fields above are the *active* branch's values, kept in sync by ConfigService.
    public List<BranchProfile> Branches { get; set; } = [];
    public string ActiveBranch { get; set; } = "";
}
