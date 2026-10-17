#nullable enable
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

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// Starts the in-process brain (#3115) when the game was launched with <see cref="InProcLaunch.Flag"/>; without the
    /// flag nothing here runs, so no brain thread, database or telemetry file exists. Like the other two bootstraps this
    /// is a module initializer, because the Bridge has no game-side startup hook. The initializer does no game work: it
    /// schedules one task that waits (bounded) for the Defs, resolves the launch environment (<see cref="InProcEnvironment"/>),
    /// takes the HTTP channel's route table and starts <see cref="BrainHost"/>, and the host is stopped on process exit.
    /// Every failure is logged and none is thrown into the loading host.
    /// </summary>
    internal static class BrainBootstrap
    {
        // The game takes minutes to reach its main menu on a cold start; past this the launch is not going to finish.
        private static readonly TimeSpan DefsBudget = TimeSpan.FromMinutes(20);
        private static readonly TimeSpan DefsPoll = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan DefsHopBound = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan RoutesBudget = TimeSpan.FromSeconds(30);
        // ProcessExit holds the game's exit hostage, so the shutdown is shorter than the host's own default (3 x 10 s).
        private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan NativeTimeout = TimeSpan.FromSeconds(15);

        private static BrainHost? host;
        private static HuntEstimateService? huntEstimates;

#pragma warning disable CA2255 // See NativeFacadeBootstrap: the host has no startup hook for a plugin assembly.
        [ModuleInitializer]
        internal static void Start()
        {
            if (!InProcLaunch.Requested(Environment.GetCommandLineArgs())) return;
            _ = Task.Run(RunAsync);
        }
#pragma warning restore CA2255

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
                _ = Directory.CreateDirectory(Path.GetDirectoryName(inputs!.StatePath)!);
                _ = Directory.CreateDirectory(inputs.ProfileDirectory);
                _ = Directory.CreateDirectory(inputs.OtlpDirectory);

                var routesStart = LaunchPhases.UtcNow;
                var routes = await WaitForRoutesAsync().ConfigureAwait(false);
                LaunchPhases.Record(LaunchPhases.NextId(), 0, "brain.routes_wait", routesStart, LaunchPhases.UtcNow);
                if (routes == null)
                    Log.Warning($"[RimGovernor] The in-process brain runs without the player API: the HTTP channel is not running ({HttpChannelBootstrap.DirectoryVariable} is unset or the channel did not start).");

                var estimates = await StartHuntEstimatesAsync(inputs).ConfigureAwait(false);
                huntEstimates = estimates;
                var timings = BrainTimings.Standard(NativeTimeout);
                timings = timings with { ShutdownAttempts = 1, ShutdownAttemptTimeout = TimeSpan.FromSeconds(8) };
                var composition = new BrainComposition(new BrainCompositionOptions
                {
                    Profile = inputs.ProfileDirectory,
                    LayoutOverlay = inputs.LayoutOverlay,
                    // The acceptance harness narrows the families through the launch environment; unset composes every family.
                    Families = BrainFamilies.FromEnvironment(Environment.GetEnvironmentVariable(BrainFamilies.EnvironmentVariable)),
                    NativeTimeout = NativeTimeout,
                    // The acceptance launch flag the clock runtime also reads; read here too so no game-touching static initialises off the game thread.
                    ClockTestAcceleration = Array.IndexOf(Environment.GetCommandLineArgs(), "-rimgovernor-test-acceleration") >= 0,
                    CatalogFactory = NativeCatalogFactory.Create,
                    HuntEstimates = estimates,
                    StartingIdeoligion = NativeCatalogFactory.StartingIdeoligion,
                });
                var hostStart = LaunchPhases.UtcNow;
                var started = await BrainHost.StartAsync(new BrainHostOptions
                {
                    StatePath = inputs.StatePath,
                    ProfileDirectory = inputs.ProfileDirectory,
                    OtlpDirectory = inputs.OtlpDirectory,
                    RunId = inputs.RunId,
                    Timings = timings,
                    Watchdog = BrainWatchdog.Standard(),
                    Composition = composition,
                    AutoResume = inputs.AutoResume,
                    Routes = routes,
                }, CancellationToken.None).ConfigureAwait(false);
                var brainStarted = LaunchPhases.UtcNow;
                LaunchPhases.Record(LaunchPhases.NextId(), 0, "brain.host_start", hostStart, brainStarted);
                ReplayLaunch(brainStarted);
                host = started;
                await ProtoBoundary.OnGameThread("brain/activity", () => { PawnActivityTracker.Attach(started.Telemetry); return started; }, CancellationToken.None, MainThreadAdmission.Observation).ConfigureAwait(false);
                AppDomain.CurrentDomain.ProcessExit += (_, _) => StopOnExit(started);
                Log.Message($"[RimGovernor] The in-process brain started (state {inputs.StatePath}, run {inputs.RunId}, auto-resume {inputs.AutoResume}).");
            }
            catch (Exception e)
            {
                Log.Warning($"[RimGovernor] The in-process brain did not start: {e}");
            }
        }

        /// <summary>
        /// Hashes the engine identity off the game thread and, when a prediction worker is configured, starts the hunt estimate
        /// service for it. Any failure leaves the brain without estimates: a hunt roster is then unestimated, never zero risk.
        /// </summary>
        private static async Task<HuntEstimateService?> StartHuntEstimatesAsync(InProcInputs inputs)
        {
            try
            {
                var roots = await ProtoBoundary.OnGameThread("brain/engine_identity", NativeEncounterIdentity.Snapshot, CancellationToken.None, MainThreadAdmission.Observation).ConfigureAwait(false);
                var identity = await NativeEncounterIdentity.ComputeAsync(roots, CancellationToken.None).ConfigureAwait(false);
                var service = HuntEstimateService.Create(Environment.GetEnvironmentVariable, identity.GameBuild, identity.Mods, identity.PredictorMods, identity.GameDirectory, inputs.OtlpDirectory, out var reason);
                if (service is null) Log.Message($"[RimGovernor] Hunt rosters have no outcome estimates: {reason}.");
                return service;
            }
            catch (Exception e)
            {
                Log.Warning($"[RimGovernor] Hunt rosters have no outcome estimates: {e.Message}");
                return null;
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

        private static void StopOnExit(BrainHost started)
        {
            try
            {
                if (!started.StopAsync().Wait(ExitWait)) Log.Warning("[RimGovernor] The in-process brain did not stop within the exit budget.");
                if (huntEstimates is { } estimates && !estimates.DisposeAsync().AsTask().Wait(ExitWait)) Log.Warning("[RimGovernor] The hunt estimate service did not stop within the exit budget.");
            }
            catch (Exception e)
            {
                Log.Warning($"[RimGovernor] The in-process brain stopped with an error: {e.Message}");
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
                    using var bound = new CancellationTokenSource(DefsHopBound);
                    var state = await ProtoBoundary.OnGameThread("brain/defs_ready", ReadReadiness, bound.Token, MainThreadAdmission.Observation).ConfigureAwait(false);
                    if (state.Loaded) return state.SaveDataFolder;
                }
                catch (Exception e) when (e is InvalidOperationException or OperationCanceledException or TimeoutException)
                {
                    // not ready: the game-thread access is not set up yet, or the thread has not drained the hop
                }
                await Task.Delay(DefsPoll).ConfigureAwait(false);
            }
            return null;
        }

        private static Readiness ReadReadiness()
            => new Readiness(PlayDataLoader.Loaded && DefDatabase<ThingDef>.DefCount > 0, GenFilePaths.SaveDataFolderPath);

        private static async Task<RouteTable?> WaitForRoutesAsync()
        {
            var published = HttpChannelBootstrap.Routes;
            return await Task.WhenAny(published, Task.Delay(RoutesBudget)).ConfigureAwait(false) == published ? await published.ConfigureAwait(false) : null;
        }

        private sealed record Readiness(bool Loaded, string SaveDataFolder);
    }
}
