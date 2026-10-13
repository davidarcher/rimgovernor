using System.Globalization;

namespace RimGovernor.Brain.Acceptance;

/// <summary>A check's verdict (Go <c>doctor.Status</c>).</summary>
public enum DoctorStatus
{
    /// <summary>Nothing to do.</summary>
    Ok,

    /// <summary>The run starts but pays for it (a relaunch, a slow first poll) or its evidence is suspect (a binary behind main).</summary>
    Warn,

    /// <summary>The run cannot reach its first tick.</summary>
    Fail,
}

/// <summary>Heal codes a run's heal step acts on (Go <c>doctor.Heal*</c>).</summary>
public static class DoctorHealCodes
{
    /// <summary>The installed mod's sources differ from the worktree.</summary>
    public const string StaleMod = "stale_mod";

    /// <summary>The build lacks a fixture the run's cases call.</summary>
    public const string MissingFixture = "missing_fixture";

    /// <summary>Harness processes outlive their removed worktrees.</summary>
    public const string Orphans = "orphans";
}

/// <summary>One line of the doctor's report (Go <c>doctor.Check</c>).</summary>
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

/// <summary>What a run would use (Go <c>doctor.Options</c>). The serve binary of the Go options is gone: the brain lives in the mod.</summary>
public sealed class DoctorOptions
{
    /// <summary>The worker root (<c>-root</c>), absolute.</summary>
    public string Root { get; set; } = "";

    /// <summary>The run's output directory; empty for the root's default.</summary>
    public string Output { get; set; } = "";

    /// <summary>The registry names the run would write under <see cref="Output"/>.</summary>
    public IReadOnlyList<string> Cases { get; set; } = [];

    /// <summary>The configured game id (<see cref="IDoctorSetup.GameId"/> when empty).</summary>
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

/// <summary>
/// What the doctor needs from the setup and launch layers (Go <c>setup.*</c> and the <c>nativeaccept</c> package install
/// helpers). Every member that reads or judges an installed package is here so the Setup/Lifecycle code owns its layout;
/// <see cref="FileDoctorSetup"/> is the self-contained default.
/// </summary>
public interface IDoctorSetup
{
    /// <summary>The configured game id by default.</summary>
    string GameId { get; }

    /// <summary>Setup's MAX_PATH bound on the game copy path.</summary>
    int MaxGameCopyPath { get; }

    /// <summary>The current store schema version (<c>service.sqlite</c> user_version).</summary>
    int StoreSchemaVersion { get; }

    /// <summary>The generated baseline save's file name.</summary>
    string BaselineSave { get; }

    /// <summary>The generated baseline save's name.</summary>
    string BaselineName { get; }

    /// <summary>The environment variable that skips the stale-package check.</summary>
    string AllowStaleModEnv { get; }

    /// <summary>The manifest file name at the package root.</summary>
    string PackageManifestName { get; }

    /// <summary>The checkout root enclosing <paramref name="dir"/>, or null.</summary>
    string? FindRepo(string dir);

    /// <summary>The game section of the root's configuration; throws <see cref="AcceptanceException"/> when it cannot be read.</summary>
    DoctorGameSection ReadGameSection(string root, string gameId);

    /// <summary>Validates the unified package under <paramref name="mods"/>; throws <see cref="AcceptanceException"/> when it is incomplete.</summary>
    void RequireNativePackage(string mods);

    /// <summary>Reads the package's manifest, or null when there is none.</summary>
    DoctorPackageManifest? ReadPackageManifest(string package);

    /// <summary>
    /// Fails (<see cref="AcceptanceException"/>) when the installed build's sources differ from the worktree's; returns
    /// why the check was skipped, or an empty string when it ran.
    /// </summary>
    string RequireCurrentPackage(string package, IReadOnlyList<string> fixtureOps);

    /// <summary>The sorted fixture list a rebuild for the run needs: installed plus those registering <paramref name="fixtureOps"/>.</summary>
    IReadOnlyList<string> FixtureFlags(string repo, IReadOnlyList<string> installed, IReadOnlyList<string> fixtureOps);

    /// <summary>The casefolded package ids of the expansions installed under the game copy.</summary>
    IReadOnlyList<string> InstalledExpansions(string gameCopy);

    /// <summary>The casefolded activeMods list of a ModsConfig.xml; throws when unreadable.</summary>
    IReadOnlyList<string> ActiveMods(string modsConfigPath);

    /// <summary>
    /// Why a kept process's launch record differs from the prepared state ("unrecorded", "expansions", another word for
    /// the package), or an empty string when it matches.
    /// </summary>
    string LaunchedMismatch(string headlessConfigDir);
}

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
