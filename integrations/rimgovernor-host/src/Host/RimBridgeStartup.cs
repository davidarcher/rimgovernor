using System;
using System.Diagnostics;
using System.Reflection;
using UnityEngine;
using Verse;

namespace RimGovernor.Host;

internal static class RimBridgeStartup
{
    private static readonly object Sync = new();
    private static bool _modConstructed;
    private static bool _runtimeReady;
    private static bool _started;

    public static void OnModConstructed()
    {
        lock (Sync)
        {
            _modConstructed = true;
        }
    }

    public static void OnRuntimeReady()
    {
        var shouldLog = false;
        var alreadyStarted = false;

        lock (Sync)
        {
            if (!_runtimeReady)
            {
                _runtimeReady = true;
                shouldLog = true;
            }

            alreadyStarted = _started;
        }

        if (shouldLog)
        {
            Log.Message(alreadyStarted
                ? "[RimBridge] Play-data load complete; bridge services are already available."
                : "[RimBridge] Startup conditions satisfied after play-data load; initializing bridge services.");
        }

        TryStart();
    }

    private static void TryStart()
    {
        lock (Sync)
        {
            if (!_modConstructed || !_runtimeReady || _started)
                return;

            _started = true;
        }

        try
        {
            using (RimBridgeStartupTiming.Phase("bridge-start.total"))
            {
                using (RimBridgeStartupTiming.Phase("capabilities.initialize"))
                    RimBridgeCapabilities.Initialize();
                // Mod diagnostics reach Unity's log as warnings only: Log.Error pauses the game.
                RimGovernor.Host.Sdk.ModLog.Sink = (level, component, tick, trace, message, suppressed) =>
                {
                    if (level >= RimGovernor.Host.Sdk.ModLogLevel.Warn)
                        Log.Warning($"[{component}] {message}");
                };
                using (RimBridgeStartupTiming.Phase("logs.initialize"))
                    RimBridgeLogs.Initialize(RimBridgeCapabilities.LogJournal);
                using (RimBridgeStartupTiming.Phase("application.run-in-background"))
                    Application.runInBackground = true;
                using (RimBridgeStartupTiming.Phase("debug.playback-defaults"))
                {
                    typeof(DebugViewSettings)
                        .GetField("showTpsCounter", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                        ?.SetValue(null, true);
                    typeof(TickManager)
                        .GetField("UltraSpeedBoost", BindingFlags.Static | BindingFlags.NonPublic)
                        ?.SetValue(null, true);
                }
            }
        }
        catch (Exception ex)
        {
            lock (Sync)
            {
                _started = false;
            }

            Log.Error($"[RimBridge] STARTUP_INIT_FAILURE: {ex}");
            Log.Error($"[RimBridge] Failed to initialize bridge services: {ex}");
        }
    }
}

internal static class RimBridgeStartupTiming
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    public static long ElapsedMilliseconds => Clock.ElapsedMilliseconds;

    public static IDisposable Phase(string name)
    {
        return new PhaseScope(name, Clock.ElapsedMilliseconds);
    }

    public static void LogCompletedPhase(string name, long startedElapsedMilliseconds)
    {
        if (!IsDebugTimingEnabled())
            return;

        var ended = Clock.ElapsedMilliseconds;
        Log.Warning($"[RimBridge] STARTUP_TIMING phase={name} durationMs={ended - startedElapsedMilliseconds} elapsedMs={ended}");
    }

    private static bool IsDebugTimingEnabled()
    {
        try
        {
            return Prefs.DevMode;
        }
        catch
        {
            return false;
        }
    }

    private sealed class PhaseScope : IDisposable
    {
        private readonly string _name;
        private readonly long _startedElapsedMilliseconds;
        private bool _disposed;

        public PhaseScope(string name, long startedElapsedMilliseconds)
        {
            _name = string.IsNullOrWhiteSpace(name) ? "unknown" : name;
            _startedElapsedMilliseconds = startedElapsedMilliseconds;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            LogCompletedPhase(_name, _startedElapsedMilliseconds);
        }
    }
}
