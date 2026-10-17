using System.Globalization;

namespace RimGovernor.Brain.Acceptance;

/// <summary>A check's verdict.</summary>
public enum DoctorStatus
{
    /// <summary>Nothing to do.</summary>
    Ok,

    /// <summary>The run starts but pays for it (a relaunch, a slow first poll) or its evidence is suspect (a binary behind main).</summary>
    Warn,

    /// <summary>The run cannot reach its first tick.</summary>
    Fail,
}

/// <summary>Heal codes a run's heal step acts on.</summary>
public static class DoctorHealCodes
{
    /// <summary>The installed mod's sources differ from the worktree.</summary>
    public const string StaleMod = "stale_mod";

    /// <summary>The build lacks a fixture the run's cases call.</summary>
    public const string MissingFixture = "missing_fixture";

    /// <summary>Harness processes outlive their removed worktrees.</summary>
    public const string Orphans = "orphans";
}

/// <summary>One line of the doctor's report.</summary>
public sealed class DoctorCheck
{
    public DoctorCheck(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public DoctorStatus Status { get; set; }

    /// <summary>What was found.</summary>
    public string Detail { get; set; } = "";

    /// <summary>What to do about it (empty on Ok).</summary>
    public string Fix { get; set; } = "";

    /// <summary>
    /// Names a Fail the runner can heal on its own (<see cref="DoctorHealCodes"/>); empty for every other check.
    /// </summary>
    public string Code { get; set; } = "";

    /// <summary>The processes an orphans check names.</summary>
    public IList<int> Pids { get; } = [];
}

/// <summary>What a run would use.</summary>
public sealed class DoctorOptions
{
    /// <summary>The worker root (<c>-root</c>), absolute.</summary>
    public string Root { get; set; } = "";

    /// <summary>The run's output directory; empty for the root's default.</summary>
    public string Output { get; set; } = "";

    /// <summary>The registry names the run would write under <see cref="Output"/>.</summary>
    public IReadOnlyList<string> Cases { get; set; } = [];

    /// <summary>The configured game id (<see cref="FileDoctorSetup.GameId"/> when empty).</summary>
    public string GameId { get; set; } = "";

    /// <summary>
    /// The test ops the run's cases call from their Start: the installed build must carry the fixtures registering them,
    /// and a stale build's rebuild hint names them.
    /// </summary>
    public IReadOnlyList<string> FixtureOps { get; set; } = [];

    /// <summary>
    /// The checkout the stale-package check compares against: the one enclosing the working directory, else the one
    /// enclosing <see cref="Root"/>.
    /// </summary>
    public string Repo { get; set; } = "";
}

/// <summary>The part of a root's game configuration the doctor reads.</summary>
public sealed record DoctorGameSection(string WorkingDir, string Target);

/// <summary>The part of an installed package's <c>native-manifest.json</c> the doctor reads: what the build was made from.</summary>
public sealed record DoctorPackageManifest(string Role, IReadOnlyList<string> Fixtures, string SourceRevision, bool SourceDirty);

/// <summary>Formatting shared by the doctor and prune output.</summary>
internal static class DoctorText
{
    public static string Short(string rev)
    {
        if (rev.Length > 8)
        {
            return rev[..8];
        }

        return rev.Length == 0 ? "unknown" : rev;
    }

    public static string Join(IEnumerable<string> items) => "[" + string.Join(' ', items) + "]";

    public static string Join(IEnumerable<int> items) => "[" + string.Join(' ', items.Select(i => i.ToString(CultureInfo.InvariantCulture))) + "]";
}
