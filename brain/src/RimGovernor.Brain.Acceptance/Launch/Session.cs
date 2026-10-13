using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using RimGovernor.Brain.HttpChannel;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// Starting, attaching to and connecting to the configured game (Go <c>session.go</c>'s OpenBridgeSession and friends).
/// The Go harness opened a GABP bridge session through a launcher; the brain now runs in the mod and the harness talks to
/// its local HTTP channel, so the session is an <see cref="INativeTransport"/> and the launch is
/// <see cref="GameProcess.StartDetached"/> with the channel's directory in the environment. The game records its channel
/// in <c>http-endpoint.json</c> under that directory (<see cref="EndpointDirectory"/>), which also tells a later
/// controller which process serves it.
/// </summary>
public static class GameSessions
{
    /// <summary>How long a connect waits for the game to boot and announce a working channel.</summary>
    public static readonly TimeSpan ConnectBudget = TimeSpan.FromMinutes(5);

    private const string OtlpDirectoryVariable = "RIMGOVERNOR_OTLP_DIR";
    private const string RunIdVariable = "RIMGOVERNOR_RUN_ID";
    private const string ReadinessTool = "rimgovernor/lifecycle_read_identity";

    /// <summary>
    /// The launch spec for <paramref name="gameId"/> from <c>games.&lt;gameId&gt;</c> of <paramref name="configDir"/>'s
    /// config.json; the running game's records live under <paramref name="configDir"/>, so every session of one
    /// configuration finds the same game.
    /// </summary>
    public static GameLaunchSpec LaunchSpecFromConfig(string configDir, string gameId)
    {
        var stateDir = HeadlessProfile.MustAbs(configDir);
        var game = HeadlessProfile.GameSection(HeadlessProfile.LoadConfig(Path.Combine(stateDir, "config.json")), gameId);
        var executable = game["target"].AsString();
        if (executable.Length == 0)
        {
            throw new AcceptanceException($"{Path.Combine(stateDir, "config.json")}: games.{gameId} has no target");
        }

        var args = game["args"].AsSlice().Select(a => a.AsString()).ToList();
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (game["env"].AsMap() is { } entries)
        {
            foreach (var (name, value) in entries)
            {
                env[name] = value.AsString();
            }
        }

        return new GameLaunchSpec
        {
            GameId = gameId,
            Executable = executable,
            WorkingDir = game["workingDir"].AsString(),
            Args = args,
            Env = env,
            StateDir = stateDir,
        };
    }

    /// <summary>The directory the game announces its HTTP channel in: its own record directory under the configuration.</summary>
    public static string EndpointDirectory(string configDir, string gameId) => GameProcess.GameDir(HeadlessProfile.MustAbs(configDir), gameId);

    /// <summary>A transport to the game's channel, waiting up to <paramref name="timeout"/> per call for the endpoint file.</summary>
    public static HttpNativeTransport CreateTransport(string configDir, string gameId, TimeSpan timeout) =>
        new(EndpointDirectory(configDir, gameId), timeout);

    /// <summary>
    /// Runs right before a launch (not an attach): the prepared ModsConfig.xml and the installed package's hashes are
    /// snapshotted so a later open can tell what the process runs with.
    /// </summary>
    public static void PrepareFreshLaunch(string configDir)
    {
        HeadlessProfile.RecordLaunchedMods(configDir);
        HeadlessProfile.RecordLaunchedPackage(configDir);
    }

    /// <summary>
    /// Opens a session on the configured game, starting it (attaching when one is already running under
    /// <paramref name="configDir"/>), then connects and waits for the channel to answer. On any failure the session is
    /// disposed before the exception leaves, so callers never leak a half-open one.
    /// </summary>
    public static async Task<INativeTransport> OpenBridgeSessionAsync(string configDir, string gameId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var transport = CreateTransport(configDir, gameId, timeout);
        var ok = false;
        try
        {
            if (!HeadlessProfile.GameRunning(HeadlessProfile.MustAbs(configDir), gameId))
            {
                PrepareFreshLaunch(configDir);
            }

            var game = StartOrAttach(configDir, gameId);
            await ConnectAsync(transport, game, configDir, gameId, ConnectBudget, cancellationToken).ConfigureAwait(false);
            ok = true;
            return transport;
        }
        finally
        {
            if (!ok)
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Connects to the running game without starting one. Only for stopping a game whose controller stalled; with no
    /// owner lease any session may connect, so it is a plain connect.
    /// </summary>
    public static async Task<INativeTransport> OpenRunningSessionAsync(string configDir, string gameId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var transport = CreateTransport(configDir, gameId, timeout);
        var ok = false;
        try
        {
            var game = GameProcess.Attach(HeadlessProfile.MustAbs(configDir), gameId)
                ?? throw new AcceptanceException($"connect: no game \"{gameId}\" is running under {configDir}");
            await ConnectAsync(transport, game, configDir, gameId, ConnectBudget, cancellationToken).ConfigureAwait(false);
            ok = true;
            return transport;
        }
        finally
        {
            if (!ok)
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The game recorded under <paramref name="configDir"/> when one is running, else a fresh detached launch of the
    /// configured game with the channel directory and a new run id in its environment (the mod starts its HTTP channel only
    /// then). A stale endpoint file from an earlier process is removed first so nothing connects to it.
    /// </summary>
    public static GameProcess StartOrAttach(string configDir, string gameId)
    {
        var stateDir = HeadlessProfile.MustAbs(configDir);
        var existing = GameProcess.Attach(stateDir, gameId);
        if (existing is not null)
        {
            return existing;
        }

        var spec = LaunchSpecFromConfig(configDir, gameId);
        var directory = EndpointDirectory(configDir, gameId);
        var stale = EndpointFile.PathIn(directory);
        try
        {
            File.Delete(stale);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new AcceptanceException($"remove stale {stale}: {e.Message}", e);
        }

        var env = new Dictionary<string, string>(spec.Env, StringComparer.Ordinal)
        {
            [OtlpDirectoryVariable] = directory,
            [RunIdVariable] = Guid.NewGuid().ToString("N"),
        };
        try
        {
            return GameProcess.StartDetached(new GameLaunchSpec
            {
                GameId = spec.GameId,
                Executable = spec.Executable,
                WorkingDir = spec.WorkingDir,
                Args = spec.Args,
                Env = env,
                StateDir = spec.StateDir,
            });
        }
        catch (GameAlreadyRunningException)
        {
            return GameProcess.Attach(stateDir, gameId) ?? throw new AcceptanceException($"game \"{gameId}\" vanished while attaching");
        }
    }

    /// <summary>
    /// Waits, for up to <paramref name="budget"/>, until the game serves its channel: the process is alive, its endpoint
    /// file names it, <c>GET /api/health</c> answers and a first native read gets a reply (a refusal counts: the game
    /// answered). A process that exits meanwhile fails the connect at once with its exit code and the end of its log.
    /// </summary>
    public static async Task ConnectAsync(INativeTransport transport, GameProcess game, string configDir, string gameId, TimeSpan budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(game);
        var stateDir = HeadlessProfile.MustAbs(configDir);
        var directory = EndpointDirectory(configDir, gameId);
        var clock = Stopwatch.StartNew();
        var lastProblem = "the game has not announced its channel yet";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        while (true)
        {
            if (!game.Alive)
            {
                throw new AcceptanceException(ExitedMessage(stateDir, gameId, game.Pid));
            }

            if (EndpointFile.Read(directory) is { } endpoint && endpoint.Address.Length > 0 && endpoint.Pid == game.Pid)
            {
                var problem = await ProbeAsync(transport, http, endpoint.Address, cancellationToken).ConfigureAwait(false);
                if (problem is null)
                {
                    return;
                }

                lastProblem = problem;
            }

            if (clock.Elapsed >= budget)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"connect: the game (pid {game.Pid}) was not reachable within {budget.TotalSeconds:0}s: {lastProblem}"));
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>One readiness round: null when the channel answers, else why not.</summary>
    private static async Task<string?> ProbeAsync(INativeTransport transport, HttpClient http, string address, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(new Uri(address.TrimEnd('/') + "/api/health"), cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return $"/api/health answered {(int)response.StatusCode}";
            }
        }
        catch (HttpRequestException e)
        {
            return "/api/health: " + e.Message;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return "/api/health: no reply";
        }

        try
        {
            _ = await transport.CallAsync(
                ReadinessTool,
                new JsonObject { ["request"] = "{}" },
                new NativeCallOptions { Timeout = TimeSpan.FromSeconds(60), Phase = "connect" },
                cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (NativeRefusedException)
        {
            // The game answered, which is all a connect asks.
            return null;
        }
        catch (NativeTransportException e)
        {
            return e.Message;
        }
    }

    private static string ExitedMessage(string stateDir, string gameId, int pid)
    {
        var message = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"connect: the game (pid {pid}) exited before its channel came up"));
        if (GameProcess.LastExit(stateDir, gameId) is { } code)
        {
            message.Append(CultureInfo.InvariantCulture, $" (exit code {code})");
        }

        var tail = LogTailText(GameProcess.LogPath(stateDir, gameId), 2048);
        if (tail.Length > 0)
        {
            message.Append(": ").Append(GameProcess.LogPath(stateDir, gameId)).Append(" ends with: ").Append(tail);
        }

        return message.ToString();
    }

    /// <summary>The last <paramref name="bytes"/> of a log as trimmed text; empty when unreadable.</summary>
    internal static string LogTailText(string path, int bytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = (int)Math.Min(stream.Length, bytes);
            _ = stream.Seek(-length, SeekOrigin.End);
            var buffer = new byte[length];
            var read = 0;
            while (read < length)
            {
                var n = stream.Read(buffer, read, length - read);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            return Encoding.UTF8.GetString(buffer, 0, read).Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }
}
