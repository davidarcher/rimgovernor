using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RimGovernor.Brain.Acceptance;

/// <summary>Where a worktree's private environment lives, all under <c>&lt;repo&gt;/.rimgovernor</c> (git-ignored; Go <c>setup.Layout</c>).</summary>
public sealed class SetupLayout
{
    public SetupLayout(string repo, string gameCopy, string root, string builds)
    {
        Repo = repo;
        GameCopy = gameCopy;
        Root = root;
        Builds = builds;
    }

    public string Repo { get; }

    /// <summary>The private RimWorld install (native-rimworld).</summary>
    public string GameCopy { get; }

    /// <summary>The root <c>acceptance run -root</c> takes (bridge).</summary>
    public string Root { get; }

    /// <summary>Mod build outputs (native-builds/&lt;role&gt;-&lt;stamp&gt;).</summary>
    public string Builds { get; }

    /// <summary>The conventional layout for <paramref name="repo"/>.</summary>
    public static SetupLayout For(string repo)
    {
        repo = SetupDiscovery.AbsClean(repo);
        return Under(repo, Path.Combine(repo, ".rimgovernor"));
    }

    /// <summary>The layout with every private directory under <paramref name="layoutDir"/> (the <c>-layout</c> flag).</summary>
    public static SetupLayout Under(string repo, string layoutDir) => new(
        repo,
        Path.Combine(layoutDir, "native-rimworld"),
        Path.Combine(layoutDir, "bridge"),
        Path.Combine(layoutDir, "native-builds"));
}

/// <summary>Drives <see cref="SetupEnvironment.RunAsync"/> (Go <c>setup.Options</c>).</summary>
public sealed class SetupOptions
{
    public required SetupLayout Layout { get; init; }

    public required SetupInputs Inputs { get; init; }

    /// <summary>The fixture classes the mod build takes; null means every build flag of the native project; empty is the production build.</summary>
    public IReadOnlyList<string>? Fixtures { get; init; }

    /// <summary>Forces a mod build even when the installed one is current.</summary>
    public bool Rebuild { get; init; }

    /// <summary>The worktree's sourceTree hash when the caller already computed it (<see cref="SetupPackage.SourceTreeHash"/>); null computes it.</summary>
    public string? SourceTree { get; init; }

    /// <summary>Leaves the installed mod alone (no build, no install).</summary>
    public bool SkipMod { get; init; }

    /// <summary>Receives progress lines.</summary>
    public TextWriter Log { get; init; } = TextWriter.Null;
}

/// <summary>What <see cref="SetupEnvironment.RunAsync"/> did, for the closing report (Go <c>setup.Summary</c>).</summary>
public sealed class SetupSummary
{
    public required SetupLayout Layout { get; init; }

    public required SetupInputs Inputs { get; init; }

    public IReadOnlyList<string> Fixtures { get; internal set; } = [];

    public bool ModBuilt { get; internal set; }

    public string ModBuild { get; internal set; } = "";

    public string ModReason { get; internal set; } = "";

    public IReadOnlyList<string> Changes => _changes;

    public string RunCommand { get; internal set; } = "";

    public string StopCommand { get; internal set; } = "";

    private readonly List<string> _changes = [];

    internal void AddChange(string line) => _changes.Add(line);
}

/// <summary>
/// Builds a worktree's private native acceptance environment (Go <c>setup/setup.go</c>; <c>acceptance setup</c>): the
/// game copy under .rimgovernor/native-rimworld, the root under .rimgovernor/bridge and the fixture mod build installed
/// into the copy. The Go controller binaries step is gone: the harness and the brain are one C# process now.
/// </summary>
public static class SetupEnvironment
{
    /// <summary>
    /// Bounds the copy's path: the deepest Core Defs path under it is ~120 characters and the engine's memory-mapped
    /// file open has no long-path support, so a copy past this fails to load the game's own XML. The .claude/worktrees
    /// layout sits at ~100.
    /// </summary>
    public const int MaxGameCopyPath = 140;

    /// <summary>The configured game every runner flag defaults to.</summary>
    public const string GameId = "rimgovernor-trial";

    /// <summary>The install's directories the copy junctions to Steam instead of copying: the bulk of the game, read-only for a DirectPath launch.</summary>
    public static readonly IReadOnlyList<string> JunctionDirs = ["Data"];

    /// <summary>Enough of a Prefs.xml for RimWorld to launch and TrimPrefs to fill in; the game writes the rest on first run.</summary>
    private const string MinimalPrefs = """
        <?xml version="1.0" encoding="utf-8"?>
        <PrefsData>
          <langFolderName>English</langFolderName>
          <devMode>False</devMode>
          <runInBackground>True</runInBackground>
          <automaticPauseMode>MajorThreat</automaticPauseMode>
          <resetModsConfigOnCrash>False</resetModsConfigOnCrash>
          <adaptiveTrainingEnabled>False</adaptiveTrainingEnabled>
        </PrefsData>

        """;

    /// <summary>
    /// Makes the layout complete and current: the game copy, the root, the installed mod, each step idempotent and
    /// reporting what it changed. Throws <see cref="SetupException"/> on failure.
    /// </summary>
    public static async Task<SetupSummary> RunAsync(SetupOptions o, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(o);
        if (!OperatingSystem.IsWindows())
        {
            throw new SetupException("acceptance setup builds an NTFS-junctioned copy of a Windows RimWorld install; it only runs on Windows");
        }

        var l = o.Layout;
        var s = new SetupSummary { Layout = l, Inputs = o.Inputs };

        // Refuse before copying game files, not only before installing the mod. A running game owns all these inputs.
        var processes = await SetupProcesses.ListProcessesAsync(SetupProcesses.HarnessImages, cancellationToken).ConfigureAwait(false);
        foreach (var p in processes)
        {
            if (p.Pid != Environment.ProcessId && (p.Under(l.GameCopy) || p.Under(l.Root)))
            {
                throw new SetupException(string.Create(CultureInfo.InvariantCulture, $"setup refuses while owned process {p.Pid} ({p.Name}) is running; stop this root first"));
            }
        }

        var n = l.GameCopy.Length;
        if (n > MaxGameCopyPath)
        {
            var allowed = MaxGameCopyPath - "/.rimgovernor/native-rimworld".Length;
            throw new SetupException(string.Create(CultureInfo.InvariantCulture, $"game copy path is {n} characters ({l.GameCopy}); RimWorld memory-maps Data files by full path and fails past MAX_PATH (observed: every Defs XML \"Could not open file\", the mod assembly not loading). Keep the worktree under a path of at most {allowed} characters"));
        }

        void Note(string line)
        {
            s.AddChange(line);
            o.Log.WriteLine(line);
        }

        o.Log.Write(string.Create(CultureInfo.InvariantCulture, $"worktree  {l.Repo}\nrimworld  {o.Inputs.RimWorldDir} ({o.Inputs.Sources["rimworld"]})\nharmony   {o.Inputs.Harmony} ({o.Inputs.Sources["harmony"]})\n"));

        try
        {
            try
            {
                await GameCopyAsync(o.Inputs.RimWorldDir, l.GameCopy, Note, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or SetupException)
            {
                throw new SetupException("game copy: " + e.Message, e);
            }

            if (o.Inputs.HarmonyMod.Length > 0)
            {
                try
                {
                    _ = await EnsureJunctionAsync(Path.Combine(l.GameCopy, "Mods", "Harmony"), o.Inputs.HarmonyMod, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or SetupException)
                {
                    throw new SetupException("harmony runtime: " + e.Message, e);
                }
            }

            try
            {
                BridgeRoot(l, o.Inputs, Note);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new SetupException("bridge root: " + e.Message, e);
            }

            if (!o.SkipMod)
            {
                try
                {
                    await InstallModAsync(o, s, Note, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or SetupException)
                {
                    throw new SetupException("mod: " + e.Message, e);
                }
            }
        }
        catch (JsonException e)
        {
            throw new SetupException("setup: " + e.Message, e);
        }

        s.RunCommand = $"RimGovernor.cmd acceptance run smoke/identity -root {l.Root}";
        s.StopCommand = $"RimGovernor.cmd acceptance stop -root {l.Root}";
        return s;
    }

    /// <summary>
    /// Makes <paramref name="dest"/> the private install: the loose top-level files copied (refreshed when Steam's
    /// differ in size or mtime), JunctionDirs junctioned, every other top-level directory copied, and any mod left out
    /// (ModsConfig decides activation; the copy holds only our own package).
    /// </summary>
    private static async Task GameCopyAsync(string steam, string dest, Action<string> note, CancellationToken cancellationToken)
    {
        _ = Directory.CreateDirectory(Path.Combine(dest, "Mods"));
        var copied = 0;
        foreach (var entry in new DirectoryInfo(steam).EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            var name = entry.Name;
            string from = Path.Combine(steam, name), to = Path.Combine(dest, name);
            if (JunctionDirs.Contains(name, StringComparer.Ordinal))
            {
                if (await EnsureJunctionAsync(to, from, cancellationToken).ConfigureAwait(false))
                {
                    note($"junction {to} -> {from}");
                }
            }
            else if (string.Equals(name, "Mods", StringComparison.Ordinal))
            {
                continue;
            }
            else if (entry is DirectoryInfo)
            {
                copied += CopyTree(from, to);
            }
            else
            {
                copied += CopyIfChanged(from, to);
            }
        }

        if (copied > 0)
        {
            note(string.Create(CultureInfo.InvariantCulture, $"copied {copied} game files into {dest}"));
        }
    }

    /// <summary>
    /// Makes <paramref name="link"/> a junction to <paramref name="target"/>, replacing a junction that points
    /// elsewhere; a real directory in the way is an error (it is someone's data, not ours to delete). Returns whether it
    /// changed anything.
    /// </summary>
    private static async Task<bool> EnsureJunctionAsync(string link, string target, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(target))
        {
            throw new SetupException(target + " is not a directory");
        }

        var info = new DirectoryInfo(link);
        if (info.Exists || File.Exists(link))
        {
            if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                var current = info.LinkTarget;
                if (current is not null
                    && string.Equals(
                        Path.TrimEndingDirectorySeparator(current.StartsWith(@"\\?\", StringComparison.Ordinal) ? current[4..] : current),
                        Path.TrimEndingDirectorySeparator(target),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                // Deleting a junction (non-recursive) removes the link, never the directory it points at.
                try
                {
                    info.Delete(recursive: false);
                }
                catch (IOException e)
                {
                    throw new SetupException($"replace junction {link}: {e.Message}", e);
                }
            }
            else
            {
                throw new SetupException($"{link} exists and is not a junction; move it aside (it should point at {target})");
            }
        }

        var parent = Path.GetDirectoryName(link);
        if (!string.IsNullOrEmpty(parent))
        {
            _ = Directory.CreateDirectory(parent);
        }

        var (exit, stdout, stderr) = await SetupProcesses.RunToolAsync("cmd", ["/c", "mklink", "/J", link, target], cancellationToken).ConfigureAwait(false);
        if (exit != 0)
        {
            throw new SetupException($"mklink /J {link} {target}: exit {exit}: {(stdout + stderr).Trim()}");
        }

        return true;
    }

    /// <summary>Copies <paramref name="from"/> over <paramref name="to"/> unless <c>to</c> already has its size and modification time; returns how many files it wrote (0 or 1).</summary>
    private static int CopyIfChanged(string from, string to)
    {
        var src = new FileInfo(from);
        var dst = new FileInfo(to);
        if (dst.Exists && dst.Length == src.Length && dst.LastWriteTimeUtc == src.LastWriteTimeUtc)
        {
            return 0;
        }

        SetupPackage.CopyFile(from, to);
        File.SetLastWriteTimeUtc(to, src.LastWriteTimeUtc);
        return 1;
    }

    /// <summary>Copies the regular files under <paramref name="from"/> into <paramref name="to"/> (CopyIfChanged each), returning the count written.</summary>
    private static int CopyTree(string from, string to)
    {
        var n = 0;
        foreach (var path in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            n += CopyIfChanged(path, Path.Combine(to, Path.GetRelativePath(from, path)));
        }

        return n;
    }

    /// <summary>
    /// Makes the root a worker root for the copy: config/config.json launching the copy's exe with the root's profile,
    /// profile/Config with a Prefs.xml (the player's own when RimWorld has been run on this machine, else a minimal one;
    /// Prepare trims either) and a ModsConfig.xml for the core game plus Harmony and our package. The baseline save is
    /// generated into profile/Saves on first use.
    /// </summary>
    private static void BridgeRoot(SetupLayout l, SetupInputs inputs, Action<string> note)
    {
        var configPath = Path.Combine(l.Root, "config", "config.json");
        var want = RenderConfig(l);
        string? current = null;
        try
        {
            current = File.ReadAllText(configPath);
        }
        catch (IOException)
        {
        }

        if (!string.Equals(current, want, StringComparison.Ordinal))
        {
            _ = Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            File.WriteAllText(configPath, want, new UTF8Encoding(false));
            note("wrote " + configPath);
        }

        var config = Path.Combine(l.Root, "profile", "Config");
        _ = Directory.CreateDirectory(config);
        var prefs = Path.Combine(config, "Prefs.xml");
        if (!File.Exists(prefs))
        {
            var player = inputs.CleanProfile ? "" : PlayerPrefs();
            if (player.Length > 0)
            {
                SetupPackage.CopyFile(player, prefs);
                note("copied the player's Prefs.xml from " + player);
            }
            else
            {
                File.WriteAllText(prefs, MinimalPrefs, new UTF8Encoding(false));
                note("wrote a minimal " + prefs);
            }
        }

        var mods = Path.Combine(config, "ModsConfig.xml");
        if (!File.Exists(mods))
        {
            var version = "";
            try
            {
                version = File.ReadAllText(Path.Combine(inputs.RimWorldDir, "Version.txt")).Trim();
            }
            catch (IOException)
            {
            }

            File.WriteAllText(mods, RenderModsConfig(version), new UTF8Encoding(false));
            note("wrote " + mods);
        }
    }

    /// <summary>
    /// The root's config/config.json: a DirectPath launch of the copy's exe with the root's profile and log, windowed
    /// (Prepare rewrites the args for headless runs into config-headless).
    /// </summary>
    public static string RenderConfig(SetupLayout l)
    {
        ArgumentNullException.ThrowIfNull(l);
        var profile = Path.Combine(l.Root, "profile");
        var game = new HarnessGame
        {
            Args =
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
            ],
            Id = GameId,
            LaunchMode = "DirectPath",
            Name = "RimGovernor",
            Target = Path.Combine(l.GameCopy, SetupDiscovery.GameImage),
            WorkingDir = l.GameCopy,
        };
        var config = new HarnessConfig
        {
            Games = new() { [GameId] = game },
            Version = "1.0",
        };
        return LaunchJson.Config(config) + "\n";
    }

    /// <summary>A ModsConfig.xml activating the core game, Harmony and the native package, in load order.</summary>
    public static string RenderModsConfig(string version) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <ModsConfigData>
          <version>{version}</version>
          <activeMods>
            <li>ludeon.rimworld</li>
            <li>brrainz.harmony</li>
            <li>{SetupPackage.NativePackage}</li>
          </activeMods>
          <knownExpansions />
        </ModsConfigData>

        """;

    /// <summary>The Prefs.xml of the player's own RimWorld profile on this machine; empty when the game has never been run here.</summary>
    private static string PlayerPrefs()
    {
        var profile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (string.IsNullOrEmpty(profile))
        {
            return "";
        }

        var path = Path.Combine(profile, "AppData", "LocalLow", "Ludeon Studios", "RimWorld by Ludeon Studios", "Config", "Prefs.xml");
        return File.Exists(path) ? path : "";
    }

    /// <summary>
    /// Brings the copy's Mods/RimGovernor to a build of this worktree's native sources with the wanted fixtures: it skips
    /// when the installed manifest already records that source tree and fixture set, otherwise builds through
    /// <see cref="NativeModBuild"/> and installs the package -- never while a game runs from the copy.
    /// </summary>
    private static async Task InstallModAsync(SetupOptions o, SetupSummary s, Action<string> note, CancellationToken cancellationToken)
    {
        var l = o.Layout;
        var wanted = o.Fixtures ?? FixtureInputs.BuildFlags(l.Repo);
        s.Fixtures = NativeModBuild.RecordedFixtures(wanted);
        var installed = Path.Combine(l.GameCopy, "Mods", "RimGovernor");
        var current = o.SourceTree ?? SetupPackage.SourceTreeHash(l.Repo);
        var reason = "no installed build";
        SetupPackageManifest? manifest = null;
        try
        {
            manifest = SetupPackage.ReadPackageManifest(installed);
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            // No readable manifest: build.
        }

        if (manifest is not null)
        {
            var got = manifest.Fixtures.Order(StringComparer.Ordinal).ToList();
            if (o.Rebuild)
            {
                reason = "-rebuild";
            }
            else if (!string.Equals(manifest.SourceTree, current, StringComparison.Ordinal))
            {
                reason = "installed build's sources differ from the worktree";
            }
            else if (!string.Equals(string.Join(',', got), string.Join(',', s.Fixtures), StringComparison.Ordinal))
            {
                reason = $"installed build has fixtures [{string.Join(' ', got)}], wanted [{string.Join(' ', s.Fixtures)}]";
            }
            else
            {
                s.ModReason = "installed build is current";
                o.Log.WriteLine("mod       " + s.ModReason);
                return;
            }
        }

        s.ModReason = reason;
        var running = await SetupProcesses.RunningGamesAsync(l.GameCopy, cancellationToken).ConfigureAwait(false);
        if (running.Count > 0)
        {
            throw new SetupException(string.Create(CultureInfo.InvariantCulture, $"a game is running from {l.GameCopy} (pid [{string.Join(' ', running)}]); stop it first (RimGovernor.cmd acceptance stop -root {l.Root}) -- a DLL must never be replaced under a running game"));
        }

        var role = s.Fixtures.Count == 0 ? "production" : "fixture";
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var output = Path.Combine(l.Builds, $"{role}-{stamp}");
        note($"building the {role} mod ({reason}) into {output}");
        _ = await NativeModBuild.BuildAsync(
            new NativeModBuildOptions { Repo = l.Repo, RimWorldDir = o.Inputs.RimWorldDir, Harmony = o.Inputs.Harmony, Fixtures = wanted, OutputRoot = output, Log = o.Log },
            cancellationToken).ConfigureAwait(false);
        s.ModBuilt = true;
        s.ModBuild = output;
        var pkg = Path.Combine(output, "RimGovernor");
        try
        {
            _ = SetupPackage.ReadPackageManifest(pkg);
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            throw new SetupException("build produced no package: " + e.Message, e);
        }

        // The install is a rename away and a fresh copy in, so a half-copied package never sits under the game's Mods.
        if (Directory.Exists(installed))
        {
            var trash = Path.Combine(l.Repo, ".rimgovernor", "trash", "RimGovernor-" + stamp);
            _ = Directory.CreateDirectory(Path.GetDirectoryName(trash)!);
            try
            {
                Directory.Move(installed, trash);
            }
            catch (IOException e)
            {
                throw new SetupException("move the old package aside: " + e.Message, e);
            }

            try
            {
                Directory.Delete(trash, recursive: true);
            }
            catch (IOException)
            {
                // Leftover trash is harmless.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        _ = CopyTree(pkg, installed);
        SetupPackage.RequireNativePackage(Path.Combine(l.GameCopy, "Mods"));
        note($"installed {installed} (fixtures [{string.Join(' ', s.Fixtures)}])");
    }
}
