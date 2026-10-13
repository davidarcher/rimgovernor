using System.Buffers.Binary;
using System.Globalization;
using System.Reflection;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The acceptance preflight (Go <c>doctor</c> package): every environment pitfall that has cost a run ten minutes
/// before it surfaced, checked in a few seconds against a worker root. Each check names its fix rather than refusing;
/// only a check that would certainly fail a run is Fail, the rest Warn (a relaunch, a slow first poll, a stale binary).
/// The Go build-cache check and the serve-binary check are gone with the Go toolchain and the serve child process.
/// </summary>
public static class DoctorChecks
{
    /// <summary>Performs every check and returns them in report order.</summary>
    public static async Task<IReadOnlyList<DoctorCheck>> RunAsync(DoctorOptions o, IDoctorSetup setup, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(o);
        ArgumentNullException.ThrowIfNull(setup);
        if (o.GameId.Length == 0)
        {
            o.GameId = setup.GameId;
        }

        o.Root = MustAbs(o.Root);
        if (o.Repo.Length == 0)
        {
            o.Repo = setup.FindRepo(Directory.GetCurrentDirectory()) ?? setup.FindRepo(o.Root) ?? "";
        }

        var checks = new List<DoctorCheck>();
        (DoctorGameSection? game, DoctorCheck rootCheck) = Root(o, setup);
        string gameCopy = game?.WorkingDir ?? "";
        checks.Add(rootCheck);
        if (game is not null)
        {
            checks.Add(GameCopyPath(gameCopy, setup));
            checks.Add(Mod(o, gameCopy, setup));
        }

        checks.Add(Baseline(o, gameCopy, setup));
        checks.Add(ModsConfig(o, setup));
        if (gameCopy.Length > 0)
        {
            checks.Add(await Process(o, gameCopy, cancellationToken).ConfigureAwait(false));
        }

        checks.Add(await Orphans.CheckAsync(o, cancellationToken).ConfigureAwait(false));
        checks.Add(await RunBinary(o, cancellationToken).ConfigureAwait(false));
        checks.Add(Output(o, setup));
        return checks;
    }

    /// <summary>Reports whether any check is Fail.</summary>
    public static bool Failed(IReadOnlyList<DoctorCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);
        return checks.Any(c => c.Status == DoctorStatus.Fail);
    }

    /// <summary>Prints one line per check (only Warn and Fail when <paramref name="onlyFailing"/>), the fix indented under it.</summary>
    public static async Task WriteAsync(TextWriter w, IReadOnlyList<DoctorCheck> checks, bool onlyFailing)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(checks);
        foreach (DoctorCheck c in checks)
        {
            if (onlyFailing && c.Status == DoctorStatus.Ok)
            {
                continue;
            }

            await w.WriteLineAsync($"{StatusText(c.Status),-5} {c.Name,-14} {c.Detail}").ConfigureAwait(false);
            if (c.Fix.Length > 0)
            {
                await w.WriteLineAsync($"      fix: {c.Fix}").ConfigureAwait(false);
            }
        }
    }

    /// <summary>Lists, sorted, the fixture classes registering any of <paramref name="fixtureOps"/> that <paramref name="installed"/> lacks; empty when the repo is unknown or every class is installed.</summary>
    public static IReadOnlyList<string> MissingFixtures(IDoctorSetup setup, string repo, IReadOnlyList<string> installed, IReadOnlyList<string> fixtureOps)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(fixtureOps);
        if (repo.Length == 0 || fixtureOps.Count == 0)
        {
            return [];
        }

        var have = new HashSet<string>(installed, StringComparer.Ordinal);
        return [.. setup.FixtureFlags(repo, [], fixtureOps).Where(f => !have.Contains(f)).OrderBy(f => f, StringComparer.Ordinal)];
    }

    private static string StatusText(DoctorStatus s) => s switch
    {
        DoctorStatus.Ok => "ok",
        DoctorStatus.Warn => "warn",
        _ => "FAIL",
    };

    /// <summary>The fix for anything setup makes, with any extra flags.</summary>
    private static string SetupHint(string repo, params string[] flags)
    {
        string cmd = "RimGovernor.cmd acceptance setup";
        if (repo.Length > 0)
        {
            cmd += " -worktree " + repo;
        }

        foreach (string f in flags)
        {
            if (f.Length > 0)
            {
                cmd += " " + f;
            }
        }

        return cmd;
    }

    /// <summary>Reads the root's config and the game it launches; the game section and its workingDir (the game copy) feed the later checks.</summary>
    private static (DoctorGameSection? Game, DoctorCheck Check) Root(DoctorOptions o, IDoctorSetup setup)
    {
        var c = new DoctorCheck("root") { Detail = o.Root };
        string configPath = Path.Combine(o.Root, "config", "config.json");
        if (!File.Exists(configPath))
        {
            Fail(c, "no config/config.json under " + o.Root, SetupHint(o.Repo));
            return (null, c);
        }

        DoctorGameSection game;
        try
        {
            game = setup.ReadGameSection(o.Root, o.GameId);
        }
        catch (AcceptanceException e)
        {
            Fail(c, configPath + ": " + e.Message, SetupHint(o.Repo));
            return (null, c);
        }

        if (game.WorkingDir.Length == 0 || game.Target.Length == 0)
        {
            Fail(c, configPath + ": the game has no DirectPath target or workingDir", SetupHint(o.Repo));
            return (null, c);
        }

        if (!File.Exists(game.Target))
        {
            Fail(c, "game exe missing: " + game.Target, SetupHint(o.Repo));
            return (game, c);
        }

        c.Detail = $"{o.Root} -> {game.WorkingDir}";
        return (game, c);
    }

    /// <summary>Setup's MAX_PATH bound: a copy past it boots but cannot open any Defs XML.</summary>
    private static DoctorCheck GameCopyPath(string gameCopy, IDoctorSetup setup)
    {
        var c = new DoctorCheck("game-copy") { Detail = string.Create(CultureInfo.InvariantCulture, $"{gameCopy.Length} chars: {gameCopy}") };
        if (gameCopy.Length > setup.MaxGameCopyPath)
        {
            c.Status = DoctorStatus.Fail;
            c.Detail = string.Create(CultureInfo.InvariantCulture, $"path is {gameCopy.Length} characters (limit {setup.MaxGameCopyPath}): RimWorld cannot open its own Defs XML from it");
            int headroom = setup.MaxGameCopyPath - Path.Combine(".rimgovernor", "native-rimworld").Length - 1;
            c.Fix = string.Create(CultureInfo.InvariantCulture, $"move the worktree under a path at most {headroom} characters (e.g. .claude/worktrees/<name>) and rerun setup");
        }

        return c;
    }

    /// <summary>
    /// Prepare's own package checks (the package is complete, then the stale-build comparison) run early, with the
    /// manifest's role and fixtures on the Ok line.
    /// </summary>
    private static DoctorCheck Mod(DoctorOptions o, string gameCopy, IDoctorSetup setup)
    {
        var c = new DoctorCheck("mod");
        string mods = Path.Combine(gameCopy, "Mods");
        try
        {
            setup.RequireNativePackage(mods);
        }
        catch (AcceptanceException e)
        {
            Fail(c, e.Message, SetupHint(o.Repo));
            return c;
        }

        string pkg = Path.Combine(mods, "RimGovernor");
        DoctorPackageManifest? manifest = setup.ReadPackageManifest(pkg);
        if (manifest is null)
        {
            c.Status = DoctorStatus.Warn;
            c.Detail = "installed package has no " + setup.PackageManifestName + "; the stale check cannot run";
            c.Fix = "rebuild through setup so the manifest records the sources";
            return c;
        }

        string role = manifest.Role.Length == 0 ? "unknown role" : manifest.Role;
        c.Detail = string.Create(CultureInfo.InvariantCulture, $"{role} build from {DoctorText.Short(manifest.SourceRevision)}, {manifest.Fixtures.Count} fixtures");
        if (manifest.SourceDirty)
        {
            c.Detail += " (dirty tree)";
        }

        string skipped;
        try
        {
            skipped = setup.RequireCurrentPackage(pkg, o.FixtureOps);
        }
        catch (AcceptanceException e)
        {
            // The error's rebuild recipe is the by-hand one; setup is the fix.
            int cut = e.Message.IndexOf("; rebuild it", StringComparison.Ordinal);
            c.Status = DoctorStatus.Fail;
            c.Detail = cut >= 0 ? e.Message[..cut] : e.Message;
            c.Code = DoctorHealCodes.StaleMod;
            c.Fix = SetupHint(o.Repo, FixtureFlag(setup, o.Repo, manifest.Fixtures, o.FixtureOps)) + ", -rebuild if it says the build is current";
            return c;
        }

        if (skipped.Length > 0)
        {
            c.Status = DoctorStatus.Warn;
            c.Detail += "; stale check skipped: " + skipped;
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(setup.AllowStaleModEnv)))
            {
                c.Fix = "unset " + setup.AllowStaleModEnv + " unless you mean to run against an older native build";
            }
        }

        IReadOnlyList<string> missing = MissingFixtures(setup, o.Repo, manifest.Fixtures, o.FixtureOps);
        if (missing.Count > 0)
        {
            c.Status = DoctorStatus.Fail;
            c.Code = DoctorHealCodes.MissingFixture;
            c.Detail += "; the run's cases call ops of fixtures the build lacks: " + DoctorText.Join(missing);
            c.Fix = SetupHint(o.Repo, FixtureFlag(setup, o.Repo, manifest.Fixtures, o.FixtureOps));
        }

        return c;
    }

    /// <summary>The -fixture flag a setup rebuild for the run takes (installed plus the run's), empty when there is nothing to name.</summary>
    private static string FixtureFlag(IDoctorSetup setup, string repo, IReadOnlyList<string> installed, IReadOnlyList<string> fixtureOps)
    {
        IReadOnlyList<string> flags = setup.FixtureFlags(repo, installed, fixtureOps);
        return flags.Count == 0 ? "" : "-fixture " + string.Join(',', flags);
    }

    /// <summary>
    /// The generated baseline save: missing, the first save-driven case generates it through the new-colony op; present,
    /// an expansion in its modIds the game copy does not ship fails save.missing_mods at load.
    /// </summary>
    private static DoctorCheck Baseline(DoctorOptions o, string gameCopy, IDoctorSetup setup)
    {
        var c = new DoctorCheck("baseline");
        string effective = Path.Combine(o.Root, "profile", "Saves", setup.BaselineSave);
        if (!File.Exists(effective))
        {
            c.Detail = "not generated yet; the first case that loads " + setup.BaselineName + " generates it";
            return c;
        }

        IReadOnlyList<string> mods;
        try
        {
            mods = SaveModIds(effective);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or AcceptanceException)
        {
            Fail(c, effective + ": " + e.Message, "delete it; the next case that loads it regenerates it");
            return c;
        }

        if (gameCopy.Length == 0)
        {
            return c;
        }

        IReadOnlyList<string> installed;
        try
        {
            installed = setup.InstalledExpansions(gameCopy);
        }
        catch (AcceptanceException e)
        {
            c.Status = DoctorStatus.Fail;
            c.Detail = e.Message;
            return c;
        }

        var missing = mods.Where(id => id.StartsWith(Expansions.ExpansionPrefix, StringComparison.Ordinal) && !installed.Contains(id, StringComparer.Ordinal)).ToList();
        if (missing.Count > 0)
        {
            c.Status = DoctorStatus.Fail;
            c.Detail = $"{effective} needs {DoctorText.Join(missing)}, which the game copy lacks; it fails save.missing_mods at load";
            c.Fix = "install those expansions, or delete the baseline so it regenerates on this copy";
        }

        return c;
    }

    /// <summary>Reads the modIds of an .rws from its header (the first few KB of a multi-MB file).</summary>
    internal static IReadOnlyList<string> SaveModIds(string path)
    {
        using var f = File.OpenRead(path);
        byte[] head = new byte[64 << 10];
        int n = f.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        string text = System.Text.Encoding.UTF8.GetString(head, 0, n);
        int start = text.IndexOf("<modIds>", StringComparison.Ordinal);
        int end = text.IndexOf("</modIds>", StringComparison.Ordinal);
        if (start < 0 || end < start)
        {
            throw new AcceptanceException("no <modIds> in the save header");
        }

        var ids = new List<string>();
        foreach (string part in text[(start + "<modIds>".Length)..end].Split("<li>"))
        {
            int i = part.IndexOf("</li>", StringComparison.Ordinal);
            if (i >= 0)
            {
                ids.Add(part[..i].Trim().ToLowerInvariant());
            }
        }

        return ids;
    }

    /// <summary>
    /// The profile's ModsConfig.xml Prepare rewrites: it must exist and parse with a core entry; and a kept process
    /// launched with a different list or package is relaunched by the next run.
    /// </summary>
    private static DoctorCheck ModsConfig(DoctorOptions o, IDoctorSetup setup)
    {
        var c = new DoctorCheck("mods-config");
        string path = Path.Combine(o.Root, "profile", "Config", "ModsConfig.xml");
        IReadOnlyList<string> active;
        try
        {
            active = setup.ActiveMods(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or AcceptanceException)
        {
            Fail(c, e.Message, SetupHint(o.Repo));
            return c;
        }

        if (!active.Contains(Expansions.CorePackage, StringComparer.Ordinal))
        {
            Fail(c, $"{path} activates no {Expansions.CorePackage}; Prepare refuses it", "delete it and rerun setup (it rewrites the profile's ModsConfig.xml)");
            return c;
        }

        c.Detail = string.Create(CultureInfo.InvariantCulture, $"{active.Count} active mods");
        string headless = Path.Combine(o.Root, "config-headless");
        if (!File.Exists(Path.Combine(headless, "config.json")))
        {
            return c;
        }

        string reason;
        try
        {
            reason = setup.LaunchedMismatch(headless);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or AcceptanceException)
        {
            return c;
        }

        if (reason.Length == 0)
        {
            return c;
        }

        c.Status = DoctorStatus.Warn;
        c.Detail += reason switch
        {
            "unrecorded" => "; the last launch recorded no mod list or package snapshot",
            "expansions" => "; the prepared list differs from what the kept process launched with",
            _ => "; the installed package differs from what the kept process launched with",
        };
        c.Fix = $"the next run relaunches the game (one boot); `acceptance stop -root {o.Root}` to pay it now";
        return c;
    }

    /// <summary>
    /// This root's own RimWorld processes (executables under the game copy, never a peer's). One is the kept process a
    /// run reuses; two is a leak; one small and pegged for minutes is a boot that never finished.
    /// </summary>
    private static async Task<DoctorCheck> Process(DoctorOptions o, string gameCopy, CancellationToken cancellationToken)
    {
        var c = new DoctorCheck("game");
        IReadOnlyList<HarnessProcess> procs;
        try
        {
            procs = await HarnessProcesses.RunningGameProcessesAsync(gameCopy, cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            c.Status = DoctorStatus.Warn;
            c.Detail = "could not list RimWorld processes: " + e.Message;
            return c;
        }

        var pids = procs.Select(p => p.Pid).ToList();
        switch (pids.Count)
        {
            case 0:
                c.Detail = "no game running from the copy; the first case boots one";
                break;
            case 1:
                c.Detail = string.Create(CultureInfo.InvariantCulture, $"kept process pid {pids[0]} running from the copy");
                break;
            default:
                c.Status = DoctorStatus.Warn;
                c.Detail = string.Create(CultureInfo.InvariantCulture, $"{pids.Count} games run from the copy (pids {DoctorText.Join(pids)}); a run reuses one and the rest are leaks");
                c.Fix = "stop the extras by pid (Stop-Process -Id <pid>), never by image name";
                break;
        }

        string note = Orphans.StuckNote(procs, DateTimeOffset.UtcNow);
        if (note.Length > 0)
        {
            c.Status = DoctorStatus.Warn;
            c.Detail += note;
            c.Fix = $"`acceptance stop -root {o.Root}` (or Stop-Process -Id <pid>); the next run boots fresh";
        }

        return c;
    }

    /// <summary>
    /// This very binary against the checkout: a run binary behind main reproduces failures main already fixed. The
    /// revision comes from the assembly's informational version (<c>+&lt;sha&gt;</c> suffix); a build without one is
    /// by construction not comparable.
    /// </summary>
    private static async Task<DoctorCheck> RunBinary(DoctorOptions o, CancellationToken cancellationToken)
    {
        var c = new DoctorCheck("acceptance");
        string version = typeof(DoctorChecks).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        int plus = version.IndexOf('+', StringComparison.Ordinal);
        string rev = plus >= 0 ? version[(plus + 1)..] : "";
        if (rev.Length == 0)
        {
            c.Detail = "no VCS stamp (built outside a checkout)";
            return c;
        }

        return await BehindMain(c, o.Repo, rev, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Fills <paramref name="c"/> from <paramref name="rev"/>'s standing against the checkout's HEAD and main.</summary>
    private static async Task<DoctorCheck> BehindMain(DoctorCheck c, string repo, string rev, CancellationToken cancellationToken)
    {
        c.Detail = "built from " + DoctorText.Short(rev);
        if (repo.Length == 0)
        {
            c.Detail += "; no checkout to compare against";
            return c;
        }

        TimeSpan bound = TimeSpan.FromSeconds(20);
        (int headExit, string head) = await DoctorGit.RunAsync(repo, ["rev-parse", "HEAD"], bound, cancellationToken).ConfigureAwait(false);
        if (headExit != 0)
        {
            c.Detail += "; git could not resolve HEAD";
            return c;
        }

        if (!head.StartsWith(rev, StringComparison.OrdinalIgnoreCase) && !rev.StartsWith(head, StringComparison.OrdinalIgnoreCase))
        {
            c.Status = DoctorStatus.Warn;
            c.Detail += ", the worktree is at " + DoctorText.Short(head);
            c.Fix = "rebuild so the run reflects the checkout: " + SetupHint(repo);
        }

        (int catExit, _) = await DoctorGit.RunAsync(repo, ["cat-file", "-e", rev + "^{commit}"], bound, cancellationToken).ConfigureAwait(false);
        if (catExit != 0)
        {
            c.Status = DoctorStatus.Warn;
            c.Detail += "; that revision is not in this checkout";
            return c;
        }

        (int ancestorExit, _) = await DoctorGit.RunAsync(repo, ["merge-base", "--is-ancestor", rev, "main"], bound, cancellationToken).ConfigureAwait(false);
        if (ancestorExit != 0)
        {
            c.Detail += "; not on main (branch build)";
            return c;
        }

        (int logExit, string log) = await DoctorGit.RunAsync(repo, ["log", "--oneline", rev + "..main"], bound, cancellationToken).ConfigureAwait(false);
        if (logExit != 0 || log.Length == 0)
        {
            return c;
        }

        int n = log.Split('\n').Length;
        c.Status = DoctorStatus.Warn;
        c.Detail += string.Create(CultureInfo.InvariantCulture, $"; {n} landings on main since");
        if (c.Fix.Length == 0)
        {
            c.Fix = "git merge main, then " + SetupHint(repo) + " before diagnosing anything the run fails";
        }

        return c;
    }

    /// <summary>
    /// The run's output directory: a case whose directory already holds anything is refused (every run needs a fresh
    /// one), and a service.sqlite there from an older schema is unreadable except raw (acceptance why).
    /// </summary>
    private static DoctorCheck Output(DoctorOptions o, IDoctorSetup setup)
    {
        var c = new DoctorCheck("output");
        string dir = o.Output.Length == 0 ? Path.Combine(o.Root, "acceptance") : o.Output;
        dir = MustAbs(dir);
        var occupied = new List<string>();
        foreach (string name in o.Cases)
        {
            string caseDir = Path.Combine(dir, name.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(caseDir) && Directory.EnumerateFileSystemEntries(caseDir).Any())
            {
                occupied.Add(name);
            }
        }

        if (occupied.Count > 0)
        {
            c.Status = DoctorStatus.Fail;
            c.Detail = $"{dir} already holds {DoctorText.Join(occupied)}; a case run into a non-empty directory is refused";
            string parent = Path.GetDirectoryName(dir) ?? dir;
            c.Fix = "pass a fresh -output (e.g. " + Path.Combine(parent, "runs", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)) + ")";
            return c;
        }

        var results = new List<string>();
        if (Directory.Exists(dir))
        {
            foreach (string area in Directory.EnumerateDirectories(dir))
            {
                foreach (string caseDir in Directory.EnumerateDirectories(area))
                {
                    string result = Path.Combine(caseDir, "result.json");
                    if (File.Exists(result))
                    {
                        results.Add(result);
                    }
                }
            }
        }

        if (results.Count == 0)
        {
            c.Detail = dir + " (fresh)";
            return c;
        }

        c.Detail = string.Create(CultureInfo.InvariantCulture, $"{dir} holds {results.Count} earlier case results");
        int stale = 0;
        foreach (string r in results)
        {
            int? version = StoreVersion(Path.Combine(Path.GetDirectoryName(r) ?? "", "service.sqlite"));
            if (version is { } v && v != setup.StoreSchemaVersion)
            {
                stale++;
            }
        }

        if (stale > 0)
        {
            c.Status = DoctorStatus.Warn;
            c.Detail += string.Create(CultureInfo.InvariantCulture, $"; {stale} service.sqlite of an older schema");
            c.Fix = "read them with `acceptance why <case dir>` (raw), not the store; a new run writes the current schema";
        }

        return c;
    }

    /// <summary>
    /// A service.sqlite's user_version, read from the database header (the 4-byte big-endian integer at offset 60)
    /// without opening it; null when absent or not a SQLite file.
    /// </summary>
    internal static int? StoreVersion(string path)
    {
        try
        {
            using var f = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            byte[] header = new byte[100];
            if (f.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length
                || !System.Text.Encoding.ASCII.GetString(header, 0, 15).Equals("SQLite format 3", StringComparison.Ordinal))
            {
                return null;
            }

            return BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(60, 4));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Fail(DoctorCheck c, string detail, string fix)
    {
        c.Status = DoctorStatus.Fail;
        c.Detail = detail;
        c.Fix = fix;
    }

    private static string MustAbs(string path)
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
}
