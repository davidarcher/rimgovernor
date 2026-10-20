using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The game profile preparation of the acceptance harness: the disposable worker root's
/// ModsConfig.xml, Prefs.xml, launch arguments and config.json, the launch snapshots that tell a kept process what it
/// runs with, and the save-header expansion reader. The native package validation and manifest reading are
/// <see cref="SetupPackage"/>'s; the expansion naming is <see cref="Expansions"/>'s.
/// </summary>
public static partial class HeadlessProfile
{
    /// <summary>The unified native mod's package ID.</summary>
    public const string NativePackage = SetupPackage.NativePackage;

    /// <summary>The game id the profile preparers hard-code in config.json.</summary>
    public const string TrialGameId = "rimgovernor-trial";

    /// <summary>
    /// The snapshot of the profile's ModsConfig.xml taken right before a fresh launch, in the save-data folder.
    /// ModsConfig.xml only applies at launch and every Prepare rewrites it, so this copy is the one record of which mods
    /// a kept process is actually running with.
    /// </summary>
    public const string LaunchedModsFile = "RimGovernorLaunchedMods.xml";

    /// <summary>
    /// The snapshot of the installed package's file hashes (<see cref="Harness.RelaunchFiles"/>: every shipped assembly,
    /// not just the stamp files) taken right before a fresh launch, beside
    /// <see cref="LaunchedModsFile"/>. The process runs the DLLs it loaded at launch, so this is the record of which
    /// native build a kept process serves.
    /// </summary>
    public const string LaunchedPackageFile = "RimGovernorLaunchedPackage.json";

    /// <summary>
    /// The profile-directory file recording the environment entries (beyond the channel directory and run id) a process
    /// was launched with: the game reads its environment once, so a kept process whose entries differ from what a case
    /// wants cannot be reused (<see cref="LaunchedEnvMismatch"/>), and the readers of the brain's state database find it
    /// at the recorded <c>RIMGOVERNOR_STATE</c> (<see cref="LaunchedEnvValue"/>).
    /// </summary>
    public const string LaunchedEnvFile = "RimGovernorLaunchedEnv.json";

    /// <summary>The environment variable naming the brain's state database (<c>InProcEnvironment.State</c>).</summary>
    public const string StateVariable = "RIMGOVERNOR_STATE";

    /// <summary>
    /// The game launch flag that asks the mod to host the brain in-process (<c>InProcLaunch.Flag</c> of Brain.Host, which
    /// this assembly does not reference). Every harness profile and the launcher carry it: in-process is the only launch
    /// mode since the cutover (#3085).
    /// </summary>
    public const string InProcArg = "-rimgovernor-inproc";

    /// <summary>The variable that turns the host's auto-resume and layout overlay off (<c>InProcEnvironment.Observe</c>).</summary>
    public const string ObserveVariable = "RIMGOVERNOR_OBSERVE";

    /// <summary>
    /// The variable that keeps a test-acceleration launch's brain at the player's speed instead of pinning Hyperfast
    /// (<c>InProcEnvironment.UnpinTestSpeed</c>): the speed matrix sets it so each row's Run asks for the row's speed.
    /// </summary>
    public const string UnpinTestSpeedVariable = "RIMGOVERNOR_UNPIN_TEST_SPEED";

    /// <summary>
    /// Makes a harness-launched game quit after 20 minutes without a call (native IdleExit), so a warm game kept at the
    /// main menu that no run comes back to does not linger. Only acceptance launches carry it (root/config-headless and
    /// root/config-rendered); the player's root/config, which the launcher's Play reads, never does.
    /// </summary>
    public const string IdleExitArg = "-rimgovernor-idle-exit=20";

    /// <summary>Split/superseded native package IDs that must never coexist with the unified package in a worker's ModsConfig.xml.</summary>
    public static readonly IReadOnlyList<string> LegacyPackages =
    [
        "davidarcher.rimgovernor.observations",
        "brrainz.rimbridgeserver",
        "redeyedev.headlessrim",
    ];

    private static readonly string[] ExpansionOrder = ["royalty", "ideology", "biotech", "anomaly", "odyssey"];

    [GeneratedRegex(@"<li>\s*([^<\s]+)\s*</li>", RegexOptions.CultureInvariant)]
    private static partial Regex SaveModItem();

    private static string Casefold(string s) => s.ToLowerInvariant();

    /// <summary>The trimmed packageId of an About.xml.</summary>
    internal static string PackageId(string aboutXmlPath)
    {
        try
        {
            var (_, root) = XmlTree.ParseFile(aboutXmlPath);
            return root.ChildText("packageId").Trim();
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"parse {aboutXmlPath}: {e.Message}", e);
        }
    }

    /// <summary>An official expansion's release position; unknown packages sort after them in the order given.</summary>
    private static int ExpansionRank(string id)
    {
        for (var i = 0; i < ExpansionOrder.Length; i++)
        {
            if (string.Equals(id, Expansions.ExpansionPrefix + ExpansionOrder[i], StringComparison.Ordinal))
            {
                return i;
            }
        }

        return 1 << 30;
    }

    /// <summary>
    /// Rewrites ModsConfig.xml's activeMods to the core game, only the requested expansions, and exactly brrainz.harmony
    /// and the native package, removing every other official expansion plus legacy or duplicate entries first. The
    /// profile preparers pass every installed expansion when a run names none.
    /// <para>
    /// knownExpansions is extended with <paramref name="installed"/> (the official expansions the game copy ships) and the
    /// requested ones. RimWorld activates every installed expansion it has not seen before at boot, knownExpansions being
    /// its record of which it has, and rewrites ModsConfig.xml with them, so a profile whose list leaves one out runs
    /// every DLC the game owns whatever activeMods says, and a kept process's launch snapshot no longer matches the file.
    /// </para>
    /// </summary>
    public static void PrepareNativeModConfig(string modsConfigXmlPath, IReadOnlyList<string> installed, params string[] expansions)
    {
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(expansions);
        string header;
        XElement root;
        try
        {
            (header, root) = XmlTree.ParseFile(modsConfigXmlPath);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"read ModsConfig.xml: {e.Message}", e);
        }

        var active = root.Element("activeMods") ?? throw new AcceptanceException("native profile is missing activeMods");
        string[] required = ["brrainz.harmony", NativePackage];
        var drop = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in required)
        {
            drop.Add(Casefold(id));
        }

        foreach (var id in LegacyPackages)
        {
            drop.Add(Casefold(id));
        }

        var keep = new HashSet<string>(StringComparer.Ordinal);
        var wanted = new List<string>();
        foreach (var name in expansions)
        {
            var id = Expansions.ExpansionPackage(name);
            if (keep.Add(id))
            {
                wanted.Add(id);
            }
        }

        // Release order, as RimWorld's own mod manager loads them: a later expansion's defs inherit from an earlier one's
        // abstract parents (Odyssey's VacskinGland from Royalty's BodyPartBionicImperialBase), and loaded out of order the
        // child keeps a null thingClass and every new Game() NREs in ReadingPolicyDatabase.
        wanted = [.. wanted.OrderBy(ExpansionRank)];
        var kept = new List<XNode>();
        var seenCore = false;
        foreach (var kid in active.Nodes().ToList())
        {
            if (kid is XElement { Name.LocalName: "li" } li)
            {
                var id = Casefold(li.DirectText());
                if (drop.Contains(id) || id.StartsWith(Expansions.ExpansionPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                if (id == Expansions.CorePackage)
                {
                    if (seenCore)
                    {
                        continue;
                    }

                    seenCore = true;

                    // Harmony loads before the core game, as every player's list has it; a save recorded in another order
                    // prompts "the order of your mod list has changed" on load.
                    kept.Add(Item(required[0]));
                    kept.Add(li);

                    // Expansions load directly after the core game, as RimWorld's own mod manager orders them.
                    foreach (var expansion in wanted)
                    {
                        kept.Add(Item(expansion));
                    }

                    continue;
                }
            }

            kept.Add(kid);
        }

        if (!seenCore)
        {
            throw new AcceptanceException($"native profile activeMods is missing {Expansions.CorePackage}");
        }

        active.RemoveNodes();
        active.Add(kept);
        foreach (var id in required.Skip(1))
        {
            active.Add(Item(id));
        }

        var known = root.Element("knownExpansions");
        if (known is null)
        {
            known = new XElement("knownExpansions");
            root.Add(known);
        }

        var seenKnown = new HashSet<string>(StringComparer.Ordinal);
        foreach (var li in known.Elements("li"))
        {
            seenKnown.Add(Casefold(li.DirectText()));
        }

        foreach (var raw in installed.Concat(wanted))
        {
            var id = Casefold(raw);
            if (seenKnown.Add(id))
            {
                known.Add(Item(id));
            }
        }

        File.WriteAllBytes(modsConfigXmlPath, new UTF8Encoding(false).GetBytes(XmlTree.Write(header, root)));
    }

    private static XElement Item(string id) => new("li", id);

    /// <summary>
    /// Lists the casefolded package IDs of the official expansions installed under <paramref name="workingDir"/>'s Data
    /// folder (Data/&lt;name&gt;/About/About.xml with a ludeon.rimworld.* packageId), sorted. A game copy with no Data
    /// folder has none.
    /// </summary>
    public static IReadOnlyList<string> InstalledExpansions(string workingDir)
    {
        var data = Path.Combine(workingDir, "Data");
        var result = new List<string>();
        if (!Directory.Exists(data))
        {
            return result;
        }

        foreach (var dir in Directory.EnumerateDirectories(data))
        {
            var about = Path.Combine(dir, "About", "About.xml");
            if (!File.Exists(about))
            {
                continue;
            }

            var folded = Casefold(PackageId(about));
            if (folded.StartsWith(Expansions.ExpansionPrefix, StringComparison.Ordinal))
            {
                result.Add(folded);
            }
        }

        result.Sort(StringComparer.Ordinal);
        return result;
    }

    /// <summary>
    /// Enforces launchMode == DirectPath and strips stopProcessName so the launcher stops the process it recorded instead
    /// of matching any process by name.
    /// </summary>
    public static void RequireOwnedLaunch(HarnessGame game)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (game.LaunchMode != "DirectPath")
        {
            throw new AcceptanceException("disposable profiles require DirectPath PID-owned launches");
        }

        game.StopProcessName = null;
    }

    /// <summary>Reads a config.json.</summary>
    public static HarnessConfig LoadConfig(string path)
    {
        try
        {
            return LaunchJson.ParseConfig(File.ReadAllBytes(path));
        }
        catch (JsonException e)
        {
            throw new AcceptanceException($"parse {path}: {e.Message}", e);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new AcceptanceException($"read {path}: {e.Message}", e);
        }
    }

    /// <summary>Writes a config.json indented.</summary>
    public static void WriteConfig(string path, HarnessConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(LaunchJson.Config(config)));
    }

    /// <summary>The <c>games.&lt;gameId&gt;</c> section of a config.json (the preparers hard-code <see cref="TrialGameId"/>).</summary>
    public static HarnessGame GameSection(HarnessConfig config, string gameId = TrialGameId)
    {
        ArgumentNullException.ThrowIfNull(config);
        var games = config.Games ?? throw new AcceptanceException("configuration is missing games");
        return games.TryGetValue(gameId, out var game) && game is not null ? game : throw new AcceptanceException($"configuration is missing games.{gameId}");
    }

    /// <summary>
    /// The profile directory (-savedatafolder) the game that <paramref name="configDir"/>'s config.json launches reads its
    /// Config/ModsConfig.xml and saves from: root/headless-profile for Prepare (root/config-headless), root/profile for
    /// PrepareRendered (root/config-rendered) and for the player's root/config.
    /// </summary>
    public static string SaveDataFolder(string configDir)
    {
        var path = Path.Combine(MustAbs(configDir), "config.json");
        var game = GameSection(LoadConfig(path));
        foreach (var value in game.Args ?? [])
        {
            if (value.StartsWith("-savedatafolder=", StringComparison.Ordinal))
            {
                return value["-savedatafolder=".Length..];
            }
        }

        throw new AcceptanceException($"{Path.Combine(configDir, "config.json")}: the game's launch arguments carry no -savedatafolder");
    }

    /// <summary>
    /// Snapshots the profile's ModsConfig.xml to <see cref="LaunchedModsFile"/>. Called only right before a fresh launch;
    /// a missing config or profile is not an error.
    /// </summary>
    public static void RecordLaunchedMods(string configDir)
    {
        string profile;
        try
        {
            profile = SaveDataFolder(configDir);
        }
        catch (AcceptanceException)
        {
            return;
        }

        var source = Path.Combine(profile, "Config", "ModsConfig.xml");
        if (!File.Exists(source))
        {
            return;
        }

        CopyFile(source, Path.Combine(profile, LaunchedModsFile));
    }

    /// <summary>
    /// Reads the casefolded activeMods list of a ModsConfig.xml. A missing file raises the file system's own
    /// <see cref="FileNotFoundException"/> or <see cref="DirectoryNotFoundException"/>, which callers tell apart from a bad file.
    /// </summary>
    public static IReadOnlyList<string> ActiveMods(string path)
    {
        XElement root;
        try
        {
            (_, root) = XmlTree.Parse(File.ReadAllText(path, new UTF8Encoding(false)));
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"parse {path}: {e.Message}", e);
        }

        var active = root.Element("activeMods") ?? throw new AcceptanceException($"{path}: missing activeMods");
        return [.. active.Elements("li").Select(li => Casefold(li.DirectText()))];
    }

    /// <summary>The official expansions a ModsConfig.xml activates, casefolded, in its load order.</summary>
    public static IReadOnlyList<string> ActiveExpansions(string modsConfigXmlPath) =>
        [.. ActiveMods(modsConfigXmlPath).Where(id => id.StartsWith(Expansions.ExpansionPrefix, StringComparison.Ordinal))];

    /// <summary>
    /// Snapshots the installed package's hashes to <see cref="LaunchedPackageFile"/>. Called only right before a fresh
    /// launch, like <see cref="RecordLaunchedMods"/>; a missing config, profile or package is not an error.
    /// </summary>
    public static void RecordLaunchedPackage(string configDir)
    {
        string profile;
        IReadOnlyDictionary<string, string> files;
        try
        {
            profile = SaveDataFolder(configDir);
            files = LaunchedPackageFiles(configDir);
        }
        catch (AcceptanceException)
        {
            return;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var snapshot = files.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        File.WriteAllBytes(Path.Combine(profile, LaunchedPackageFile), new UTF8Encoding(false).GetBytes(LaunchJson.Snapshot(snapshot)));
    }

    /// <summary>Hashes every shipped file of the package installed under the game's working directory in <paramref name="configDir"/>'s config.json.</summary>
    private static IReadOnlyDictionary<string, string> LaunchedPackageFiles(string configDir)
    {
        var path = Path.Combine(MustAbs(configDir), "config.json");
        var workingDir = GameSection(LoadConfig(path)).WorkingDir ?? "";
        if (workingDir.Length == 0)
        {
            throw new AcceptanceException($"{Path.Combine(configDir, "config.json")}: the game has no workingDir");
        }

        return Harness.RelaunchFiles(workingDir);
    }

    /// <summary>Snapshots the environment entries a fresh launch adds to <see cref="LaunchedEnvFile"/>; a missing profile is not an error.</summary>
    public static void RecordLaunchedEnv(string configDir, IReadOnlyDictionary<string, string> env)
    {
        string profile;
        try
        {
            profile = SaveDataFolder(configDir);
        }
        catch (AcceptanceException)
        {
            return;
        }

        var snapshot = env.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        Directory.CreateDirectory(profile);
        File.WriteAllBytes(Path.Combine(profile, LaunchedEnvFile), new UTF8Encoding(false).GetBytes(LaunchJson.Snapshot(snapshot)));
    }

    /// <summary>The environment a process was launched with (<see cref="RecordLaunchedEnv"/>), or null when none was recorded.</summary>
    public static IReadOnlyDictionary<string, string>? ReadLaunchedEnv(string configDir)
    {
        try
        {
            var path = Path.Combine(SaveDataFolder(configDir), LaunchedEnvFile);
            return File.Exists(path) ? LaunchJson.ParseSnapshot(File.ReadAllText(path, new UTF8Encoding(false))) : null;
        }
        catch (Exception e) when (e is AcceptanceException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>One recorded launch-environment value, "" when absent.</summary>
    public static string LaunchedEnvValue(string configDir, string name) =>
        ReadLaunchedEnv(configDir) is { } env && env.TryGetValue(name, out var value) ? value : "";

    /// <summary>
    /// "env" when the process was launched without an entry <paramref name="wanted"/> asks for (or with another value),
    /// else "". The state database path (<see cref="StateVariable"/>) differs per run by design, so it never forces a
    /// relaunch: the readers follow the recorded path instead. So does the crash dump path
    /// (<see cref="CrashDump.NameVariable"/>): a kept process dumps into the case that launched it, and the startup crash
    /// it is for happens in that case.
    /// </summary>
    public static string LaunchedEnvMismatch(string configDir, IReadOnlyDictionary<string, string> wanted)
    {
        var launched = ReadLaunchedEnv(configDir) ?? new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in wanted)
        {
            if (name is StateVariable or CrashDump.NameVariable)
            {
                continue;
            }

            if (!launched.TryGetValue(name, out var have) || !string.Equals(have, value, StringComparison.Ordinal))
            {
                return "env";
            }
        }

        return "";
    }

    /// <summary>
    /// Compares what a kept process was launched with (its <see cref="LaunchedModsFile"/> and
    /// <see cref="LaunchedPackageFile"/> snapshots) to the profile's freshly prepared ModsConfig.xml and the package now
    /// installed. It returns "" when both match, so the process can be reused, or a short reason to relaunch:
    /// "expansions" when the mod lists differ (a Core-only process cannot load a save recorded with DLC and fails
    /// save.missing_mods at once), "package" when the installed package's hashes changed (a rebuilt mod installed under a
    /// kept process still serves the old DLL's tool catalog) and "unrecorded" when either snapshot is missing (a process
    /// an older binary or a hand launch started). The mod comparison is on the whole load order, since that is what the
    /// process is bound to.
    /// </summary>
    public static string LaunchedMismatch(string configDir)
    {
        var profile = SaveDataFolder(configDir);
        var wanted = ActiveMods(Path.Combine(profile, "Config", "ModsConfig.xml"));
        IReadOnlyList<string> launched;
        try
        {
            launched = ActiveMods(Path.Combine(profile, LaunchedModsFile));
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return "unrecorded";
        }

        if (!string.Equals(string.Join('\n', wanted), string.Join('\n', launched), StringComparison.Ordinal))
        {
            return "expansions";
        }

        string text;
        try
        {
            text = File.ReadAllText(Path.Combine(profile, LaunchedPackageFile), new UTF8Encoding(false));
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return "unrecorded";
        }

        Dictionary<string, string> launchedFiles;
        try
        {
            launchedFiles = LaunchJson.ParseSnapshot(text);
        }
        catch (JsonException e)
        {
            throw new AcceptanceException($"parse {LaunchedPackageFile}: {e.Message}", e);
        }

        var installed = LaunchedPackageFiles(configDir);
        if (installed.Count != launchedFiles.Count)
        {
            return "package";
        }

        foreach (var (name, sum) in installed)
        {
            if (!launchedFiles.TryGetValue(name, out var launchedSum) || launchedSum != sum)
            {
                return "package";
            }
        }

        return "";
    }

    /// <summary>
    /// Whether a RimWorld process for the game is already up under <paramref name="stateDir"/>: the next launch attaches to it rather than starting one. There is no shared-running state any
    /// more; the recorded process is the one.
    /// </summary>
    public static bool GameRunning(string stateDir, string gameId) => GameProcess.Attach(stateDir, gameId) is not null;

    internal static string MustAbs(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    /// <summary>
    /// Builds a fresh disposable worker root: it copies config.json and the profile Config/Saves files into a brand-new
    /// directory. <paramref name="destination"/> must not already exist.
    /// </summary>
    public static string IsolatedRoot(string source, string destination)
    {
        source = MustAbs(source);
        destination = MustAbs(destination);
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            throw new AcceptanceException("worker root already exists; use a fresh directory");
        }

        var config = LoadConfig(Path.Combine(source, "config", "config.json"));
        var game = GameSection(config);
        RequireOwnedLaunch(game);
        Directory.CreateDirectory(Path.Combine(destination, "config"));
        WriteConfig(Path.Combine(destination, "config", "config.json"), config);
        var required = new List<string>
        {
            Path.Combine("profile", "Config", "Prefs.xml"),
            Path.Combine("profile", "Config", "ModsConfig.xml"),
        };

        // The baseline is generated on first use (Config.EnsureSaveAsync), so a source root that has not run a case yet has
        // none to hand the worker; the worker then generates its own. A present baseline travels with its stamp.
        foreach (var name in Baselines.StampedNames())
        {
            if (File.Exists(Baselines.StartSavePath(source, name)))
            {
                required.Add(Path.Combine("profile", "Saves", name + ".rws"));
                if (File.Exists(Baselines.StartStampPath(source, name)))
                {
                    required.Add(Path.Combine("profile", "Saves", name + Baselines.StampSuffix));
                }
            }
        }

        // The cached lab and debug starts travel too, the lab's with its stamp and recorded reply, so a worker loads them
        // instead of generating its own; LabStarts and the start cache still judge each copy's freshness.
        var saves = Path.Combine(source, "profile", "Saves");
        if (Directory.Exists(saves))
        {
            foreach (var file in Directory.EnumerateFiles(saves).Select(Path.GetFileName).Order(StringComparer.Ordinal))
            {
                if (IsCachedStartFile(file!))
                {
                    required.Add(Path.Combine("profile", "Saves", file!));
                }
            }
        }

        foreach (var relative in required)
        {
            CopyFile(Path.Combine(source, relative), Path.Combine(destination, relative));
        }

        return destination;
    }

    /// <summary>Whether a profile/Saves file belongs to a cached lab start (save, stamp, reply) or a cached debug start (save).</summary>
    private static bool IsCachedStartFile(string file) =>
        (file.StartsWith(LabStarts.LabPrefix, StringComparison.Ordinal)
            && (file.EndsWith(".rws", StringComparison.Ordinal) || file.EndsWith(LabStarts.StampSuffix, StringComparison.Ordinal) || file.EndsWith(LabStarts.ReplySuffix, StringComparison.Ordinal)))
        || (file.StartsWith(ProfileStartCache.DebugPrefix, StringComparison.Ordinal) && file.EndsWith(".rws", StringComparison.Ordinal));

    /// <summary>Copies one regular file, creating the destination directory and replacing the destination.</summary>
    public static void CopyFile(string source, string destination) => Checkpoints.CopyFile(source, destination);

    /// <summary>
    /// Launches the unified native package without -Headless flags, for a visible window: it reads the root's base
    /// root/config/config.json, writes the acceptance args into root/config-rendered/config.json and returns that
    /// directory, so the game's records and endpoint live under config-rendered. root/config stays the player's: the
    /// launcher's Play launches from it, and an acceptance-only arg written there would leak into every later Play.
    /// -savedatafolder stays root/profile, so a rendered run sees the player's saves; its Config/Prefs.xml and Config/ModsConfig.xml, and the launch snapshots beside them, are a known
    /// shared surface this rewrites for the run. <paramref name="fixtureOps"/> feed the stale-package check's rebuild
    /// hint (<see cref="Config.FixtureOps"/>).
    /// </summary>
    public static string PrepareRendered(string root, IReadOnlyList<string> fixtureOps, IReadOnlyList<string>? expansions)
    {
        root = MustAbs(root);
        var config = LoadConfig(Path.Combine(root, "config", "config.json"));
        var game = GameSection(config);
        RequireOwnedLaunch(game);
        var workingDir = game.WorkingDir ?? "";
        SetupPackage.RequireNativePackage(Path.Combine(workingDir, "Mods"));
        StalePackage.RequireCurrent(Path.Combine(workingDir, "Mods", "RimGovernor"), fixtureOps);
        var installed = InstalledExpansions(workingDir);
        expansions ??= installed;
        var profile = Path.Combine(root, "profile");
        PrepareNativeModConfig(Path.Combine(profile, "Config", "ModsConfig.xml"), installed, [.. expansions]);
        ProfilePrefs.SetPrefs(Path.Combine(profile, "Config", "Prefs.xml"), ProfilePrefs.RenderedPrefs);
        game.Args =
        [
            "-savedatafolder=" + profile,
            "-NoFileLog",
            "-DisableLogStackTraces",
            "-screen-fullscreen",
            "0",
            "-screen-width",
            "1280",
            "-screen-height",
            "720",
            "-rimgovernor-pause-on-load",
            "-rimgovernor-test-acceleration",
            InProcArg,
            IdleExitArg,
        ];
        var destination = Path.Combine(root, "config-rendered");
        Directory.CreateDirectory(destination);
        WriteConfig(Path.Combine(destination, "config.json"), config);
        return destination;
    }

    /// <summary>
    /// Builds the headless-profile subdirectory (copying Prefs.xml/ModsConfig.xml and every profile/Saves/*.rws save into
    /// it, Prefs.xml trimmed per <see cref="ProfilePrefs.HeadlessPrefs"/>), rewrites config.json's args for -Headless,
    /// writes config-headless/config.json, and returns that directory. <paramref name="fixtureOps"/> feed the
    /// stale-package check's rebuild hint (<see cref="Config.FixtureOps"/>).
    /// </summary>
    public static string Prepare(string root, IReadOnlyList<string> fixtureOps, IReadOnlyList<string>? expansions, bool graphics)
    {
        root = MustAbs(root);
        var config = LoadConfig(Path.Combine(root, "config", "config.json"));
        var game = GameSection(config);
        RequireOwnedLaunch(game);
        var workingDir = game.WorkingDir ?? "";
        SetupPackage.RequireNativePackage(Path.Combine(workingDir, "Mods"));
        StalePackage.RequireCurrent(Path.Combine(workingDir, "Mods", "RimGovernor"), fixtureOps);
        var profile = Path.Combine(root, "headless-profile");
        Directory.CreateDirectory(Path.Combine(profile, "Config"));
        Directory.CreateDirectory(Path.Combine(profile, "Saves"));
        foreach (var name in new[] { "Prefs.xml", "ModsConfig.xml" })
        {
            CopyFile(Path.Combine(root, "profile", "Config", name), Path.Combine(profile, "Config", name));
        }

        var installed = InstalledExpansions(workingDir);
        expansions ??= installed;
        PrepareNativeModConfig(Path.Combine(profile, "Config", "ModsConfig.xml"), installed, [.. expansions]);
        ProfilePrefs.TrimPrefs(Path.Combine(profile, "Config", "Prefs.xml"));

        // Every save under profile/Saves, not just the tribal8 baseline, so a headless run (the default for the native
        // acceptance binaries) can load any variant save deposited there, the same way a rendered run already can
        // (PrepareRendered points RimWorld straight at profile/Saves with no copy step). The baseline is generated into
        // profile/Saves on first use (Config.EnsureSaveAsync).
        var saves = Path.Combine(root, "profile", "Saves");
        if (Directory.Exists(saves))
        {
            foreach (var save in Directory.EnumerateFiles(saves).Where(f => f.EndsWith(".rws", StringComparison.OrdinalIgnoreCase)))
            {
                CopyFile(save, Path.Combine(profile, "Saves", Path.GetFileName(save)));
            }
        }

        // -rimgovernor-test-acceleration is the native gate for clock test acceleration; both acceptance profiles (this
        // one and PrepareRendered, each in its own config directory) carry it, a player launch from root/config never does. 1.7 ignores -logFile and writes one Player.log at
        // a fixed LocalLow path shared by every instance, so -NoFileLog stops that write: the per-instance log is the
        // stdout the launcher captures to <config>/<game>/game.log. -DisableLogStackTraces bounds that capture (about
        // 15 MB a minute with stacks); message lines, exceptions included, stay.
        var args = new List<string>
        {
            "-savedatafolder=" + profile,
            "-NoFileLog",
            "-DisableLogStackTraces",
            "-Headless",
            "-rimgovernor-pause-on-load",
            "-rimgovernor-test-acceleration",
            InProcArg,
            IdleExitArg,
        };
        if (graphics)
        {
            args.RemoveAll(a => a == "-Headless");
        }

        game.Args = args;
        var destination = Path.Combine(root, "config-headless");
        Directory.CreateDirectory(destination);
        WriteConfig(Path.Combine(destination, "config.json"), config);
        return destination;
    }

    /// <summary>
    /// Reads the official expansions a save at <c>root/profile/Saves/&lt;saveName&gt;.rws</c> was recorded with (its
    /// modIds header). A save refuses to load (save.missing_mods) under a profile missing any of them, so a harness that
    /// loads a save passes its result to <see cref="Config.Expansions"/> rather than relying on the all-installed default.
    /// </summary>
    public static IReadOnlyList<string> SaveExpansions(string root, string saveName) =>
        SaveExpansionsAt(Path.Combine(MustAbs(root), "profile", "Saves", saveName + ".rws"), saveName);

    /// <summary><see cref="SaveExpansions"/> on the .rws at <paramref name="path"/>.</summary>
    public static IReadOnlyList<string> SaveExpansionsAt(string path, string saveName)
    {
        string text;
        try
        {
            using var stream = File.OpenRead(path);

            // modIds sit in the first few hundred bytes of the meta block; 64 KiB bounds the scan of a multi-megabyte save.
            var head = new byte[64 * 1024];
            var n = 0;
            int read;
            while (n < head.Length && (read = stream.Read(head, n, head.Length - n)) > 0)
            {
                n += read;
            }

            text = Encoding.UTF8.GetString(head, 0, n);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new AcceptanceException($"read save header: {e.Message}", e);
        }

        var start = text.IndexOf("<modIds>", StringComparison.Ordinal);
        var end = text.IndexOf("</modIds>", StringComparison.Ordinal);
        if (start < 0 || end < start)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"save {saveName} has no modIds header in its first {text.Length} bytes"));
        }

        var result = new List<string>();
        foreach (Match m in SaveModItem().Matches(text[start..end]))
        {
            var id = Casefold(m.Groups[1].Value);
            if (id.StartsWith(Expansions.ExpansionPrefix, StringComparison.Ordinal))
            {
                result.Add(id);
            }
        }

        return result;
    }
}
