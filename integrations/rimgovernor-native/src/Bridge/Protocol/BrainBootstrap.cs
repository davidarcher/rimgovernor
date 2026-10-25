using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Brain.Host;
using RimGovernor.Brain.HttpChannel;
using RimGovernor.Brain.Telemetry;
using RimGovernor.Host.Sdk;
using Verse;
using Waylith;

namespace RimGovernor.Native
{
    /// <summary>
    /// Starts the in-process brain (#3115) when the game was launched with <see cref="InProcLaunch.Flag"/>; without the
    /// flag no brain thread, database or telemetry file exists. Game cleanup still runs on quit. <see cref="NativeStartup"/> starts
    /// it, and the start does no game work: it
    /// schedules one task that waits (bounded) for the Defs, resolves the launch environment (<see cref="InProcEnvironment"/>),
    /// takes the HTTP channel's route table and starts <see cref="BrainHost"/>, and the host is stopped before engine resource teardown.
    /// Every failure is logged and none is thrown into the loading host.
    /// </summary>
    internal static class BrainBootstrap
    {
        // The game takes minutes to reach its main menu on a cold start; past this the launch is not going to finish.
        private static readonly TimeSpan DefsBudget = TimeSpan.FromMinutes(20);
        private static readonly TimeSpan DefsPoll = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan DefsHopBound = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan RoutesBudget = TimeSpan.FromSeconds(30);
        // Bound a hung shutdown without waiting for the host's default three drain attempts.
        private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan NativeTimeout = TimeSpan.FromSeconds(15);

        private static BrainHost? host;
        private static readonly CancellationTokenSource StartupLifetime = new();
        private static Task startup = Task.CompletedTask;
        private static int stopping;

        /// <summary>The running in-process brain host, null until it has started (the goals panel reads its seam).</summary>
        public static BrainHost? Host => host;

        /// <summary>Called once by <see cref="NativeStartup"/>.</summary>
        internal static void Start()
        {
            EngineApplication.Instance.Runtime.App.ShuttingDown += StopBeforeEngineShutdown;
            if (!InProcLaunch.Requested(Environment.GetCommandLineArgs())) return;
            startup = Task.Run(RunAsync);
        }

        private static async Task RunAsync()
        {
            try
            {
                var defsStart = LaunchPhases.UtcNow;
                var saveData = await WaitForDefsAsync().ConfigureAwait(false);
                LaunchPhases.Record(LaunchPhases.NextId(), 0, "brain.defs_wait", defsStart, LaunchPhases.UtcNow);
                if (saveData == null)
                {
                    Log.Warning("[RimGovernor] The in-process brain did not start: the game's Defs were not loaded in time.");
                    return;
                }
                if (!InProcEnvironment.TryResolve(Environment.GetEnvironmentVariable, saveData, DateTimeOffset.Now, out var inputs, out var error))
                {
                    Log.Warning($"[RimGovernor] The in-process brain did not start: {error}.");
                    return;
                }
                _ = Directory.CreateDirectory((Path.GetDirectoryName((inputs ?? throw new System.InvalidOperationException("Required native value is unavailable: inputs.")).StatePath) ?? throw new System.InvalidOperationException("Required native value is unavailable: Path.GetDirectoryName((inputs ?? throw new System.InvalidOperationException(\"Required native value is unavailable: inputs.\")).StatePath).")));
                _ = Directory.CreateDirectory(inputs.OtlpDirectory);

                var routesStart = LaunchPhases.UtcNow;
                var routes = await WaitForRoutesAsync().ConfigureAwait(false);
                LaunchPhases.Record(LaunchPhases.NextId(), 0, "brain.routes_wait", routesStart, LaunchPhases.UtcNow);
                if (routes == null)
                    Log.Warning($"[RimGovernor] The in-process brain runs without the player API: the HTTP channel is not running ({HttpChannelBootstrap.DirectoryVariable} is unset or the channel did not start).");

                StartupLifetime.Token.ThrowIfCancellationRequested();
                var timings = BrainTimings.Standard(NativeTimeout);
                timings = timings with { ShutdownAttempts = 1, ShutdownAttemptTimeout = TimeSpan.FromSeconds(8) };
                var composition = new BrainComposition(new BrainCompositionOptions
                {
                    LayoutOverlay = inputs.LayoutOverlay,
                    // The acceptance harness narrows the families through the launch environment; unset composes every family.
                    Families = BrainFamilies.FromEnvironment(Environment.GetEnvironmentVariable(BrainFamilies.EnvironmentVariable)),
                    NativeTimeout = NativeTimeout,
                    // The acceptance launch flag the clock runtime also reads; read here too so no game-touching static initialises off the game thread.
                    // A case's RIMGOVERNOR_UNPIN_TEST_SPEED turns off only this Hyperfast pin; the flag still drives native's test acceleration.
                    ClockTestAcceleration = inputs.PinsHyperfast(Array.IndexOf(Environment.GetCommandLineArgs(), "-rimgovernor-test-acceleration") >= 0),
                    CatalogFactory = NativeCatalogFactory.Create,
                    StartingIdeoligion = NativeCatalogFactory.StartingIdeoligion,
                });
                var hostStart = LaunchPhases.UtcNow;
                var started = await BrainHost.StartAsync(new BrainHostOptions
                {
                    StatePath = inputs.StatePath,
                    OtlpDirectory = inputs.OtlpDirectory,
                    RunId = inputs.RunId,
                    Timings = timings,
                    Watchdog = BrainWatchdog.Standard(),
                    Composition = composition,
                    AutoResume = inputs.AutoResume,
                    Routes = routes,
                    Presets = NativeCatalogFactory.Presets,
                }, StartupLifetime.Token).ConfigureAwait(false);
                var brainStarted = LaunchPhases.UtcNow;
                LaunchPhases.Record(LaunchPhases.NextId(), 0, "brain.host_start", hostStart, brainStarted);
                ReplayLaunch(brainStarted);
                host = started;
                Supervisor.AttachRows(started.Telemetry);
                await GameThread.OnGameThread("brain/activity", () => { PawnActivityTracker.Attach(started.Telemetry); return started; }, StartupLifetime.Token, GameThreadQueue.Observation).ConfigureAwait(false);

                Log.Message($"[RimGovernor] The in-process brain started (state {inputs.StatePath}, run {inputs.RunId}, auto-resume {inputs.AutoResume}).");
            }
            catch (OperationCanceledException) when (StartupLifetime.IsCancellationRequested) { }
            catch (Exception e)
            {
                Log.Warning($"[RimGovernor] The in-process brain did not start: {e}");
            }
        }

        /// <summary>
        /// Writes the launch waterfall the host and this bootstrap buffered (#3286) now that the brain's span listener
        /// exists. Diagnostics only: a failure is logged and the brain keeps running.
        /// </summary>
        private static void ReplayLaunch(DateTime brainStarted)
        {
            try
            {
                var phases = LaunchPhases.Snapshot().Select(p => new LaunchInterval(p.Id, p.Parent, p.Name, p.StartUtc, p.EndUtc)).ToList();
                _ = LaunchSpans.Replay(phases, brainStarted);
                if (LaunchPhases.Dropped > 0)
                    Log.Warning($"[RimGovernor] The launch waterfall dropped {LaunchPhases.Dropped} phases past its {LaunchPhases.Capacity}-phase buffer.");
            }
            catch (Exception e)
            {
                Log.Warning($"[RimGovernor] The launch waterfall was not written: {e.Message}");
            }
        }

        private static void StopBeforeEngineShutdown()
        {
            if (Interlocked.Exchange(ref stopping, 1) != 0) return;
            try
            {
                if (!UnityData.IsInMainThread)
                    throw new InvalidOperationException("Engine shutdown must release the game on its main thread.");
                StartupLifetime.Cancel();
                // StartAsync can still be completing. Join it before reading host, and run
                // StopAsync off-thread because it can synchronously join the scheduler.
                var stop = Task.Run(async () =>
                {
                    await startup.ConfigureAwait(false);
                    if (host is { } started) await started.StopAsync().ConfigureAwait(false);
                });
                ShutdownDrain.Run(stop, () => GameThreadQueue.Frame(false), () =>
                {
                    Current.Game?.Dispose();
                    Current.Game = null;
                }, ExitWait);
                StartupLifetime.Dispose();
            }
            catch (Exception e)
            {
                Log.Error($"[RimGovernor] Cleanup before engine shutdown failed: {e}");
            }
        }
        /// <summary>
        /// Polls the game thread until the Defs are loaded; returns the game's save-data folder, or null on timeout. The
        /// game-thread access exists only once the host has set it up, so its absence is the same as "not yet".
        /// </summary>
        private static async Task<string?> WaitForDefsAsync()
        {
            var deadline = DateTime.UtcNow + DefsBudget;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using var bound = CancellationTokenSource.CreateLinkedTokenSource(StartupLifetime.Token);
                    bound.CancelAfter(DefsHopBound);
                    var state = await GameThread.OnGameThread("brain/defs_ready", ReadReadiness, bound.Token, GameThreadQueue.Observation).ConfigureAwait(false);
                    if (state.Loaded) return state.SaveDataFolder;
                }
                catch (Exception e) when (e is InvalidOperationException or OperationCanceledException or TimeoutException)
                {
                    // not ready: the game-thread access is not set up yet, or the thread has not drained the hop
                }
                await Task.Delay(DefsPoll, StartupLifetime.Token).ConfigureAwait(false);
            }
            return null;
        }

        private static Readiness ReadReadiness()
            => new Readiness(PlayDataLoader.Loaded && DefDatabase<ThingDef>.DefCount > 0, GenFilePaths.SaveDataFolderPath);

        private static async Task<RouteTable?> WaitForRoutesAsync()
        {
            var published = HttpChannelBootstrap.Routes;
            return await Task.WhenAny(published, Task.Delay(RoutesBudget, StartupLifetime.Token)).ConfigureAwait(false) == published ? await published.ConfigureAwait(false) : null;
        }

        private sealed record Readiness(bool Loaded, string SaveDataFolder);
    }
}
