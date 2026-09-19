#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    internal interface INativeAcquisitionRecord { Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context); }
    internal sealed class NativeAcquisitionRecord : INativeAcquisitionRecord
    {
        internal readonly Plant Source;
        internal readonly string SourceId, Resource;
        internal readonly IntVec3 Cell;
        internal readonly Map Map;
        internal readonly Dictionary<Thing, int> Outputs = new Dictionary<Thing, int>();
        internal bool Finished, Unreadable;
        internal NativeAcquisitionRecord(Plant source)
        { Source = source; SourceId = source.GetUniqueLoadID(); Resource = source.def.plant.harvestedThingDef.defName; Cell = source.Position; Map = source.Map; }
        internal Receipts.AcquisitionEffect Evidence()
        {
            var result = new Receipts.AcquisitionEffect { SourceId = SourceId, ResourceDef = Resource,
                Cell = new Common.Cell { X = Cell.x, Z = Cell.z }, LaborFinished = Finished,
                Designated = !Source.Destroyed && Source.Spawned && ResourceAcquisitionTools.Designated(Source),
                ProducedUnits = (int)Math.Min(int.MaxValue, Outputs.Values.Sum(v => (long)v)),
                OutputComplete = NativeAcquisitionTracking.Ready && !Unreadable && Outputs.Values.Sum(v => (long)v) <= int.MaxValue,
                OutputObserved = NativeAcquisitionTracking.Ready && !Unreadable && Outputs.Count > 0 };
            if (result.Designated && !Finished)
            {
                var reason = PendingReason(Source, Map);
                if (reason != null) result.PendingReason = reason;
            }
            foreach (var pair in Outputs.OrderBy(p => p.Key.GetUniqueLoadID(), StringComparer.Ordinal))
            {
                var thing = pair.Key;
                result.Outputs.Add(new Receipts.AcquisitionOutput { ThingId = thing.GetUniqueLoadID(), Units = pair.Value });
                if (thing.Destroyed || !thing.Spawned || thing.Map != Map || thing.def.defName != Resource || thing.stackCount < pair.Value
                    || thing.IsForbidden(Faction.OfPlayer) || thing.Position.Fogged(Map)) result.OutputObserved = false;
            }
            return result;
        }
        // Why a still-designated harvest has no worker, mirroring the checks
        // WorkGiver_PlantsCut makes before taking the designation; null when a
        // plant cutter is on it now. Read-only evidence for the pending effect.
        internal static string? PendingReason(Plant source, Map map)
        {
            try
            {
                if (source.IsForbidden(Faction.OfPlayer)) return "source forbidden";
                if (source.Position.Fogged(map)) return "source fogged";
                if (source.IsBurning()) return "source burning";
                if (map.designationManager.DesignationOn(source, DesignationDefOf.HarvestPlant) != null && !source.HarvestableNow)
                    return "below harvest growth (" + source.Growth.ToString("0.00") + ")";
                var work = WorkTypeDefOf.PlantCutting;
                var cutters = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Downed && !p.Drafted && !p.InMentalState
                    && !p.WorkTypeIsDisabled(work) && p.workSettings != null && p.workSettings.WorkIsActive(work)
                    && p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)).ToList();
                if (cutters.Count == 0) return "no enabled plant cutter";
                if (cutters.Any(p => p.CurJob?.targetA.Thing == source)) return null;
                var able = cutters.Where(p => NativePlantAcquisition.Cutter(p, source)).ToList();
                if (able.Count == 0) return cutters.Count + " enabled plant cutter(s), none can reach the source";
                var reserver = map.reservationManager.FirstRespectedReserver(source, able[0]);
                if (reserver != null && !able.Contains(reserver)) return "source reserved by " + BridgeCommon.SafeString(() => reserver.LabelShortCap.ToString());
                return able.Count + " enabled plant cutter(s) busy: " + string.Join(", ", able.Take(4).Select(p =>
                    BridgeCommon.SafeString(() => p.LabelShortCap.ToString()) + "=" + (p.CurJob?.def.defName ?? "idle")));
            }
            catch (Exception error) { return "pending reason unavailable: " + error.GetType().Name; }
        }
        public Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context) => ObservePlant(attempt, context);
        internal static Receipts.Progress Progress(Common.AttemptKey attempt, Common.ObservationContext context, Receipts.AcquisitionEffect observed)
        {
            var result = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = true };
            var evidence = new Receipts.EffectEvidence { Acquisition = observed.Clone() };
            if (observed.OutputComplete && observed.LaborFinished && observed.ProducedUnits > 0 && observed.OutputObserved)
                result.Completed = new Receipts.CompletedEffect { Evidence = evidence };
            else if (observed.OutputComplete && observed.LaborFinished && observed.ProducedUnits == 0)
                result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.OutcomeNotAchieved,
                    Evidence = evidence, Detail = "Ordinary harvest finished without observed output." };
            else if (!observed.LaborFinished && observed.Designated)
                result.Pending = new Receipts.PendingEffect { Evidence = evidence };
            else { result.CompleteInspection = false; result.Unknown = new Receipts.UnknownEffect { Reason = "Missing source or output does not prove acquisition." }; }
            return result;
        }
        // A plant record whose designation is gone before any harvest is
        // terminal (the excavation rule for a removed mine designation): the
        // player, the game or CancelAcquisition (#291) withdrew it, and the
        // acquisition certainly did not happen through it. A source that
        // vanished uncollected is the same outcome.
        internal Receipts.Progress ObservePlant(Common.AttemptKey attempt, Common.ObservationContext context)
        {
            var observed = Evidence();
            if (!observed.LaborFinished && !observed.Designated)
                return new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = true,
                    Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.OutcomeNotAchieved,
                        Evidence = new Receipts.EffectEvidence { Acquisition = observed },
                        Detail = Source.Destroyed || !Source.Spawned ? "Source is gone without an observed harvest." : "Harvest designation is gone and the plant remains." } };
            return Progress(attempt, context, observed);
        }
    }

    // These hooks observe ordinary pawn harvest and placement; they never create
    // resources or perform pawn work. The placement callback accounts for merges.
    internal static class NativeAcquisitionTracking
    {
        private const string Owner = "rimgovernor.native.acquisition";
        private sealed class State
        {
            internal readonly Dictionary<Plant, NativeAcquisitionRecord> Sources = new Dictionary<Plant, NativeAcquisitionRecord>();
            internal readonly Dictionary<Thing, NativeAcquisitionRecord> Products = new Dictionary<Thing, NativeAcquisitionRecord>();
        }
        private static readonly ConditionalWeakTable<Game, State> States = new ConditionalWeakTable<Game, State>();
        private static readonly List<MethodBase> Targets = new List<MethodBase>();
        private static bool installed;
        internal static void Install()
        {
            if (installed) return;
            installed = true;
            try
            {
                var harmony = new Harmony(Owner);
                Patch(harmony, AccessTools.Method(typeof(QuestManager), "Notify_PlantHarvested", new[] { typeof(Pawn), typeof(Thing) }), nameof(Produced), null);
                Patch(harmony, AccessTools.Method(typeof(GenPlace), "TryPlaceThing", new[] { typeof(Thing), typeof(IntVec3), typeof(Map), typeof(ThingPlaceMode), typeof(Thing).MakeByRefType(), typeof(Action<Thing, int>), typeof(Predicate<IntVec3>), typeof(Rot4?), typeof(int) }), nameof(BeforePlace), null);
                Patch(harmony, AccessTools.Method(typeof(Plant), "PlantCollected", new[] { typeof(Pawn), typeof(PlantDestructionMode) }), null, nameof(Collected));
            }
            catch (Exception error) { Log.Error("[RimGovernor] Acquisition tracking unavailable: " + error); }
        }
        private static void Patch(Harmony harmony, MethodBase? target, string? prefix, string? postfix)
        {
            if (target == null) throw new MissingMethodException("Native acquisition hook target missing");
            harmony.Patch(target, prefix == null ? null : new HarmonyMethod(typeof(NativeAcquisitionTracking), prefix),
                postfix == null ? null : new HarmonyMethod(typeof(NativeAcquisitionTracking), postfix));
            Targets.Add(target);
        }
        internal static bool Ready => Targets.Count == 3 && Targets.All(t => {
            var info = Harmony.GetPatchInfo(t);
            return info != null && info.Prefixes.Concat(info.Postfixes).Any(p => p.owner == Owner);
        });
        // Tracked is the live (unfinished) record for a plant this controller
        // designated, if any; CancelAcquisition withdraws through it.
        internal static NativeAcquisitionRecord? Tracked(Plant source)
        {
            if (Current.Game == null || !States.TryGetValue(Current.Game, out var state) || !state.Sources.TryGetValue(source, out var record) || record.Finished) return null;
            return record;
        }
        internal static bool Track(NativeAcquisitionRecord record)
        {
            if (!Ready || Current.Game == null) return false;
            var state = States.GetOrCreateValue(Current.Game);
            if (state.Sources.TryGetValue(record.Source, out var prior) && !prior.Finished) return false;
            if (!state.Sources.ContainsKey(record.Source) && state.Sources.Count >= 4096) return false;
            state.Sources[record.Source] = record; return true;
        }
        private static void Produced(Pawn __0, Thing __1)
        {
            NativeAcquisitionRecord? observed = null;
            try
            {
                if (Current.Game == null || !States.TryGetValue(Current.Game, out var state) || !(__0.CurJob?.targetA.Thing is Plant source)
                    || !state.Sources.TryGetValue(source, out var record) || record.Finished || __1.def.defName != record.Resource) return;
                observed = record;
                if (state.Products.Count >= 4096) { record.Unreadable = true; return; }
                state.Products[__1] = record;
            }
            catch (Exception) { if (observed != null) observed.Unreadable = true; }
        }
        private static void BeforePlace(Thing __0, ref Action<Thing, int>? __5)
        {
            if (Current.Game == null || !States.TryGetValue(Current.Game, out var state) || !state.Products.TryGetValue(__0, out var record)) return;
            state.Products.Remove(__0);
            var original = __5;
            __5 = (thing, count) => {
                try { original?.Invoke(thing, count); }
                catch { record.Unreadable = true; throw; }
                if (count <= 0 || record.Outputs.Count >= 256 && !record.Outputs.ContainsKey(thing) || thing.def.defName != record.Resource) { record.Unreadable = true; return; }
                record.Outputs.TryGetValue(thing, out var old);
                try { record.Outputs[thing] = checked(old + count); }
                catch (OverflowException) { record.Unreadable = true; }
            };
        }
        private static void Collected(Plant __instance)
        {
            if (Current.Game != null && States.TryGetValue(Current.Game, out var state) && state.Sources.TryGetValue(__instance, out var record)) record.Finished = true;
        }
    }
}
