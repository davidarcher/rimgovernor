using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using RimGovernor.Host.Core;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimGovernor.Host;

internal sealed class LifecycleCapabilityModule
{
    private static readonly MainThreadDispatcher Dispatcher = new();
    private static readonly ConditionWaiter Waiter = new();
    private static readonly FieldInfo UltraSpeedBoostField = typeof(TickManager).GetField("UltraSpeedBoost", BindingFlags.Static | BindingFlags.NonPublic);

    private sealed class SaveCompatibilityInspection
    {
        public SaveModMetadataReadResult Metadata { get; set; }

        public SaveModCompatibilityResult Compatibility { get; set; }

        public int ActiveModCount { get; set; }

        public object State { get; set; }
    }

    private sealed class SaveCompatibilityRuntimeSnapshot
    {
        public List<string> ActivePackageIds { get; set; } = [];

        public object State { get; set; }
    }

    public object SetTimeSpeed(string speed = "Normal", bool? ultraSpeedBoost = null)
    {
        if (Current.Game == null || Find.TickManager == null)
        {
            return new
            {
                success = false,
                message = "No game is currently loaded.",
                state = RimWorldState.ToolStateSnapshot()
            };
        }

        if (Enum.TryParse<TimeSpeed>(speed, ignoreCase: true, out var parsed) == false)
        {
            return new
            {
                success = false,
                message = $"Unknown time speed '{speed}'. Supported values: {string.Join(", ", Enum.GetNames(typeof(TimeSpeed)))}.",
                state = RimWorldState.ToolStateSnapshot()
            };
        }

        var previousUltraSpeedBoost = TryReadUltraSpeedBoost();
        bool? currentUltraSpeedBoost = null;
        if (ultraSpeedBoost != null)
        {
            TrySetUltraSpeedBoost(ultraSpeedBoost.Value);
            currentUltraSpeedBoost = TryReadUltraSpeedBoost();
        }

        Find.TickManager.CurTimeSpeed = parsed;
        return new
        {
            success = true,
            timeSpeed = Find.TickManager.CurTimeSpeed.ToString(),
            ultraSpeedBoostAvailable = UltraSpeedBoostField != null,
            requestedUltraSpeedBoost = ultraSpeedBoost,
            previousUltraSpeedBoost,
            currentUltraSpeedBoost = currentUltraSpeedBoost ?? TryReadUltraSpeedBoost(),
            paused = Find.TickManager.Paused,
            message = $"Time speed set to {Find.TickManager.CurTimeSpeed}.",
            state = RimWorldState.ToolStateSnapshot()
        };
    }

    public object PlayFor(int durationMs, string speed = "Normal", int pollIntervalMs = 25, bool forceRequestedSpeed = false)
    {
        if (durationMs <= 0)
        {
            return new
            {
                success = false,
                message = "durationMs must be greater than 0.",
                state = RimWorldState.ToolStateSnapshot()
            };
        }

        if (pollIntervalMs < 0)
        {
            return new
            {
                success = false,
                message = "pollIntervalMs cannot be negative.",
                state = RimWorldState.ToolStateSnapshot()
            };
        }

        if (Enum.TryParse<TimeSpeed>(speed, ignoreCase: true, out var parsedSpeed) == false)
        {
            return new
            {
                success = false,
                message = $"Unknown time speed '{speed}'. Supported values: Normal, Fast, Superfast, Ultrafast.",
                state = RimWorldState.ToolStateSnapshot()
            };
        }

        if (parsedSpeed == TimeSpeed.Paused)
        {
            return new
            {
                success = false,
                message = "play_for requires an active play speed. Use Normal, Fast, Superfast, or Ultrafast.",
                state = RimWorldState.ToolStateSnapshot()
            };
        }

        bool? previousNeverForceNormalSpeed = null;
        bool? restoredNeverForceNormalSpeed = null;
        bool? previousUltraSpeedBoost = null;
        bool? restoredUltraSpeedBoost = null;

        try
        {
            Stopwatch stopwatch = null;
            var controller = new TimedPlaybackController();
            var result = controller.PlayFor(
                durationMs,
                pollIntervalMs,
                getElapsedMs: () => stopwatch?.ElapsedMilliseconds ?? 0L,
                readState: () => RimBridgeMainThread.Invoke(CapturePlaybackState, timeoutMs: 5000),
                startPlayback: () => RimBridgeMainThread.Invoke(() =>
                {
                    if (forceRequestedSpeed && previousNeverForceNormalSpeed == null)
                    {
                        previousNeverForceNormalSpeed = DebugViewSettings.neverForceNormalSpeed;
                        DebugViewSettings.neverForceNormalSpeed = true;
                        previousUltraSpeedBoost = TrySetUltraSpeedBoost(true);
                    }
                    stopwatch = Stopwatch.StartNew();
                    EnsurePlaybackRunning(parsedSpeed);
                }, timeoutMs: 5000),
                pausePlayback: () => RimBridgeMainThread.Invoke(() =>
                {
                    try
                    {
                        PauseIfNeeded();
                    }
                    finally
                    {
                        restoredNeverForceNormalSpeed = RestoreNeverForceNormalSpeed(previousNeverForceNormalSpeed);
                        restoredUltraSpeedBoost = RestoreUltraSpeedBoost(previousUltraSpeedBoost);
                    }
                }, timeoutMs: 5000));

            restoredNeverForceNormalSpeed ??= RimBridgeMainThread.Invoke(
                () => RestoreNeverForceNormalSpeed(previousNeverForceNormalSpeed),
                timeoutMs: 5000);
            restoredUltraSpeedBoost ??= RimBridgeMainThread.Invoke(
                () => RestoreUltraSpeedBoost(previousUltraSpeedBoost),
                timeoutMs: 5000);
            var finalNeverForceNormalSpeed = RimBridgeMainThread.Invoke(() => DebugViewSettings.neverForceNormalSpeed, timeoutMs: 5000);
            var finalUltraSpeedBoost = RimBridgeMainThread.Invoke(TryReadUltraSpeedBoost, timeoutMs: 5000);
            var finalTimeSpeed = RimBridgeMainThread.Invoke(() => Find.TickManager?.CurTimeSpeed.ToString(), timeoutMs: 5000);

            return new
            {
                success = result.Success,
                requestedDurationMs = durationMs,
                elapsedMs = result.ElapsedMs,
                pollIntervalMs,
                timeSpeed = parsedSpeed.ToString(),
                forceRequestedSpeed,
                previousNeverForceNormalSpeed,
                restoredNeverForceNormalSpeed,
                finalNeverForceNormalSpeed,
                ultraSpeedBoostAvailable = UltraSpeedBoostField != null,
                previousUltraSpeedBoost,
                restoredUltraSpeedBoost,
                finalUltraSpeedBoost,
                finalTimeSpeed,
                paused = result.PausedAtEnd,
                initiallyPaused = result.InitiallyPaused,
                startTick = result.StartTick,
                endTick = result.EndTick,
                advancedTicks = result.AdvancedTicks,
                attempts = result.Attempts,
                probeFailureCount = result.ProbeFailureCount,
                lastProbeError = string.IsNullOrWhiteSpace(result.LastProbeError) ? null : result.LastProbeError,
                message = result.Message,
                state = result.Snapshot
            };
        }
        catch (Exception ex)
        {
            if (forceRequestedSpeed && previousNeverForceNormalSpeed != null)
            {
                try
                {
                    restoredNeverForceNormalSpeed = RimBridgeMainThread.Invoke(
                        () => RestoreNeverForceNormalSpeed(previousNeverForceNormalSpeed),
                        timeoutMs: 5000);
                    restoredUltraSpeedBoost = RimBridgeMainThread.Invoke(
                        () => RestoreUltraSpeedBoost(previousUltraSpeedBoost),
                        timeoutMs: 5000);
                }
                catch
                {
                    restoredNeverForceNormalSpeed = null;
                    restoredUltraSpeedBoost = null;
                }
            }

            return new
            {
                success = false,
                message = $"Failed to play for {durationMs}ms: {ex.Message}",
                forceRequestedSpeed,
                previousNeverForceNormalSpeed,
                restoredNeverForceNormalSpeed,
                ultraSpeedBoostAvailable = UltraSpeedBoostField != null,
                previousUltraSpeedBoost,
                restoredUltraSpeedBoost,
                state = RimWorldState.ToolStateSnapshot()
            };
        }
    }

    public object StepGameTicks(int ticks = 1, int timeoutMs = 10000, int pollIntervalMs = 10, bool pauseFirst = true, bool playSound = false)
    {
        if (ticks <= 0)
        {
            return new
            {
                success = false,
                message = "ticks must be greater than 0.",
                state = RimWorldState.ToolStateSnapshot()
            };
        }

        if (timeoutMs < 0)
        {
            return new
            {
                success = false,
                message = "timeoutMs cannot be negative.",
                state = RimWorldState.ToolStateSnapshot()
            };
        }

        if (pollIntervalMs < 0)
        {
            return new
            {
                success = false,
                message = "pollIntervalMs cannot be negative.",
                state = RimWorldState.ToolStateSnapshot()
            };
        }

        var start = RimBridgeMainThread.Invoke(() => RimWorldTickStepper.Start(ticks, timeoutMs, pauseFirst, playSound), timeoutMs: 5000);
        if (start.Status != "active")
            return start.ToToolResponse();

        var waiter = new ConditionWaiter();
        var outcome = waiter.WaitUntil(() =>
        {
            var current = RimBridgeMainThread.Invoke(() => RimWorldTickStepper.GetSnapshot(start.StepId), timeoutMs: 5000);
            var terminal = current.Status != "active";
            return new WaitProbeResult
            {
                IsSatisfied = terminal,
                Message = current.Message,
                Snapshot = current
            };
        }, new WaitOptions
        {
            TimeoutMs = timeoutMs,
            PollIntervalMs = pollIntervalMs,
            TimeoutMessage = $"Timed out waiting to advance {ticks} game tick(s).",
            HandleProbeException = ex => ex is TimeoutException
                ? new WaitProbeResult
                {
                    IsSatisfied = false,
                    Message = "Retrying after main-thread timeout."
                }
                : null
        });

        if (outcome.Satisfied && outcome.Snapshot is RimWorldTickStepper.StepSnapshot completed)
            return completed.ToToolResponse();

        return RimBridgeMainThread.Invoke(() => RimWorldTickStepper.Cancel(start.StepId, outcome.Message), timeoutMs: 5000).ToToolResponse();
    }

    public object SaveGame(string saveName)
    {
        if (Current.Game == null)
            return new { success = false, message = "No game is currently loaded." };
        if (GameDataSaveLoader.SavingIsTemporarilyDisabled)
            return new { success = false, message = "RimWorld is temporarily blocking saves because a UI/window state or cutscene prevents saving." };

        var safeName = RimWorldState.SanitizeName(saveName, "rimbridge_save");
        GameDataSaveLoader.SaveGame(safeName);
        var path = GenFilePaths.FilePathForSavedGame(safeName);
        var info = new FileInfo(path);

        return new
        {
            success = true,
            saveName = safeName,
            path,
            exists = info.Exists,
            sizeBytes = info.Exists ? info.Length : 0
        };
    }

    public object LoadGameReady(string saveName, int timeoutMs = 120000, int pollIntervalMs = 50, string readiness = AutomationReadiness.DefaultTargetName, bool pauseIfNeeded = false, string targetReadiness = null, bool waitForVisualReady = false, bool ignoreModCompatibility = false)
    {
        readiness = RimWorldWaits.ResolveReadinessInput(readiness, targetReadiness, waitForVisualReady);
        var operationStopwatch = Stopwatch.StartNew();
        var safeName = RimWorldState.SanitizeName(saveName, "rimbridge_save");
        var path = GenFilePaths.FilePathForSavedGame(safeName);
        if (!File.Exists(path))
        {
            return new { success = false, message = $"Save '{safeName}' does not exist.", path };
        }

        var mainThreadReady = WaitForAutomationMainThread(timeoutMs, pollIntervalMs);
        if (mainThreadReady.Satisfied == false)
        {
            return CreateLoadGameReadyQueueFailure(safeName, path, mainThreadReady);
        }

        var compatibility = InspectSaveCompatibility(path, captureRuntimeOnCurrentThread: false);
        var compatibilityFailure = ignoreModCompatibility
            ? null
            : CreateSaveCompatibilityFailure(safeName, path, compatibility);
        if (compatibilityFailure != null)
            return compatibilityFailure;

        object queuedState;
        try
        {
            queuedState = Dispatcher.Invoke(() =>
            {
                if (Find.WindowStack?.FloatMenu != null)
                    Find.WindowStack.TryRemove(Find.WindowStack.FloatMenu, doCloseSound: false);

                GameDataSaveLoader.LoadGame(safeName);
                return RimWorldState.ToolStateSnapshot();
            }, timeoutMs: ResolveLoadQueueDispatchTimeoutMs(timeoutMs, operationStopwatch.ElapsedMilliseconds));
        }
        catch (Exception ex)
        {
            return CreateLoadGameReadyQueueFailure(safeName, path, mainThreadReady, ex);
        }

        var wait = RimWorldWaits.WaitForGameLoadedResult(ResolveRemainingTimeoutMs(timeoutMs, operationStopwatch.ElapsedMilliseconds), pollIntervalMs, readiness, pauseIfNeeded);
        var success = wait.TryGetValue("success", out var successValue) && successValue is bool satisfied && satisfied;
        wait.TryGetValue("message", out var message);
        wait.TryGetValue("state", out var finalState);

        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["success"] = success,
            ["saveName"] = safeName,
            ["path"] = path,
            ["message"] = message,
            ["compatibilityCheckIgnored"] = ignoreModCompatibility,
            ["compatibility"] = DescribeSaveCompatibility(compatibility),
            ["state"] = finalState ?? queuedState,
            ["load"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["success"] = true,
                ["status"] = "queued",
                ["saveName"] = safeName,
                ["path"] = path,
                ["compatibilityCheckIgnored"] = ignoreModCompatibility,
                ["compatibility"] = DescribeSaveCompatibility(compatibility),
                ["state"] = queuedState
            },
            ["wait"] = wait
        };
    }

    private static SaveCompatibilityInspection InspectSaveCompatibility(string path, bool captureRuntimeOnCurrentThread)
    {
        var runtime = captureRuntimeOnCurrentThread
            ? CaptureSaveCompatibilityRuntimeSnapshot()
            : Dispatcher.Invoke(CaptureSaveCompatibilityRuntimeSnapshot, timeoutMs: 5000);
        return InspectSaveCompatibility(path, runtime);
    }

    private static SaveCompatibilityInspection InspectSaveCompatibility(string path, SaveCompatibilityRuntimeSnapshot runtime)
    {
        var metadata = SaveModCompatibility.ReadMetadata(path);
        return new SaveCompatibilityInspection
        {
            Metadata = metadata,
            Compatibility = SaveModCompatibility.Evaluate(metadata, runtime.ActivePackageIds),
            ActiveModCount = runtime.ActivePackageIds.Count,
            State = runtime.State
        };
    }

    private static SaveCompatibilityRuntimeSnapshot CaptureSaveCompatibilityRuntimeSnapshot()
    {
        return new SaveCompatibilityRuntimeSnapshot
        {
            ActivePackageIds = LoadedModManager.RunningMods
                .Where(mod => mod != null)
                .Select(mod => mod.PackageId)
                .Where(packageId => string.IsNullOrWhiteSpace(packageId) == false)
                .ToList(),
            State = RimWorldState.ToolStateSnapshot()
        };
    }

    private static Dictionary<string, object> CreateSaveCompatibilityFailure(
        string saveName,
        string path,
        SaveCompatibilityInspection inspection)
    {
        if (inspection.Compatibility.IsCompatible)
            return null;

        var missingMods = inspection.Compatibility.MissingMods;
        var hasMissingMods = inspection.Compatibility.Status == SaveModCompatibilityStatus.MissingMods;
        var message = hasMissingMods
            ? $"Save '{saveName}' cannot be loaded because {missingMods.Count} mod(s) recorded by the save are not currently active: {string.Join(", ", missingMods.Select(DescribeMissingModInline))}."
            : $"Save '{saveName}' cannot be loaded because its mod compatibility metadata could not be read: {inspection.Compatibility.MetadataError}";

        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["success"] = false,
            ["code"] = hasMissingMods ? "save.missing_mods" : "save.mod_metadata_unavailable",
            ["message"] = message,
            ["saveName"] = saveName,
            ["path"] = path,
            ["compatibilityCheckIgnored"] = false,
            ["compatibility"] = DescribeSaveCompatibility(inspection),
            ["state"] = inspection.State
        };
    }

    private static string DescribeMissingModInline(SaveModReference mod)
    {
        return string.Equals(mod.Name, mod.PackageId, StringComparison.OrdinalIgnoreCase)
            ? mod.PackageId
            : $"{mod.Name} ({mod.PackageId})";
    }

    private static Dictionary<string, object> DescribeSaveCompatibility(SaveCompatibilityInspection inspection)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["status"] = DescribeSaveCompatibilityStatus(inspection.Compatibility.Status),
            ["compatible"] = inspection.Compatibility.IsCompatible,
            ["metadataStatus"] = DescribeSaveMetadataStatus(inspection.Compatibility.MetadataStatus),
            ["metadataReadable"] = inspection.Compatibility.MetadataReadable,
            ["metadataError"] = string.IsNullOrWhiteSpace(inspection.Compatibility.MetadataError)
                ? null
                : inspection.Compatibility.MetadataError,
            ["recordedModCount"] = inspection.Metadata.Mods.Count,
            ["activeModCount"] = inspection.ActiveModCount,
            ["missingModCount"] = inspection.Compatibility.MissingMods.Count,
            ["missingMods"] = inspection.Compatibility.MissingMods.Select(mod => new
            {
                name = mod.Name,
                packageId = mod.PackageId,
                canonicalPackageId = mod.CanonicalPackageId
            }).ToList()
        };
    }

    private static string DescribeSaveCompatibilityStatus(SaveModCompatibilityStatus status)
    {
        return status switch
        {
            SaveModCompatibilityStatus.Compatible => "compatible",
            SaveModCompatibilityStatus.MissingMods => "missing_mods",
            _ => "metadata_unavailable"
        };
    }

    private static string DescribeSaveMetadataStatus(SaveModMetadataStatus status)
    {
        return status switch
        {
            SaveModMetadataStatus.Readable => "readable",
            SaveModMetadataStatus.MissingModMetadata => "missing_mod_metadata",
            _ => "unreadable"
        };
    }

    private static WaitOutcome WaitForAutomationMainThread(int timeoutMs, int pollIntervalMs)
    {
        return Waiter.WaitUntil(
            () =>
            {
                var state = Dispatcher.Invoke(RimWorldState.ToolStateSnapshot, timeoutMs: ResolveProbeDispatchTimeoutMs(timeoutMs));
                return new WaitProbeResult
                {
                    IsSatisfied = true,
                    Message = "RimWorld main thread accepted automation work.",
                    Snapshot = state
                };
            },
            new WaitOptions
            {
                TimeoutMs = NormalizeTimeoutMs(timeoutMs),
                PollIntervalMs = NormalizePollIntervalMs(pollIntervalMs),
                HandleProbeException = ex => new WaitProbeResult
                {
                    IsSatisfied = false,
                    Message = "RimWorld main thread was busy before queueing save load. Retrying.",
                    BlockingReason = ex.Message
                },
                TimeoutMessage = "Timed out waiting for RimWorld main thread before queueing save load."
            });
    }

    private static Dictionary<string, object> CreateLoadGameReadyQueueFailure(
        string safeName,
        string path,
        WaitOutcome mainThreadReady,
        Exception exception = null)
    {
        var message = exception == null
            ? mainThreadReady.Message
            : $"RimWorld main thread did not queue save '{safeName}': {exception.GetBaseException().Message}";

        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["success"] = false,
            ["saveName"] = safeName,
            ["path"] = path,
            ["message"] = message,
            ["state"] = mainThreadReady.Snapshot,
            ["queue"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["success"] = false,
                ["message"] = message,
                ["attempts"] = mainThreadReady.Attempts,
                ["elapsedMs"] = mainThreadReady.ElapsedMs,
                ["probeFailureCount"] = mainThreadReady.ProbeFailureCount,
                ["lastProbeError"] = string.IsNullOrWhiteSpace(mainThreadReady.LastProbeError) ? null : mainThreadReady.LastProbeError,
                ["blockingReason"] = string.IsNullOrWhiteSpace(mainThreadReady.BlockingReason) ? null : mainThreadReady.BlockingReason
            }
        };
    }

    private static int ResolveProbeDispatchTimeoutMs(int timeoutMs)
    {
        if (timeoutMs <= 0)
            return 0;

        return Math.Min(timeoutMs, 10000);
    }

    private static int ResolveLoadQueueDispatchTimeoutMs(int timeoutMs, long elapsedMs)
    {
        var remainingMs = ResolveRemainingTimeoutMs(timeoutMs, elapsedMs);
        if (remainingMs <= 0)
            return 0;

        return remainingMs <= 10000
            ? remainingMs
            : Math.Min(remainingMs, 30000);
    }

    private static int ResolveRemainingTimeoutMs(int timeoutMs, long elapsedMs)
    {
        if (timeoutMs <= 0)
            return 0;

        return Math.Max(1, timeoutMs - (int)Math.Min(int.MaxValue, elapsedMs));
    }

    private static int NormalizeTimeoutMs(int timeoutMs)
    {
        return Math.Max(0, timeoutMs);
    }

    private static int NormalizePollIntervalMs(int pollIntervalMs)
    {
        return Math.Max(25, pollIntervalMs);
    }

    private static TimedPlaybackState CapturePlaybackState()
    {
        var hasPlayableGame = Current.ProgramState == ProgramState.Playing
            && Current.Game != null
            && Find.TickManager != null;
        var hasLongEvent = LongEventHandler.AnyEventNowOrWaiting;
        var atMainMenu = GenScene.InEntryScene || Current.ProgramState == ProgramState.Entry;

        return new TimedPlaybackState
        {
            Available = hasPlayableGame && !hasLongEvent,
            Paused = hasPlayableGame && Find.TickManager.Paused,
            TickCount = hasPlayableGame ? Find.TickManager.TicksGame : 0,
            SessionToken = Current.Game,
            Snapshot = RimWorldState.ToolStateSnapshot(),
            Message = hasPlayableGame
                ? (hasLongEvent ? "RimWorld is busy with a long event." : string.Empty)
                : (atMainMenu ? "RimWorld returned to the main menu." : "No playable game is currently loaded.")
        };
    }

    private static void EnsurePlaybackRunning(TimeSpeed speed)
    {
        if (Current.ProgramState != ProgramState.Playing || Current.Game == null || Find.TickManager == null)
            throw new InvalidOperationException("No playable game is currently loaded.");
        if (LongEventHandler.AnyEventNowOrWaiting)
            throw new InvalidOperationException("RimWorld is busy with a long event.");

        Find.TickManager.CurTimeSpeed = speed;
        if (Find.TickManager.Paused)
            Find.TickManager.TogglePaused();
    }

    private static void PauseIfNeeded()
    {
        if (Current.Game == null || Find.TickManager == null)
            return;

        if (!Find.TickManager.Paused)
            Find.TickManager.TogglePaused();
    }

    private static bool? RestoreNeverForceNormalSpeed(bool? previousNeverForceNormalSpeed)
    {
        if (previousNeverForceNormalSpeed == null)
            return null;

        DebugViewSettings.neverForceNormalSpeed = previousNeverForceNormalSpeed.Value;
        return DebugViewSettings.neverForceNormalSpeed;
    }

    private static bool? TryReadUltraSpeedBoost()
    {
        return UltraSpeedBoostField?.GetValue(null) as bool?;
    }

    private static bool? TrySetUltraSpeedBoost(bool value)
    {
        var previous = TryReadUltraSpeedBoost();
        UltraSpeedBoostField?.SetValue(null, value);
        return previous;
    }

    private static bool? RestoreUltraSpeedBoost(bool? previousUltraSpeedBoost)
    {
        if (previousUltraSpeedBoost == null)
            return null;

        UltraSpeedBoostField?.SetValue(null, previousUltraSpeedBoost.Value);
        return TryReadUltraSpeedBoost();
    }
}
