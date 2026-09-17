#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
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

    /// <summary>One required hook's installation state, for diagnostics.</summary>
    public sealed class NativeAuthorityHookStatus
    {
        internal NativeAuthorityHookStatus(string name, MethodBase? method, bool installed, string error)
        { Name = name; Method = method; Installed = installed; Error = error; }
        public string Name { get; }
        /// <summary>The patched game method, or null when it could not be resolved.</summary>
        public MethodBase? Method { get; }
        public bool Installed { get; }
        public string Error { get; }
    }

    /// <summary>
    /// Native invalidation only. No hook acquires or renews authority.
    /// Installation is per hook and re-attemptable: a hook that failed to
    /// resolve or patch at startup, or was removed since, is retried by
    /// <see cref="Install"/> without touching the hooks already in place, so a
    /// partial install recovers on the next admission instead of needing a
    /// game restart.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class NativeAuthorityHooks
    {
        public const string Owner = "rimgovernor.native.authority";
        private sealed class Spec
        {
            internal Spec(string name, Func<MethodBase?> resolve, string? prefix, string? postfix)
            { Name = name; Resolve = resolve; Prefix = prefix; Postfix = postfix; }
            internal readonly string Name;
            internal readonly Func<MethodBase?> Resolve;
            internal readonly string? Prefix;
            internal readonly string? Postfix;
            internal MethodBase? Method;
            internal string Error = "";
        }
        private static readonly object Sync = new object();
        private static readonly List<Spec> Specs = new List<Spec>
        {
            new Spec("Pawn_JobTracker.TryTakeOrderedJob", () => AccessTools.Method(typeof(Pawn_JobTracker), "TryTakeOrderedJob", new[] { typeof(Job), typeof(JobTag?), typeof(bool) }), null, nameof(OrderedJob)),
            new Spec("Pawn_WorkSettings.SetPriority", () => AccessTools.Method(typeof(Pawn_WorkSettings), "SetPriority", new[] { typeof(WorkTypeDef), typeof(int) }), nameof(BeforeWork), nameof(AfterWork)),
            new Spec("Zone_Growing.SetPlantDefToGrow", () => AccessTools.Method(typeof(Zone_Growing), "SetPlantDefToGrow", new[] { typeof(ThingDef) }), nameof(BeforeCrop), nameof(AfterCrop)),
            new Spec("Zone.AddCell", () => AccessTools.Method(typeof(Zone), "AddCell", new[] { typeof(IntVec3) }), nameof(BeforeZoneCell), nameof(AfterZoneCell)),
            new Spec("Zone.RemoveCell", () => AccessTools.Method(typeof(Zone), "RemoveCell", new[] { typeof(IntVec3) }), nameof(BeforeZoneCell), nameof(AfterZoneCell)),
            new Spec("ZoneManager.DeregisterZone", () => AccessTools.Method(typeof(ZoneManager), "DeregisterZone", new[] { typeof(Zone) }), nameof(BeforeZoneRemoval), nameof(AfterZoneRemoval)),
            new Spec("Command_Toggle.ProcessInput", () => AccessTools.Method(typeof(Command_Toggle), "ProcessInput", new[] { AccessTools.TypeByName("UnityEngine.Event") ?? throw new TypeLoadException("UnityEngine.Event") }), null, nameof(PlayerToggle)),
            new Spec("Pawn_DraftController.Drafted", () => AccessTools.PropertySetter(typeof(Pawn_DraftController), "Drafted"), nameof(BeforeDraft), nameof(AfterDraft)),
            new Spec("GenConstruct.PlaceBlueprintForBuild", () => AccessTools.Method(typeof(GenConstruct), "PlaceBlueprintForBuild", new[] { typeof(BuildableDef), typeof(IntVec3), typeof(Map), typeof(Rot4), typeof(Faction), typeof(ThingDef), typeof(Precept_ThingStyle), typeof(ThingStyleDef), typeof(bool) }), null, nameof(Built)),
            new Spec("GenConstruct.PlaceBlueprintForInstall", () => AccessTools.Method(typeof(GenConstruct), "PlaceBlueprintForInstall", new[] { typeof(MinifiedThing), typeof(IntVec3), typeof(Map), typeof(Rot4), typeof(Faction), typeof(bool) }), null, nameof(Installed)),
            new Spec("GenConstruct.PlaceBlueprintForReinstall", () => AccessTools.Method(typeof(GenConstruct), "PlaceBlueprintForReinstall", new[] { typeof(Building), typeof(IntVec3), typeof(Map), typeof(Rot4), typeof(Faction), typeof(bool) }), null, nameof(Installed)),
            new Spec("Designator_Cancel.DesignateThing", () => AccessTools.Method(typeof(Designator_Cancel), "DesignateThing", new[] { typeof(Thing) }), nameof(BeforeCancelThing), nameof(AfterCancelThing)),
            new Spec("Designator_Cancel.DesignateSingleCell", () => AccessTools.Method(typeof(Designator_Cancel), "DesignateSingleCell", new[] { typeof(IntVec3) }), nameof(BeforeCancelCell), nameof(AfterCancelCell)),
            new Spec("Current.Game", () => AccessTools.PropertySetter(typeof(Current), "Game"), nameof(BeforeGame), nameof(AfterGame)),
            new Spec("Game.CurrentMap", () => AccessTools.PropertySetter(typeof(Game), "CurrentMap"), nameof(BeforeMap), nameof(AfterMap)),
            new Spec("Game.UpdatePlay", () => AccessTools.Method(typeof(Game), "UpdatePlay", Type.EmptyTypes), nameof(Update), null),
            // Bills: RimGovernor's own writes (NativeProductionBills.Execute) run inside authority.Owned(),
            // which suppresses these; any other caller (player UI, home/bills) is treated as external.
            new Spec("BillStack.AddBill", () => AccessTools.Method(typeof(BillStack), "AddBill", new[] { typeof(Bill) }), null, nameof(ExternalBillWrite)),
            new Spec("BillStack.Delete", () => AccessTools.Method(typeof(BillStack), "Delete", new[] { typeof(Bill) }), null, nameof(ExternalBillWrite)),
            new Spec("BillStack.Reorder", () => AccessTools.Method(typeof(BillStack), "Reorder", new[] { typeof(Bill), typeof(int) }), null, nameof(ExternalBillWrite)),
            // Bill.suspended has no setter; DoInterface's row toggle is the only mutation point outside the config dialog.
            new Spec("Bill.DoInterface", () => AccessTools.Method(typeof(Bill), "DoInterface", new[] { typeof(float), typeof(float), typeof(float), typeof(int) }), nameof(BeforeBillInterface), nameof(AfterBillInterface)),
            // Dialog_BillConfig writes every other Bill_Production field directly with no setters; snapshot-diff the whole config.
            new Spec("Dialog_BillConfig.DoWindowContents", () => AccessTools.Method(typeof(Dialog_BillConfig), "DoWindowContents", new[] { typeof(Rect) }), nameof(BeforeBillConfig), nameof(AfterBillConfig)),
        };
        private static int Required => Specs.Count;
        static NativeAuthorityHooks() => Install();

        /// <summary>
        /// Patch every required hook that is not currently in place. Hooks
        /// already verified are left alone; each failure is recorded on its
        /// own hook and never blocks the rest. A target that failed to resolve
        /// is not retried: game assemblies do not change within a process, so
        /// that is a restart-required fault, and runtime_health says so.
        /// Returns the resulting health.
        /// </summary>
        public static NativeAuthorityHookHealth Install()
        {
            lock (Sync)
            {
                var harmony = new Harmony(Owner);
                foreach (var spec in Specs)
                {
                    if (spec.Method == null && spec.Error.Length > 0) continue;
                    try
                    {
                        if (spec.Method == null) spec.Method = spec.Resolve() ?? throw new MissingMethodException("Required native authority hook target missing: " + spec.Name);
                        if (Verified(spec)) { spec.Error = ""; continue; }
                        var before = spec.Prefix == null ? null : AccessTools.Method(typeof(NativeAuthorityHooks), spec.Prefix);
                        var after = spec.Postfix == null ? null : AccessTools.Method(typeof(NativeAuthorityHooks), spec.Postfix);
                        harmony.Patch(spec.Method, before == null ? null : new HarmonyMethod(before), after == null ? null : new HarmonyMethod(after));
                        spec.Error = Verified(spec) ? "" : "Patched but not verified";
                    }
                    catch (Exception ex)
                    {
                        var error = ex.GetType().Name + ": " + ex.Message;
                        if (error != spec.Error) Log.Error("[RimGovernor] Authority invalidation hook " + spec.Name + " installation failed: " + ex);
                        spec.Error = error;
                    }
                }
                return Health;
            }
        }

        private static bool Verified(Spec spec)
        {
            if (spec.Method == null) return false;
            var patches = Harmony.GetPatchInfo(spec.Method);
            if (patches == null) return false;
            var before = spec.Prefix == null ? null : AccessTools.Method(typeof(NativeAuthorityHooks), spec.Prefix);
            var after = spec.Postfix == null ? null : AccessTools.Method(typeof(NativeAuthorityHooks), spec.Postfix);
            return (before == null || patches.Prefixes.Any(p => p.owner == Owner && p.PatchMethod == before))
                && (after == null || patches.Postfixes.Any(p => p.owner == Owner && p.PatchMethod == after));
        }

        /// <summary>Per-hook state, in installation order. Reads never install.</summary>
        public static IReadOnlyList<NativeAuthorityHookStatus> Statuses
        {
            get
            {
                lock (Sync)
                {
                    var rows = new List<NativeAuthorityHookStatus>(Specs.Count);
                    foreach (var spec in Specs)
                    {
                        bool installed;
                        try { installed = Verified(spec); } catch { installed = false; }
                        rows.Add(new NativeAuthorityHookStatus(spec.Name, spec.Method, installed, spec.Error));
                    }
                    return rows;
                }
            }
        }

        public static NativeAuthorityHookHealth Health
        {
            get
            {
                try
                {
                    var rows = Statuses;
                    var verified = rows.Count(r => r.Installed);
                    var firstError = rows.FirstOrDefault(r => !r.Installed && r.Error.Length > 0);
                    return new NativeAuthorityHookHealth(verified, Required, verified == Required ? "" :
                        firstError == null ? "Required native authority hooks are not installed" : firstError.Name + ": " + firstError.Error);
                }
                catch { return new NativeAuthorityHookHealth(0, Required, "Native authority hook verification failed"); }
            }
        }

        /// <summary>Call on the main thread before trusted admission; UpdatePlay also polls while paused.</summary>
        public static NativeControlSnapshot? InitializeForCurrentGame()
        {
            if (!UnityData.IsInMainThread) throw new InvalidOperationException("Authority initialization requires the game thread");
            var game = Current.Game;
            if (game == null) return null;
            // A missing hook is retried here, once per admission or update poll,
            // so a partial startup install or a removed patch recovers in place.
            // The gap is reported before the repair: a player action during it
            // went unobserved, so any active authority must be invalidated.
            var authority = NativeControlAuthority.ForGame(game);
            var health = Health;
            if (!health.Ready)
            {
                authority.SetHookHealth(false);
                health = Install();
            }
            return authority.SetHookHealth(health.Ready);
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
        // Gizmo toggles express player direction, including direct sow/cut field writes.
        private static void PlayerToggle() => Revoke(NativeControlRevocationReason.PlayerControl);
        private static readonly FieldInfo CropField = AccessTools.Field(typeof(Zone_Growing), "plantDefToGrow");
        private static void BeforeCrop(Zone_Growing __instance, out object? __state) => __state = CropField.GetValue(__instance);
        private static void AfterCrop(Zone_Growing __instance, object? __state)
        { if (!ReferenceEquals(__state, CropField.GetValue(__instance))) Revoke(NativeControlRevocationReason.PlayerControl); }
        private static void BeforeZoneCell(Zone __instance, IntVec3 __0, out bool __state) => __state = __instance.Cells.Contains(__0);
        private static void AfterZoneCell(Zone __instance, IntVec3 __0, bool __state)
        { if (__state != __instance.Cells.Contains(__0)) Revoke(NativeControlRevocationReason.PlayerControl); }
        private static void BeforeZoneRemoval(ZoneManager __instance, Zone __0, out bool __state) => __state = __instance.AllZones.Contains(__0);
        private static void AfterZoneRemoval(ZoneManager __instance, Zone __0, bool __state)
        { if (__state && !__instance.AllZones.Contains(__0)) Revoke(NativeControlRevocationReason.PlayerControl); }
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

        private static void ExternalBillWrite() => Revoke(NativeControlRevocationReason.ExternalOrder);
        private static void BeforeBillInterface(Bill __instance, out bool __state) => __state = __instance.suspended;
        private static void AfterBillInterface(Bill __instance, bool __state)
        { if (__state != __instance.suspended) Revoke(NativeControlRevocationReason.PlayerControl); }

        private struct BillConfigSnapshot
        {
            internal string RepeatMode; internal int RepeatCount; internal int TargetCount; internal bool PauseWhenSatisfied;
            internal int UnpauseWhenYouHave; internal bool IncludeEquipped; internal bool IncludeTainted; internal object? IncludeGroup;
            internal float HpMin; internal float HpMax; internal int QualityMin; internal int QualityMax; internal bool LimitToAllowedStuff;
            internal string StoreMode; internal object? StoreGroup; internal object? PawnRestriction;
            internal bool SlavesOnly; internal bool MechsOnly; internal bool NonMechsOnly;
            internal int SkillMin; internal int SkillMax; internal float SearchRadius; internal bool Suspended; internal string FilterSummary;
        }
        private static BillConfigSnapshot SnapshotBillConfig(Bill_Production bill) => new BillConfigSnapshot
        {
            RepeatMode = bill.repeatMode?.defName ?? "", RepeatCount = bill.repeatCount, TargetCount = bill.targetCount,
            PauseWhenSatisfied = bill.pauseWhenSatisfied, UnpauseWhenYouHave = bill.unpauseWhenYouHave,
            IncludeEquipped = bill.includeEquipped, IncludeTainted = bill.includeTainted, IncludeGroup = bill.GetIncludeSlotGroup(),
            HpMin = bill.hpRange.min, HpMax = bill.hpRange.max, QualityMin = (int)bill.qualityRange.min, QualityMax = (int)bill.qualityRange.max,
            LimitToAllowedStuff = bill.limitToAllowedStuff, StoreMode = bill.GetStoreMode()?.defName ?? "", StoreGroup = bill.GetSlotGroup(),
            PawnRestriction = bill.PawnRestriction, SlavesOnly = bill.SlavesOnly, MechsOnly = bill.MechsOnly, NonMechsOnly = bill.NonMechsOnly,
            SkillMin = bill.allowedSkillRange.min, SkillMax = bill.allowedSkillRange.max, SearchRadius = bill.ingredientSearchRadius,
            Suspended = bill.suspended, FilterSummary = bill.ingredientFilter?.Summary ?? "",
        };
        private static bool BillConfigChanged(BillConfigSnapshot a, BillConfigSnapshot b) =>
            a.RepeatMode != b.RepeatMode || a.RepeatCount != b.RepeatCount || a.TargetCount != b.TargetCount ||
            a.PauseWhenSatisfied != b.PauseWhenSatisfied || a.UnpauseWhenYouHave != b.UnpauseWhenYouHave ||
            a.IncludeEquipped != b.IncludeEquipped || a.IncludeTainted != b.IncludeTainted || !ReferenceEquals(a.IncludeGroup, b.IncludeGroup) ||
            a.HpMin != b.HpMin || a.HpMax != b.HpMax || a.QualityMin != b.QualityMin || a.QualityMax != b.QualityMax ||
            a.LimitToAllowedStuff != b.LimitToAllowedStuff || a.StoreMode != b.StoreMode || !ReferenceEquals(a.StoreGroup, b.StoreGroup) ||
            !ReferenceEquals(a.PawnRestriction, b.PawnRestriction) || a.SlavesOnly != b.SlavesOnly || a.MechsOnly != b.MechsOnly || a.NonMechsOnly != b.NonMechsOnly ||
            a.SkillMin != b.SkillMin || a.SkillMax != b.SkillMax || a.SearchRadius != b.SearchRadius || a.Suspended != b.Suspended || a.FilterSummary != b.FilterSummary;
        private static void BeforeBillConfig(Bill_Production ___bill, out BillConfigSnapshot __state) => __state = SnapshotBillConfig(___bill);
        private static void AfterBillConfig(Bill_Production ___bill, BillConfigSnapshot __state)
        { if (BillConfigChanged(__state, SnapshotBillConfig(___bill))) Revoke(NativeControlRevocationReason.PlayerControl); }
    }
}
