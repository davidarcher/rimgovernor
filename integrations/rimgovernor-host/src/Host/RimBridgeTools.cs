using System.Collections.Generic;
using System.Runtime.CompilerServices;
using RimGovernor.Host.Gab.Tools;
using RimGovernor.Host.Core;

namespace RimGovernor.Host;

public class RimBridgeTools
{
    [Tool("rimgovernor/get_bridge_status", Description = "Get the current bridge and RimWorld state snapshot without mutating game state", Tags = new string[] { "diagnostic", "status", "read-only" })]
    public object GetBridgeStatus()
    {
        return InvokeAlias();
    }

    [Tool("rimgovernor/list_logs", Description = "List recent captured RimWorld and bridge log entries from the in-memory log journal, including operation correlation when available", Tags = new string[] { "diagnostic", "read-only" })]
    public object ListLogs(
        [ToolParameter(Description = "Maximum number of log entries to return")] int limit = 50,
        [ToolParameter(Description = "Minimum level to include: info, warning, error, or fatal")] string minimumLevel = "info",
        [ToolParameter(Description = "Only include log entries with a sequence greater than this cursor")] long afterSequence = 0,
        [ToolParameter(Description = "Optional direct operation id filter")] string operationId = null,
        [ToolParameter(Description = "Optional root operation id filter for grouped script runs or nested flows")] string rootOperationId = null,
        [ToolParameter(Description = "Optional exact capability id filter")] string capabilityId = null)
    {
        return InvokeAlias(Arguments((nameof(limit), limit), (nameof(minimumLevel), minimumLevel), (nameof(afterSequence), afterSequence), (nameof(operationId), operationId), (nameof(rootOperationId), rootOperationId), (nameof(capabilityId), capabilityId)));
    }

    [Tool("rimgovernor/set_time_speed", Description = "Set RimWorld's current time speed directly; RimGovernor.Host enables the boost for speed 4 (Ultrafast) at startup, and this tool can change that state")]
    public object SetTimeSpeed(
        [ToolParameter(Description = "Desired time speed: Paused, Normal, Fast, Superfast, or Ultrafast")] string speed = "Normal",
        [ToolParameter(Description = "When set, change RimWorld's private TickManager.UltraSpeedBoost flag. RimGovernor.Host enables it at startup; leave null to preserve the current value.")] bool? ultraSpeedBoost = null)
    {
        return InvokeAlias(Arguments((nameof(speed), speed), (nameof(ultraSpeedBoost), ultraSpeedBoost)));
    }

    [Tool("rimgovernor/play_for", Description = "Unpause the current game at a requested time speed for a bounded real-time duration, then pause it again; speed 4 (Ultrafast) is boosted by default, and forced-normal-speed slowdown can optionally be suppressed during the run")]
    public object PlayFor(
        [ToolParameter(Description = "Real-time duration in milliseconds to keep the game unpaused before pausing it again")] int durationMs,
        [ToolParameter(Description = "Desired play speed while the game is running: Normal, Fast, Superfast, or Ultrafast")] string speed = "Normal",
        [ToolParameter(Description = "How often to poll playback state while waiting to repause")] int pollIntervalMs = 25,
        [ToolParameter(Description = "When true, temporarily suppress RimWorld's forced-normal-speed slowdown and ensure TickManager.UltraSpeedBoost is enabled while preserving the normal TickManager update path, then restore both prior values. The boost is enabled at bridge startup by default.")] bool forceRequestedSpeed = false)
    {
        return InvokeAlias(Arguments((nameof(durationMs), durationMs), (nameof(speed), speed), (nameof(pollIntervalMs), pollIntervalMs), (nameof(forceRequestedSpeed), forceRequestedSpeed)));
    }

    [Tool("rimgovernor/step_game_ticks", Description = "Advance the paused game by an exact number of ticks, one tick per Unity update frame, mirroring RimWorld's Dev_TickOnce path while preserving render-frame boundaries")]
    public object StepGameTicks(
        [ToolParameter(Description = "Number of game ticks to advance. Each requested tick is executed on a separate Unity update frame.")] int ticks = 1,
        [ToolParameter(Description = "Maximum time to wait in milliseconds before cancelling the step request")] int timeoutMs = 10000,
        [ToolParameter(Description = "How often to poll step completion while waiting")] int pollIntervalMs = 10,
        [ToolParameter(Description = "Set time speed to Paused before stepping. If false, the game must already be paused.")] bool pauseFirst = true,
        [ToolParameter(Description = "Play RimWorld's dev tick-once clock sound for each stepped tick")] bool playSound = false)
    {
        return InvokeAlias(Arguments((nameof(ticks), ticks), (nameof(timeoutMs), timeoutMs), (nameof(pollIntervalMs), pollIntervalMs), (nameof(pauseFirst), pauseFirst), (nameof(playSound), playSound)));
    }

    [Tool("rimgovernor/save_game", Description = "Save the current game to a named save")]
    public object SaveGame([ToolParameter(Description = "Save name without extension")] string saveName)
    {
        return InvokeAlias(Arguments((nameof(saveName), saveName)));
    }

    [Tool("rimgovernor/load_game_ready", Description = "Load a named RimWorld save after verifying every recorded mod is active unless ignoreModCompatibility is true, then wait until the requested readiness level")]
    public object LoadGameReady(
        [ToolParameter(Description = "Save name without extension")] string saveName,
        [ToolParameter(Description = "Maximum time to wait in milliseconds")] int timeoutMs = 120000,
        [ToolParameter(Description = "Polling interval in milliseconds")] int pollIntervalMs = 50,
        [ToolParameter(Description = "Readiness target: gameData, mapData, currentMap, playable, or visual")] string readiness = AutomationReadiness.DefaultTargetName,
        [ToolParameter(Description = "Pause the game before returning success if it is still running")] bool pauseIfNeeded = false,
        [ToolParameter(Description = "Alias for readiness; useful when callers naturally name the requested readiness target explicitly")] string targetReadiness = null,
        [ToolParameter(Description = "Convenience alias that forces readiness to visual when true")] bool waitForVisualReady = false,
        [ToolParameter(Description = "Load even when recorded mods are missing or compatibility metadata cannot be read")] bool ignoreModCompatibility = false)
    {
        return InvokeAlias(Arguments((nameof(saveName), saveName), (nameof(timeoutMs), timeoutMs), (nameof(pollIntervalMs), pollIntervalMs), (nameof(readiness), readiness), (nameof(pauseIfNeeded), pauseIfNeeded), (nameof(targetReadiness), targetReadiness), (nameof(waitForVisualReady), waitForVisualReady), (nameof(ignoreModCompatibility), ignoreModCompatibility)));
    }

    private static object InvokeAlias(Dictionary<string, object> arguments = null, [CallerMemberName] string memberName = null)
    {
        return LegacyToolExecution.InvokeAlias(memberName, arguments);
    }

    private static Dictionary<string, object> Arguments(params (string Name, object Value)[] arguments)
    {
        var result = new Dictionary<string, object>(arguments.Length, System.StringComparer.Ordinal);
        foreach (var argument in arguments)
            result[argument.Name] = argument.Value;

        return result;
    }
}
