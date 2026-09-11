using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Placement = RimGovernor.Protocol.Placement;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    internal sealed class NativeConstructionPlan
    {
        internal readonly Map Map;
        internal readonly ThingDef Definition;
        internal readonly ThingDef Stuff;
        internal readonly IntVec3 Cell;
        internal readonly Rot4 Rotation;
        internal readonly Faction Player;
        internal readonly bool Instant;
        internal NativeConstructionPlan(Map map, ThingDef definition, ThingDef stuff, IntVec3 cell, Rot4 rotation, Faction player)
        {
            Map = map; Definition = definition; Stuff = stuff; Cell = cell; Rotation = rotation; Player = player;
            Instant = definition.GetStatValueAbstract(StatDefOf.WorkToBuild, stuff) == 0f;
        }

        internal static bool Prepare(Map map, Placement.PlacementCandidate candidate, Common.ObservationContext context,
            out NativeConstructionPlan plan, out Placement.PlacementEvaluated preview, out Common.Failure failure)
        {
            plan = null; preview = null;
            var validation = new Placement.PlacementRequest { Identity = context.Identity };
            if (candidate != null) validation.Placements.Add(candidate);
            if (!PlacementProtocol.Validate(validation, out failure)) return false;
            var query = new PlacementQuery(candidate.DefName, candidate.X, candidate.Z,
                PlacementProtocol.RotationName(candidate.Rotation), candidate.HasStuff ? candidate.Stuff : null);
            var result = PlacementProtocol.Map(PlacementPreviewOperation.Evaluate(map, query), context);
            if (result.Failure != null) { failure = result.Failure; return false; }
            preview = result.Evaluated;
            if (candidate.Rotation == Placement.Rotation.All)
            {
                failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Construction requires one cardinal rotation.");
                return false;
            }
            var definition = PlacementPreviewOperation.ResolveThingDef(candidate.DefName);
            if (definition == null)
            {
                failure = ProtoBoundary.Fail(Common.FailureCode.Unsupported, "This construction adapter requires a ThingDef building.");
                return false;
            }
            if (!preview.CanPlace)
            {
                failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest,
                    PlacementPreviewOperation.Diagnostic(preview.Rotations[0].Reason));
                return false;
            }
            var player = Faction.OfPlayerSilentFail;
            if (player == null)
            {
                failure = ProtoBoundary.Fail(Common.FailureCode.Unavailable, "No player faction is available.");
                return false;
            }
            var stuff = preview.MadeFromStuff ? (candidate.Stuff.Length == 0 ? GenStuff.DefaultStuffFor(definition)
                : PlacementPreviewOperation.ResolveThingDef(candidate.Stuff)) : null;
            plan = new NativeConstructionPlan(map, definition, stuff, new IntVec3(candidate.X, 0, candidate.Z),
                new Rot4((int)candidate.Rotation - 1), player);
            failure = null;
            return true;
        }

        // Call inside the admitted native authority scope. Every mutation uses ordinary architect behavior.
        internal Thing Place(Receipts.ConstructionEffect observed)
        {
            var blueprint = Definition.blueprintDef;
            foreach (var frame in Map.thingGrid.ThingsListAt(Cell).OfType<Frame>().ToArray())
            {
                if (frame.Destroyed || blueprint.replaceTags == null || frame.def.replaceTags == null
                    || !blueprint.replaceTags.Intersect(frame.def.replaceTags).Any()) continue;
                var id = frame.GetUniqueLoadID();
                frame.Destroy(DestroyMode.Cancel);
                if (frame.Destroyed) observed.CancelledFrameIds.Add(id);
            }
            if (!Instant)
            {
                var wiped = GenAdj.CellsOccupiedBy(Cell, Rotation, blueprint.Size).Where(cell => cell.InBounds(Map))
                    .SelectMany(cell => Map.thingGrid.ThingsListAt(cell)).Distinct()
                    .Where(thing => !thing.Destroyed && GenSpawn.SpawningWipes(blueprint, thing.def)).ToArray();
                var ids = wiped.Select(thing => thing.GetUniqueLoadID()).ToArray();
                GenSpawn.WipeExistingThings(Cell, Rotation, blueprint, Map, DestroyMode.Deconstruct);
                for (var index = 0; index < wiped.Length; index++)
                    if (wiped[index].Destroyed) observed.WipedThingIds.Add(ids[index]);
            }
            if (!Instant) return GenConstruct.PlaceBlueprintForBuild(Definition, Cell, Map, Rotation, Player, Stuff);
            var building = ThingMaker.MakeThing(Definition, Stuff);
            building.SetFactionDirect(Player);
            return GenSpawn.Spawn(building, Cell, Map, Rotation);
        }

        internal Receipts.ConstructionEffect Proposed() => new Receipts.ConstructionEffect
        {
            DefName = Definition.defName, Stuff = Stuff?.defName ?? "", Cell = new Common.Cell { X = Cell.x, Z = Cell.z },
            Rotation = (Placement.Rotation)(Rotation.AsInt + 1)
        };
    }

    // Unsaved object identity follows the actual blueprint/frame/building calls, never a matching-coordinate guess.
    internal sealed class NativeConstructionRecord
    {
        internal readonly Game Game;
        internal readonly Map Map;
        internal readonly NativeConstructionPlan Plan;
        internal readonly Receipts.ConstructionEffect Effect;
        internal Thing Current;
        internal string Uncertain;
        internal NativeConstructionRecord(Game game, NativeConstructionPlan plan, Thing thing, Receipts.ConstructionEffect effect)
        {
            Game = game; Map = plan.Map; Plan = plan; Current = thing; Effect = effect.Clone();
            Effect.OriginThingId = thing.GetUniqueLoadID();
            Update(thing);
        }
        internal bool Matches(Thing thing) => thing != null && thing.Spawned && ReferenceEquals(thing.Map, Map)
            && thing.Position == Plan.Cell && thing.Rotation == Plan.Rotation && thing.Faction == Plan.Player
            && (thing is Building ? thing.def : thing.def.entityDefToBuild) == Plan.Definition
            && (thing is Blueprint_Build blueprint ? blueprint.EntityToBuildStuff() : thing.Stuff) == Plan.Stuff;
        internal void Update(Thing thing)
        {
            Current = thing;
            Effect.CurrentThingId = thing.GetUniqueLoadID();
            Effect.Stage = thing is Frame ? Receipts.ConstructionStage.Frame : thing is Blueprint
                ? Receipts.ConstructionStage.Blueprint : Receipts.ConstructionStage.Building;
            Effect.Present = Matches(thing); Effect.Started = true; Effect.Failed = false;
        }
        internal Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context)
        {
            var progress = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = false };
            if (!ReferenceEquals(Game, Verse.Current.Game) || !ReferenceEquals(Map, Find.CurrentMap) || Uncertain != null)
            {
                progress.Unknown = new Receipts.UnknownEffect { Reason = Uncertain ?? "Construction context changed." };
                return progress;
            }
            if (Matches(Current))
            {
                Effect.Present = true;
                progress.CompleteInspection = true;
                var evidence = new Receipts.EffectEvidence { Construction = Effect.Clone() };
                if (Effect.Stage == Receipts.ConstructionStage.Building)
                    progress.Completed = new Receipts.CompletedEffect { Evidence = evidence };
                else progress.Pending = new Receipts.PendingEffect { Evidence = evidence };
            }
            else if (Current.Destroyed)
            {
                progress.CompleteInspection = true;
                progress.Absent = new Receipts.AbsentEffect { InspectionToken = Guid.NewGuid().ToString("N") };
            }
            else progress.Unknown = new Receipts.UnknownEffect { Reason = "Tracked construction no longer matches its admitted native identity." };
            return progress;
        }
    }

    internal static class NativeConstructionTracking
    {
        private const string PatchOwner = "rimgovernor.typed-construction";
        private static readonly ConditionalWeakTable<Thing, NativeConstructionRecord> Tracked = new ConditionalWeakTable<Thing, NativeConstructionRecord>();
        private static readonly List<Completion> Completions = new List<Completion>();
        private static bool installed;
        internal static bool Ready { get; private set; }
        internal static void Install()
        {
            if (installed) return;
            installed = true;
            try
            {
                var patcher = new Harmony(PatchOwner);
                var blueprint = AccessTools.Method(typeof(Blueprint_Build), "MakeSolidThing");
                var finish = AccessTools.Method(typeof(Frame), nameof(Frame.CompleteConstruction));
                var fail = AccessTools.Method(typeof(Frame), nameof(Frame.FailConstruction));
                var spawn = AccessTools.Method(typeof(GenSpawn), nameof(GenSpawn.Spawn), new[] {
                    typeof(Thing), typeof(IntVec3), typeof(Map), typeof(Rot4), typeof(WipeMode), typeof(bool), typeof(bool) });
                if (blueprint == null || finish == null || fail == null || spawn == null) return;
                patcher.Patch(blueprint, finalizer: new HarmonyMethod(typeof(NativeConstructionTracking), nameof(Transition)));
                foreach (var method in new[] { finish, fail }) patcher.Patch(method,
                    prefix: new HarmonyMethod(typeof(NativeConstructionTracking), nameof(Begin)),
                    finalizer: new HarmonyMethod(typeof(NativeConstructionTracking), nameof(End)));
                patcher.Patch(spawn, postfix: new HarmonyMethod(typeof(NativeConstructionTracking), nameof(Spawned)));
                Ready = new[] { blueprint, finish, fail, spawn }.All(method => Harmony.GetPatchInfo(method)?.Owners.Contains(PatchOwner) == true);
            }
            catch { Ready = false; }
        }
        internal static NativeConstructionRecord Register(NativeConstructionPlan plan, Thing thing, Receipts.ConstructionEffect effect)
        {
            var record = new NativeConstructionRecord(Current.Game, plan, thing, effect);
            Tracked.Add(thing, record);
            return record;
        }
        private static void Transition(Blueprint_Build __instance, Thing __result, Exception __exception)
        {
            NativeConstructionRecord record;
            if (!Tracked.TryGetValue(__instance, out record)) return;
            if (__exception != null || !record.Matches(__result) || !(__result is Frame || __result is Building))
            { record.Uncertain = "Blueprint transition did not produce one matching native object."; return; }
            Tracked.Remove(__instance); Tracked.Add(__result, record); record.Update(__result);
        }
        private sealed class Completion
        {
            internal NativeConstructionRecord Record;
            internal Frame Frame;
            internal readonly List<Thing> Spawned = new List<Thing>();
        }
        private static void Begin(Frame __instance, out Completion __state)
        {
            __state = null;
            NativeConstructionRecord record;
            if (!Tracked.TryGetValue(__instance, out record)) return;
            __state = new Completion { Record = record, Frame = __instance };
            Completions.Add(__state);
        }
        private static void Spawned(Thing __result)
        {
            foreach (var completion in Completions)
                if (completion.Record.Matches(__result)) completion.Spawned.Add(__result);
        }
        private static void End(Completion __state, Exception __exception)
        {
            if (__state == null) return;
            Completions.Remove(__state);
            var matches = __state.Spawned.Distinct().ToArray();
            if (__exception != null || !__state.Frame.Destroyed || matches.Length != 1
                || !(matches[0] is Building || matches[0] is Blueprint_Build))
            { __state.Record.Uncertain = "Construction transition identity could not be established."; return; }
            Tracked.Remove(__state.Frame); Tracked.Add(matches[0], __state.Record); __state.Record.Update(matches[0]);
        }
    }
}
