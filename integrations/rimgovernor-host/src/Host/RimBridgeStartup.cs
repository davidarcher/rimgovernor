using System;
using RimGovernor.Host.Sdk;
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
            try
            {
                LaunchStagePatches.Remove();
            }
            catch (Exception ex)
            {
                Log.Warning($"[RimBridge] The launch-stage prefixes stay installed: {ex.Message}");
            }
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
            using (LaunchPhases.Phase("bridge-start.total"))
            {
                using (LaunchPhases.Phase("capabilities.initialize"))
                    RimBridgeCapabilities.Initialize();
                // Mod diagnostics reach Unity's log as warnings only: Log.Error pauses the game.
                RimGovernor.Host.Sdk.ModLog.Sink = (level, component, tick, message, suppressed) =>
                {
                    if (level >= RimGovernor.Host.Sdk.ModLogLevel.Warn)
                        Log.Warning($"[{component}] {message}");
                };
                using (LaunchPhases.Phase("logs.initialize"))
                    RimBridgeLogs.Initialize(RimBridgeCapabilities.LogJournal);
                using (LaunchPhases.Phase("application.run-in-background"))
                    Application.runInBackground = true;
                using (LaunchPhases.Phase("debug.playback-defaults"))
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
