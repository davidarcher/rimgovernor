using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using RimWorld;
using RimGovernor.Host.Core;
using UnityEngine;
using Verse;

namespace RimGovernor.Host;

internal static class RimWorldState
{
    internal readonly struct RuntimeStatus
    {
        public RuntimeStatus(
            string programState,
            bool inEntryScene,
            bool hasCurrentGame,
            int mapCount,
            bool hasCurrentMap,
            bool longEventPending,
            bool paused,
            string timeSpeed,
            bool screenFading,
            float fadeOverlayAlpha)
        {
            ProgramState = programState;
            InEntryScene = inEntryScene;
            HasCurrentGame = hasCurrentGame;
            MapCount = mapCount;
            HasCurrentMap = hasCurrentMap;
            LongEventPending = longEventPending;
            Paused = paused;
            TimeSpeed = timeSpeed;
            ScreenFading = screenFading;
            FadeOverlayAlpha = fadeOverlayAlpha;
        }

        public string ProgramState { get; }

        public bool InEntryScene { get; }

        public bool HasCurrentGame { get; }

        public int MapCount { get; }

        public bool HasCurrentMap { get; }

        public bool LongEventPending { get; }

        public bool Paused { get; }

        public string TimeSpeed { get; }

        public bool ScreenFading { get; }

        public float FadeOverlayAlpha { get; }

        public AutomationReadinessEvaluation Readiness =>
            AutomationReadiness.Evaluate(HasCurrentGame, MapCount, HasCurrentMap, ProgramState, LongEventPending, ScreenFading, FadeOverlayAlpha);
    }

    public static object ToolStateSnapshot()
    {
        return ToolStateSnapshot(ReadStatus());
    }

    public static RuntimeStatus ReadStatus()
    {
        var hasCurrentGame = Current.Game != null;
        var currentMap = Find.CurrentMap;
        var mapCount = hasCurrentGame ? Find.Maps?.Count(map => map != null) ?? 0 : 0;
        var paused = hasCurrentGame && Find.TickManager?.Paused == true;
        var timeSpeed = hasCurrentGame ? Find.TickManager?.CurTimeSpeed.ToString() : null;
        var screenFading = ScreenFader.IsFading();
        var fadeOverlayAlpha = Mathf.Clamp01(ScreenFader.CurrentInstantColor().a);

        return new RuntimeStatus(
            Current.ProgramState.ToString(),
            GenScene.InEntryScene,
            hasCurrentGame,
            mapCount,
            currentMap != null,
            LongEventHandler.AnyEventNowOrWaiting,
            paused,
            timeSpeed,
            screenFading,
            fadeOverlayAlpha);
    }

    public static object ToolStateSnapshot(RuntimeStatus status)
    {
        var readiness = status.Readiness;
        var currentMap = Find.CurrentMap;

        return new
        {
            programState = status.ProgramState,
            inEntryScene = status.InEntryScene,
            hasCurrentGame = status.HasCurrentGame,
            currentMapId = GetMapId(currentMap),
            currentMapIndex = currentMap?.Index,
            mapCount = status.MapCount,
            longEventPending = status.LongEventPending,
            paused = status.Paused,
            timeSpeed = status.TimeSpeed,
            screenFading = status.ScreenFading,
            fadeOverlayAlpha = Math.Round(status.FadeOverlayAlpha, 4),
            gameDataReady = readiness.GameDataReady,
            mapDataReady = readiness.MapDataReady,
            currentMapReady = readiness.CurrentMapReady,
            screenFadeClear = readiness.ScreenFadeClear,
            playable = readiness.Playable,
            visualReady = readiness.VisualReady,
            automationReady = readiness.AutomationReady
        };
    }

    public static string GetMapId(Map map)
    {
        return map?.GetUniqueLoadID();
    }

    public static string SanitizeName(string name, string fallbackPrefix)
    {
        var raw = string.IsNullOrWhiteSpace(name)
            ? $"{fallbackPrefix}_{DateTime.Now:yyyyMMdd_HHmmss}"
            : name.Trim();

        var invalid = Path.GetInvalidFileNameChars();
        var chars = raw.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        return new string(chars);
    }

}
