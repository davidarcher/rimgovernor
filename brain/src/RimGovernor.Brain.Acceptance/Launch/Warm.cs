using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// One harness's hold on the RimWorld process: a fresh launch, or an attach to a process an earlier harness left at the
/// main menu (Go <c>Game</c>, <c>OpenGame</c>, <c>StopGame</c> and <c>WarmGame</c> of <c>warm.go</c>).
/// <para>
/// By default (<see cref="KeepGameEnv"/> unset, or anything but 0/false/no) <see cref="Close"/> returns the game to the
/// main menu instead of stopping it, and the next <see cref="OpenGameAsync"/> under the same root attaches to the running
/// process and skips the boot and def load, roughly 80 s of every run; a batch stops the game once at the end
/// (<c>acceptance stop</c>). Mod static state is process-scoped and survives the reuse: a harness asserting on statics, or
/// one that must see a first-boot process, runs with <c>RIMGOVERNOR_ACCEPT_KEEP_GAME=0</c>.
/// </para>
/// </summary>
public sealed class Game : IGameHold
{
    /// <summary>Controls whether the RimWorld process stays alive between harness binaries.</summary>
    public const string KeepGameEnv = "RIMGOVERNOR_ACCEPT_KEEP_GAME";

    /// <summary>Returns the loaded game to the main menu without saving (ShutdownFixture); every fixture build carries it.</summary>
    public const string UnloadTool = "test/shutdown_unload";

    private static readonly TimeSpan CloseBudget = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan MenuCeiling = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan MenuInterval = TimeSpan.FromMilliseconds(500);

    private readonly string _output;
    private readonly Config _config;

    private Game(Harness harness, Config config, bool keep, string output)
    {
        Harness = harness;
        _config = config;
        Keep = keep;
        _output = output;
    }

    /// <summary>The harness the opened game is driven through.</summary>
    public Harness Harness { get; }

    /// <summary>True when <see cref="OpenGameAsync"/> attached to a process already running.</summary>
    public bool Reused { get; private set; }

    /// <summary>
    /// Why <see cref="OpenGameAsync"/> stopped a running process instead of reusing it ("expansions", "package",
    /// "unrecorded", "checkpoint", "graphics"; see <see cref="HeadlessProfile.LaunchedMismatch"/>), or empty when nothing
    /// was running or the process was reused.
    /// </summary>
    public string Relaunched { get; private set; } = "";

    /// <summary>Whether <see cref="Close"/> leaves the process up for the next harness.</summary>
    public bool Keep { get; set; }

    /// <summary>How long <see cref="OpenGameAsync"/> took, for the report.</summary>
    public TimeSpan Open { get; private set; }

    /// <summary>Whether the process is kept: true unless <see cref="KeepGameEnv"/> opts out.</summary>
    public static bool KeepGame() => !EnvOptsOut(KeepGameEnv);

    /// <summary>Whether an on-by-default flag is set to 0, false or no.</summary>
    internal static bool EnvOptsOut(string name) =>
        Environment.GetEnvironmentVariable(name)?.ToLowerInvariant() is "0" or "false" or "no";

    /// <summary>
    /// Opens a session on the configuration's game the way every harness does and, when the process was already running,
    /// returns it to the main menu so the harness starts from the state a fresh launch would give it (unless
    /// <see cref="Config.KeepLoaded"/>). A running process launched with a different mod list than the configuration
    /// prepared (ModsConfig.xml only applies at launch, so a Core-only process cannot load a DLC save) or with a package
    /// other than the one now installed (a rebuilt mod's fixtures are not in the old DLL's catalog), or one a resumed run
    /// (<see cref="Config.Resumed"/>) or a graphics case (<see cref="Config.Graphics"/>) must relaunch, is stopped and
    /// launched fresh instead; <see cref="Relaunched"/> and the report's game_reuse.relaunched say so. The configuration
    /// must have been prepared.
    /// </summary>
    public static async Task<Game> OpenGameAsync(Config config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Configuration.Length == 0)
        {
            throw new AcceptanceException("open game: Config.PrepareConfig must run first");
        }

        var started = Stopwatch.StartNew();
        var gameId = GameIdOf(config);
        var stateDir = HeadlessProfile.MustAbs(config.Configuration);
        var reused = HeadlessProfile.GameRunning(stateDir, gameId);
        var relaunched = "";
        if (reused)
        {
            var reason = HeadlessProfile.LaunchedMismatch(config.Configuration);
            if (config.Resumed)
            {
                reason = "checkpoint";
            }

            if (config.Graphics)
            {
                // A kept game may have launched with -Headless.
                reason = "graphics";
            }

            if (reason.Length > 0)
            {
                try
                {
                    _ = await GameProcess.StopAsync(stateDir, gameId, cancellationToken).ConfigureAwait(false);
                }
                catch (AcceptanceException e)
                {
                    throw new AcceptanceException($"stop kept game (relaunch: {reason}): {e.Message}", e);
                }

                reused = false;
                relaunched = reason;
            }
        }

        var transport = await GameSessions.OpenBridgeSessionAsync(config.Configuration, gameId, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
        var harness = new Harness(transport, config.Output);

        // Evidence for the unloads goes under its own directory so its numbering never collides with the harness's own.
        var game = new Game(harness, config, KeepGame(), Path.Combine(config.Output, "warm")) { Reused = reused, Relaunched = relaunched };
        if (reused && !config.KeepLoaded)
        {
            try
            {
                await game.ToMainMenuAsync("warm-open", cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                await harness.CloseAsync().ConfigureAwait(false);
                throw new AcceptanceException($"reused game: {e.Message}", e);
            }
        }

        game.Open = started.Elapsed;
        return game;
    }

    /// <summary>
    /// Ends the hold; blocks for the close's own bounded work (at most 90 seconds) because the lifecycle's session is
    /// closed from a synchronous report path.
    /// </summary>
    public void Close(Report report)
    {
        using var bound = new CancellationTokenSource(CloseBudget);
        Task.Run(() => CloseAsync(report, bound.Token), CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Ends the harness's hold: with <see cref="Keep"/>, the main menu and a closed session with the process left
    /// running; otherwise the process tree is killed (there is no games_stop to ask for a graceful exit). It records what
    /// it did on <paramref name="report"/> under "stop" / "stop_error" and "game_reuse".
    /// </summary>
    public async Task CloseAsync(Report? report, CancellationToken cancellationToken)
    {
        if (report is not null)
        {
            var reuse = new JsonObject
            {
                ["reused"] = Reused,
                ["kept"] = Keep,
                ["openMs"] = (long)Open.TotalMilliseconds,
            };
            if (Relaunched.Length > 0)
            {
                reuse["relaunched"] = Relaunched;
            }

            report["game_reuse"] = reuse;
        }

        try
        {
            if (Keep && _config.KeepLoaded)
            {
                if (report is not null)
                {
                    report["stop"] = "kept running with the game loaded (Config.KeepLoaded)";
                }

                return;
            }

            if (Keep)
            {
                try
                {
                    await ToMainMenuAsync("warm-close", cancellationToken).ConfigureAwait(false);
                    if (report is not null)
                    {
                        report["stop"] = "kept running at the main menu (" + KeepGameEnv + ")";
                    }

                    return;
                }
                catch (AcceptanceException e)
                {
                    if (report is not null)
                    {
                        report["stop_error"] = "keep: " + e.Message;
                    }
                }
            }

            try
            {
                var stopped = await GameProcess.StopAsync(HeadlessProfile.MustAbs(_config.Configuration), GameIdOf(_config), cancellationToken).ConfigureAwait(false);
                if (report is not null)
                {
                    report["stop"] = stopped ? "stopped" : "not running";
                }
            }
            catch (AcceptanceException e)
            {
                if (report is not null)
                {
                    report["stop_error"] = e.Message;
                }
            }
        }
        finally
        {
            await Harness.CloseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stops whatever game <paramref name="root"/> owns, headless first, then the windowed profile's kept process if one
    /// was launched (<see cref="StopRenderedGameAsync"/>), and waits for the processes to be gone; a root with nothing
    /// running is not an error. It is what ends a batch of harnesses that kept the game (<see cref="KeepGameEnv"/>). The
    /// state directories are <c>&lt;root&gt;/config-headless</c> and <c>&lt;root&gt;/config</c>, not the root itself.
    /// Returns whether a process was stopped.
    /// </summary>
    public static async Task<bool> StopGameAsync(string root, string gameId, CancellationToken cancellationToken)
    {
        var abs = HeadlessProfile.MustAbs(root);
        var stopped = await GameProcess.StopAsync(Path.Combine(abs, "config-headless"), gameId, cancellationToken).ConfigureAwait(false);
        return await StopRenderedGameAsync(root, gameId, cancellationToken).ConfigureAwait(false) || stopped;
    }

    /// <summary>
    /// Stops the process the root's windowed profile (<c>root/config</c>, the profile PrepareRendered launches) keeps, so
    /// a headless case does not boot beside it: a rendered case leaves its process at the main menu, and on a small box
    /// a window still drawing its menu starves the next headless boot past the connect budget. Nothing is done when the
    /// windowed profile carries no launch record (the root never launched rendered), so headless-only roots pay
    /// nothing; after a stop the record is removed, since the process it described is gone. Returns whether a process
    /// was stopped.
    /// </summary>
    public static async Task<bool> StopRenderedGameAsync(string root, string gameId, CancellationToken cancellationToken)
    {
        var configDir = Path.Combine(HeadlessProfile.MustAbs(root), "config");
        string profile;
        try
        {
            profile = HeadlessProfile.SaveDataFolder(configDir);
        }
        catch (AcceptanceException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }

        var record = Path.Combine(profile, HeadlessProfile.LaunchedModsFile);
        if (!File.Exists(record))
        {
            return false;
        }

        var stopped = await GameProcess.StopAsync(configDir, gameId, cancellationToken).ConfigureAwait(false);
        TryDelete(record);
        TryDelete(Path.Combine(profile, HeadlessProfile.LaunchedPackageFile));
        return stopped;
    }

    /// <summary>The game id a configuration launches under; the trial id when it names none.</summary>
    private static string GameIdOf(Config config) => config.GameId.Length > 0 ? config.GameId : HeadlessProfile.TrialGameId;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Gone or held; the next launch overwrites the record.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Unloads whatever is loaded and waits until no game answers.</summary>
    private async Task ToMainMenuAsync(string label, CancellationToken cancellationToken)
    {
        try
        {
            _ = Directory.CreateDirectory(_output);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new AcceptanceException($"create {_output}: {e.Message}", e);
        }

        // A harness of its own over the same session, so the unloads number their evidence under the warm directory; it is
        // never closed here because that would end the session.
        var h = new Harness(Harness.Transport, _output);
        if (!await GameLoadedAsync(h, label + "-identity", cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var names = await h.DiscoveryAsync(cancellationToken).ConfigureAwait(false) ?? [];
        if (!Session.HasTool(names, UnloadTool))
        {
            throw new AcceptanceException($"a game is loaded and {UnloadTool} is not installed: build the mod with a fixture or stop the game");
        }

        var reply = await h.CallAsync(label + "-unload", UnloadTool, [], cancellationToken).ConfigureAwait(false);
        if (reply["success"].AsBool() != true)
        {
            throw new AcceptanceException($"{UnloadTool} refused: {reply.ToJson()}");
        }

        var wait = new Wait { Ceiling = MenuCeiling, Interval = MenuInterval };
        await wait.WaitProgressAsync(
            async ct => ("", !await GameLoadedAsync(h, label + "-menu", ct).ConfigureAwait(false)),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether <c>lifecycle_read_identity</c> reports a loaded game; an unavailable or failure outcome is the main menu.</summary>
    private static async Task<bool> GameLoadedAsync(Harness h, string label, CancellationToken cancellationToken)
    {
        var reply = await h.WireAsync(label, "lifecycle_read_identity", new JsonObject(), cancellationToken).ConfigureAwait(false);
        return reply["loaded"].AsMap() is not null;
    }

    /// <summary>
    /// Prepares the configuration's profile and boots its game to the main menu, leaving the process running for the
    /// next <see cref="OpenGameAsync"/> under the same root to attach. It is the launch a first run would pay, moved to a
    /// moment nobody is waiting for it (a post-build hook): the launch records the same mods and package snapshots, so a
    /// mod rebuilt after the warm relaunches on the same "package" check as any kept process and a stale package is
    /// refused before the boot. A process already running and matching the profile is left alone. The configuration
    /// needs Root, GameId and Headless; Output defaults to <c>&lt;Root&gt;/acceptance/warm</c>.
    /// </summary>
    public static async Task<Warmed> WarmGameAsync(Config config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        var started = Stopwatch.StartNew();
        if (config.Output.Length == 0)
        {
            config.Output = Path.Combine(config.Root, "acceptance", "warm");
        }

        if (config.Configuration.Length == 0)
        {
            try
            {
                config.PrepareConfig();
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"prepare profile: {e.Message}", e);
            }
        }

        var game = await OpenGameAsync(config, cancellationToken).ConfigureAwait(false);
        game.Keep = true;
        var report = new Report();
        await game.CloseAsync(report, cancellationToken).ConfigureAwait(false);
        if (report["stop_error"].AsString() is { Length: > 0 } stopError)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"keep warmed game: {stopError}"));
        }

        return new Warmed(game.Reused, game.Relaunched, started.Elapsed, config.Configuration);
    }
}

/// <summary>What <see cref="Game.WarmGameAsync"/> did to the root's process.</summary>
/// <param name="Reused">True when a process already at the menu was kept as is.</param>
/// <param name="Relaunched">Why a running process was replaced (<see cref="Game.Relaunched"/>).</param>
/// <param name="Open">How long the prepare and boot took.</param>
/// <param name="Configuration">The profile directory the process was launched with.</param>
public sealed record Warmed(bool Reused, string Relaunched, TimeSpan Open, string Configuration);
