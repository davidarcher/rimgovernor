#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    public sealed class NativeAuthorityHookHealth
    {
        internal NativeAuthorityHookHealth(int verified, int required, string detail)
        { VerifiedTargets = verified; RequiredTargets = required; Detail = detail; }
        public bool Ready => RequiredTargets > 0 && VerifiedTargets == RequiredTargets;
        public int VerifiedTargets { get; }
        public int RequiredTargets { get; }
        public string Detail { get; }
    }

    /// <summary>Native invalidation only. No hook acquires or renews authority.</summary>
    [StaticConstructorOnStartup]
    public static class NativeAuthorityHooks
    {
        private const string Owner = "rimgovernor.native.authority";
        private sealed class Target
        {
            internal Target(MethodBase method, MethodInfo? prefix, MethodInfo? postfix)
            { Method = method; Prefix = prefix; Postfix = postfix; }
            internal readonly MethodBase Method;
            internal readonly MethodInfo? Prefix;
            internal readonly MethodInfo? Postfix;
        }
        private static readonly List<Target> Targets = new List<Target>();
        private const int Required = 11;
        private static string installationFailure = "";
        static NativeAuthorityHooks()
        {
            try
            {
                var harmony = new Harmony(Owner);
                Add(harmony, AccessTools.Method(typeof(Pawn_JobTracker), "TryTakeOrderedJob", new[] { typeof(Job), typeof(JobTag?), typeof(bool) }), null, nameof(OrderedJob));
                Add(harmony, AccessTools.Method(typeof(Pawn_WorkSettings), "SetPriority", new[] { typeof(WorkTypeDef), typeof(int) }), nameof(BeforeWork), nameof(AfterWork));
                Add(harmony, AccessTools.PropertySetter(typeof(Pawn_DraftController), "Drafted"), nameof(BeforeDraft), nameof(AfterDraft));
                Add(harmony, AccessTools.Method(typeof(GenConstruct), "PlaceBlueprintForBuild", new[] { typeof(BuildableDef), typeof(IntVec3), typeof(Map), typeof(Rot4), typeof(Faction), typeof(ThingDef), typeof(Precept_ThingStyle), typeof(ThingStyleDef), typeof(bool) }), null, nameof(Built));
                Add(harmony, AccessTools.Method(typeof(GenConstruct), "PlaceBlueprintForInstall", new[] { typeof(MinifiedThing), typeof(IntVec3), typeof(Map), typeof(Rot4), typeof(Faction), typeof(bool) }), null, nameof(Installed));
                Add(harmony, AccessTools.Method(typeof(GenConstruct), "PlaceBlueprintForReinstall", new[] { typeof(Building), typeof(IntVec3), typeof(Map), typeof(Rot4), typeof(Faction), typeof(bool) }), null, nameof(Installed));
                Add(harmony, AccessTools.Method(typeof(Designator_Cancel), "DesignateThing", new[] { typeof(Thing) }), nameof(BeforeCancelThing), nameof(AfterCancelThing));
                Add(harmony, AccessTools.Method(typeof(Designator_Cancel), "DesignateSingleCell", new[] { typeof(IntVec3) }), nameof(BeforeCancelCell), nameof(AfterCancelCell));
                Add(harmony, AccessTools.PropertySetter(typeof(Current), "Game"), nameof(BeforeGame), nameof(AfterGame));
                Add(harmony, AccessTools.PropertySetter(typeof(Game), "CurrentMap"), nameof(BeforeMap), nameof(AfterMap));
                Add(harmony, AccessTools.Method(typeof(Game), "UpdatePlay", Type.EmptyTypes), nameof(Update), null);
            }
            catch (Exception ex)
            {
                installationFailure = "Authority invalidation hook installation failed: " + ex.GetType().Name;
                Log.Error("[RimGovernor] " + installationFailure + "\n" + ex);
            }
        }

        private static void Add(Harmony harmony, MethodBase? method, string? prefix, string? postfix)
        {
            if (method == null) throw new MissingMethodException("Required native authority hook target missing");
            var before = prefix == null ? null : AccessTools.Method(typeof(NativeAuthorityHooks), prefix);
            var after = postfix == null ? null : AccessTools.Method(typeof(NativeAuthorityHooks), postfix);
            harmony.Patch(method, before == null ? null : new HarmonyMethod(before), after == null ? null : new HarmonyMethod(after));
            Targets.Add(new Target(method, before, after));
        }

        public static NativeAuthorityHookHealth Health
        {
            get
            {
                int verified = 0;
                try
                {
                    foreach (var target in Targets)
                    {
                        var patches = Harmony.GetPatchInfo(target.Method);
                        if (patches != null
                            && (target.Prefix == null || patches.Prefixes.Any(p => p.owner == Owner && p.PatchMethod == target.Prefix))
                            && (target.Postfix == null || patches.Postfixes.Any(p => p.owner == Owner && p.PatchMethod == target.Postfix))) verified++;
                    }
                }
                catch { return new NativeAuthorityHookHealth(0, Required, "Native authority hook verification failed"); }
                return new NativeAuthorityHookHealth(verified, Required, verified == Required ? "" :
                    installationFailure.Length == 0 ? "Required native authority hooks are not installed" : installationFailure);
            }
        }

        /// <summary>Call on the main thread before trusted admission; UpdatePlay also polls while paused.</summary>
        public static NativeControlSnapshot? InitializeForCurrentGame()
        {
            if (!UnityData.IsInMainThread) throw new InvalidOperationException("Authority initialization requires the game thread");
            var game = Current.Game;
            return game == null ? null : NativeControlAuthority.ForGame(game).SetHookHealth(Health.Ready);
        }

        private static void Update() => InitializeForCurrentGame();
        private static void Revoke(NativeControlRevocationReason reason)
        {
            var game = Current.Game;
            if (game != null && NativeControlAuthority.TryGetForGame(game, out var state)) state!.RevokeExternal(reason);
        }
        private static void OrderedJob(bool __result)
        { if (__result) Revoke(NativeControlRevocationReason.ExternalOrder); }
        private static void BeforeWork(Pawn_WorkSettings __instance, WorkTypeDef __0, out int __state) => __state = __instance.Initialized ? __instance.GetPriority(__0) : -1;
        private static void AfterWork(Pawn_WorkSettings __instance, WorkTypeDef __0, int __state)
        { if (__instance.Initialized && __state != __instance.GetPriority(__0)) Revoke(NativeControlRevocationReason.PlayerControl); }
        private static void BeforeDraft(Pawn_DraftController __instance, out bool __state) => __state = __instance.Drafted;
        private static void AfterDraft(Pawn_DraftController __instance, bool __state)
        { if (__state != __instance.Drafted) Revoke(NativeControlRevocationReason.PlayerControl); }
        private static void Built(Blueprint_Build __result, Faction faction)
        { if (__result != null && faction == Faction.OfPlayer) Revoke(NativeControlRevocationReason.ExternalOrder); }
        private static void Installed(Blueprint_Install __result, Faction faction)
        { if (__result != null && faction == Faction.OfPlayer) Revoke(NativeControlRevocationReason.ExternalOrder); }

        private sealed class Cancellation
        {
            internal Map Map = null!;
            internal IntVec3 Cell;
            internal int Count;
            internal bool Accepted;
        }
        private static int CancelCount(Map map, Thing thing, IntVec3 cell)
            => map.designationManager.AllDesignationsOn(thing).Count(d => d.def.designateCancelable)
                + map.designationManager.AllDesignationsAt(cell).Count(d => d.def.designateCancelable);
        private static void BeforeCancelThing(Designator_Cancel __instance, Thing t, out Cancellation __state)
        {
            __state = new Cancellation { Map = __instance.Map, Cell = t.Position,
                Accepted = !t.Destroyed && __instance.CanDesignateThing(t).Accepted };
            if (__state.Accepted) __state.Count = CancelCount(__state.Map, t, __state.Cell);
        }
        private static void AfterCancelThing(Thing t, Cancellation __state)
        {
            if (__state.Accepted && (t.Destroyed || CancelCount(__state.Map, t, __state.Cell) < __state.Count))
                Revoke(NativeControlRevocationReason.ExternalOrder);
        }
        private static void BeforeCancelCell(Designator_Cancel __instance, IntVec3 c, out int __state)
            => __state = __instance.Map.designationManager.AllDesignationsAt(c).Count(d => d.def.designateCancelable);
        private static void AfterCancelCell(Designator_Cancel __instance, IntVec3 c, int __state)
        {
            if (__instance.Map.designationManager.AllDesignationsAt(c).Count(d => d.def.designateCancelable) < __state)
                Revoke(NativeControlRevocationReason.ExternalOrder);
        }
        private static void BeforeGame(out Game? __state) => __state = Current.Game;
        private static void AfterGame(Game? __state)
        { if (!ReferenceEquals(__state, Current.Game)) Invalidate(__state); }
        private static void BeforeMap(Game __instance, out Map? __state) => __state = __instance.CurrentMap;
        private static void AfterMap(Game __instance, Map? __state)
        { if (!ReferenceEquals(__state, __instance.CurrentMap)) Invalidate(__instance); }
        private static void Invalidate(Game? game)
        {
            if (game != null && NativeControlAuthority.TryGetForGame(game, out var state)) state!.RequestContextInvalidation();
        }
    }
}
