using RimGovernor.Brain.Acceptance;

namespace RimGovernor.Launcher;

/// <summary>Artifact states the status panel shows.</summary>
public static class ArtifactState
{
    public const string Ok = "ok";
    public const string Stale = "stale";
    public const string Missing = "missing";
    public const string Building = "building";
    public const string Failed = "failed";
    public const string Pending = "pending";
}

/// <summary>One status row.</summary>
public sealed record Artifact(string Name, string State, string Detail);

/// <summary>The game states the Launch tab shows (the page's <c>controller</c> field).</summary>
public static class GameState
{
    public const string Stopped = "stopped";
    public const string Starting = "starting";
    public const string Running = "running";
}

/// <summary>What the page polls.</summary>
public sealed record LauncherView(
    IReadOnlyList<Artifact> Artifacts,
    string Controller,
    string Message,
    bool GameUp,
    IReadOnlyList<string> Log,
    Settings Settings,
    IReadOnlyList<string> Saves);

/// <summary>The installed production mod's state against the worktree's native source hash (Go <c>ModState</c>).</summary>
public static class ArtifactStates
{
    public static (string State, string Reason) ModState(SetupPackageManifest? manifest, string sourceTree)
    {
        if (manifest is null)
        {
            return (ArtifactState.Missing, "no installed build");
        }

        if (manifest.SourceTree != sourceTree)
        {
            return (ArtifactState.Stale, "installed build's sources differ from the worktree");
        }

        if (manifest.Fixtures.Count > 0)
        {
            return (ArtifactState.Stale, "installed build is a fixture build");
        }

        return (ArtifactState.Ok, "installed build is current");
    }
}
