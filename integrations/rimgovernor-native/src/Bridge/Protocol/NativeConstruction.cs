#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
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
        internal readonly BuildableDef Definition;
        // Terrain is set when the plan lays a floor (a TerrainDef): the frame
        // completes into terrain at the cell, never into a successor Thing.
        internal readonly TerrainDef? Terrain;
        internal readonly ThingDef? Stuff; // Null unless the definition is made from stuff.
        internal readonly IntVec3 Cell;
        internal readonly Rot4 Rotation;
        internal readonly Faction Player;
        internal readonly bool Instant;
        internal NativeConstructionPlan(Map map, BuildableDef definition, ThingDef? stuff, IntVec3 cell, Rot4 rotation, Faction player)
        {
            Map = map; Definition = definition; Terrain = definition as TerrainDef; Stuff = stuff; Cell = cell; Rotation = rotation; Player = player;
            Instant = definition.GetStatValueAbstract(StatDefOf.WorkToBuild, stuff) == 0f;
        }

        internal static bool Prepare(Map map, Placement.PlacementCandidate? candidate, Common.ObservationContext context,
            [NotNullWhen(true)] out NativeConstructionPlan? plan, [NotNullWhen(true)] out Placement.PlacementEvaluated? preview, [NotNullWhen(false)] out Common.Failure? failure)
        {
            plan = null; preview = null;
            var validation = new Placement.PlacementRequest { Identity = context.Identity };
            if (candidate != null) validation.Placements.Add(candidate);
            if (!PlacementProtocol.Validate(validation, out failure)) return false;
            if (candidate == null) { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Construction requires one placement candidate."); return false; }
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
            if (!PlacementPreviewOperation.TryResolveBuildable(candidate.DefName, out var definition, out _) || definition == null)
            {
                failure = ProtoBoundary.Fail(Common.FailureCode.Unsupported, "This construction adapter requires a ThingDef building or a TerrainDef floor.");
                return false;
            }
            if (definition is TerrainDef && definition.GetStatValueAbstract(StatDefOf.WorkToBuild) == 0f)
            {
                failure = ProtoBoundary.Fail(Common.FailureCode.Unsupported, "This construction adapter lays floors through an ordinary blueprint and frame only.");
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
            var building = ThingMaker.MakeThing((ThingDef)Definition, Stuff);
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
        internal string? Uncertain;
        internal bool Cancelled;
        // Laid: the tracked frame completed into the plan's terrain at its
        // cell. Current then stays the destroyed frame, the last native object
        // of the lineage, and the floor itself is re-read on every inspection.
        internal bool Laid;
        internal NativeConstructionRecord(Game game, NativeConstructionPlan plan, Thing thing, Receipts.ConstructionEffect effect)
        {
            Game = game; Map = plan.Map; Plan = plan; Current = thing; Effect = effect.Clone();
            Effect.OriginThingId = thing.GetUniqueLoadID();
            Update(thing);
        }
        internal bool Matches(Thing? thing) => thing != null && thing.Spawned && ReferenceEquals(thing.Map, Map)
            && thing.Position == Plan.Cell && thing.Rotation == Plan.Rotation && thing.Faction == Plan.Player
            && (thing is Blueprint || thing is Frame ? thing.def.entityDefToBuild : thing.def) == Plan.Definition
            && (thing is Blueprint_Build blueprint ? blueprint.EntityToBuildStuff() : thing.Stuff) == Plan.Stuff;
        internal void Lay()
        {
            Laid = true;
            Effect.Stage = Receipts.ConstructionStage.Building;
            Effect.Present = true; Effect.Started = true; Effect.Failed = false;
        }
        internal bool FloorPresent() => Plan.Terrain != null && Plan.Cell.InBounds(Map) && Map.terrainGrid.TerrainAt(Plan.Cell) == Plan.Terrain;
        internal void Update(Thing thing)
        {
            Current = thing;
            Effect.CurrentThingId = thing.GetUniqueLoadID();
            Effect.Stage = thing is Frame ? Receipts.ConstructionStage.Frame : thing is Blueprint
                ? Receipts.ConstructionStage.Blueprint : Receipts.ConstructionStage.Building;
            Effect.Present = Matches(thing); Effect.Started = true; Effect.Failed = false;
            if (Effect.Present && Effect.Stage == Receipts.ConstructionStage.Building) NativeDrillOwnership.Register(thing);
        }
        internal Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context)
        {
            var progress = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = false };
            if (!ReferenceEquals(Game, Verse.Current.Game) || !ReferenceEquals(Map, ProtoBoundary.ResolveMap(context)) || Uncertain != null)
            {
                progress.Unknown = new Receipts.UnknownEffect { Reason = Uncertain ?? "Construction context changed." };
                return progress;
            }
            if (Laid)
            {
                if (FloorPresent())
                {
                    progress.CompleteInspection = true;
                    progress.Completed = new Receipts.CompletedEffect { Evidence = new Receipts.EffectEvidence { Construction = Effect.Clone() } };
                }
                else progress.Unknown = new Receipts.UnknownEffect { Reason = "Laid floor is no longer the admitted terrain at its cell." };
                return progress;
            }
            if (Cancelled && Current.Destroyed)
            {
                var cancelled = Effect.Clone();
                cancelled.Stage = Receipts.ConstructionStage.Cancelled;
                cancelled.Present = false; cancelled.Failed = true;
                progress.CompleteInspection = true;
                progress.Unsuccessful = new Receipts.UnsuccessfulEffect
                {
                    Reason = Receipts.UnsuccessfulReason.Cancelled,
                    Evidence = new Receipts.EffectEvidence { Construction = cancelled },
                    Detail = "The exact tracked construction was cancelled by native Destroy(Cancel)."
                };
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
                progress.Unknown = new Receipts.UnknownEffect
                    { Reason = "Tracked object was destroyed; absence of all successor orders is not established." };
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
        private static readonly NativeConstructionHookSet Hooks = new NativeConstructionHookSet(PatchOwner);
        private static bool installationComplete;
        internal static bool Ready => installationComplete && Hooks.Ready(9);
        internal static void Install()
        {
            if (installed) return;
            installed = true;
            try
            {
                var patcher = new Harmony(PatchOwner);
                var blueprint = AccessTools.Method(typeof(Blueprint), nameof(Blueprint.TryReplaceWithSolidThing));
                var make = AccessTools.Method(typeof(ThingMaker), nameof(ThingMaker.MakeThing), new[] { typeof(ThingDef), typeof(ThingDef) });
                var finish = AccessTools.Method(typeof(Frame), nameof(Frame.CompleteConstruction));
                var fail = AccessTools.Method(typeof(Frame), nameof(Frame.FailConstruction));
                var spawn = AccessTools.Method(typeof(GenSpawn), nameof(GenSpawn.Spawn), new[] {
                    typeof(Thing), typeof(IntVec3), typeof(Map), typeof(Rot4), typeof(WipeMode), typeof(bool), typeof(bool) });
                var destroy = new[] { typeof(Thing), typeof(ThingWithComps), typeof(Building), typeof(Frame) }
                    .Select(type => AccessTools.DeclaredMethod(type, nameof(Thing.Destroy), new[] { typeof(DestroyMode) })).ToArray();
                if (destroy.Any(method => method == null)) return;
                if (blueprint == null || finish == null || fail == null || spawn == null || make == null) return;
                patcher.Patch(blueprint, finalizer: new HarmonyMethod(typeof(NativeConstructionTracking), nameof(Transition)));
                foreach (var method in new[] { finish, fail }) patcher.Patch(method,
                    prefix: new HarmonyMethod(typeof(NativeConstructionTracking), nameof(Begin)),
                    finalizer: new HarmonyMethod(typeof(NativeConstructionTracking), nameof(End)));
                patcher.Patch(make, postfix: new HarmonyMethod(typeof(NativeConstructionTracking), nameof(Created)));
                patcher.Patch(spawn, postfix: new HarmonyMethod(typeof(NativeConstructionTracking), nameof(Spawned)));
                foreach (var method in destroy) patcher.Patch(method,
                    finalizer: new HarmonyMethod(typeof(NativeConstructionTracking), nameof(Cancelled)));
                Hooks.Add(blueprint, finalizer: AccessTools.Method(typeof(NativeConstructionTracking), nameof(Transition)));
                foreach (var method in new[] { finish, fail }) Hooks.Add(method,
                    prefix: AccessTools.Method(typeof(NativeConstructionTracking), nameof(Begin)),
                    finalizer: AccessTools.Method(typeof(NativeConstructionTracking), nameof(End)));
                Hooks.Add(make, postfix: AccessTools.Method(typeof(NativeConstructionTracking), nameof(Created)));
                Hooks.Add(spawn, postfix: AccessTools.Method(typeof(NativeConstructionTracking), nameof(Spawned)));
                foreach (var method in destroy) Hooks.Add(method,
                    finalizer: AccessTools.Method(typeof(NativeConstructionTracking), nameof(Cancelled)));
                installationComplete = true;
            }
            catch { installationComplete = false; }
        }
        internal static NativeConstructionRecord Register(NativeConstructionPlan plan, Thing thing, Receipts.ConstructionEffect effect)
        {
            var record = new NativeConstructionRecord(Current.Game, plan, thing, effect);
            Tracked.Add(thing, record);
            return record;
        }
        // MakeSolidThing returns an unspawned, unfactioned frame. Its enclosing
        // method supplies placement/faction and exposes the exact created object.
        private static void Transition(Blueprint __instance, bool __result, Thing? createdThing, Exception? __exception)
        {
            if (!Tracked.TryGetValue(__instance, out var record)) return;
            if (__exception == null && !__result && createdThing == null && record.Matches(__instance)) return;
            if (__exception != null || !__result || !__instance.Destroyed || !record.Matches(createdThing)
                || !(createdThing is Frame))
            { record.Uncertain = "Blueprint transition did not produce one matching native object."; return; }
            Tracked.Remove(__instance); Tracked.Add(createdThing, record); record.Update(createdThing);
        }
        private static void Cancelled(Thing __instance, DestroyMode mode,
            System.Reflection.MethodBase __originalMethod, Exception? __exception)
        {
            if (mode != DestroyMode.Cancel || __exception != null || !__instance.Destroyed) return;
            // Base Destroy may succeed before a derived override or component
            // throws. Only the outermost concrete virtual implementation confirms.
            if (!NativeConstructionHookSet.SameMethod(
                AccessTools.Method(__instance.GetType(), nameof(Thing.Destroy), new[] { typeof(DestroyMode) }), __originalMethod)) return;
            if (Tracked.TryGetValue(__instance, out var record) && ReferenceEquals(record.Current, __instance))
                record.Cancelled = true;
        }
        private sealed class Completion
        {
            internal readonly NativeConstructionRecord? Record; // Null for an untracked nested frame call.
            internal readonly Frame Frame;
            internal readonly ThingDef? ExpectedDefinition;
            internal readonly bool Failing;
            internal Completion(NativeConstructionRecord? record, Frame frame, ThingDef? expectedDefinition, bool failing)
            { Record = record; Frame = frame; ExpectedDefinition = expectedDefinition; Failing = failing; }
            internal readonly NativeConstructionCausality Causality = new NativeConstructionCausality();
        }
        private static void Begin(Frame __instance, System.Reflection.MethodBase __originalMethod, out Completion __state)
        {
            Tracked.TryGetValue(__instance, out var record);
            // Even an untracked nested frame call hides its effects from a parent.
            var failing = __originalMethod.Name == nameof(Frame.FailConstruction);
            __state = new Completion(record, __instance, failing
                ? __instance.def.entityDefToBuild?.blueprintDef : __instance.def.entityDefToBuild as ThingDef, failing);
            Completions.Add(__state);
        }
        private static void Created(Thing? __result)
        {
            if (Completions.Count == 0 || __result == null) return;
            var completion = Completions[Completions.Count - 1];
            if (completion.Record != null && __result.def == completion.ExpectedDefinition)
                completion.Causality.Created(__result);
        }
        private static void Spawned(Thing __0, Thing? __result)
        {
            if (Completions.Count == 0) return;
            var completion = Completions[Completions.Count - 1];
            if (completion.Record != null) completion.Causality.Spawned(__0, __result);
        }
        private static void End(Completion? __state, Exception? __exception)
        {
            if (__state == null) return;
            Completions.Remove(__state);
            if (__state.Record == null) return;
            if (__state.Record.Plan.Terrain != null && !__state.Failing)
            {
                // A floor frame completes by setting the terrain grid and
                // vanishing; there is no successor object to follow.
                if (__exception != null || !__state.Frame.Destroyed || !ReferenceEquals(__state.Record.Current, __state.Frame) || !__state.Record.FloorPresent())
                { __state.Record.Uncertain = "Floor completion did not lay the admitted terrain."; return; }
                __state.Record.Lay();
                return;
            }
            if (!__state.Causality.TryComplete(__state.Frame.Destroyed, __exception, out var successor)
                || !(successor is Thing thing) || !__state.Record.Matches(thing)
                || !(thing is Building || thing is Blueprint_Build))
            { __state.Record.Uncertain = "Construction transition identity could not be established."; return; }
            Tracked.Remove(__state.Frame); Tracked.Add(thing, __state.Record); __state.Record.Update(thing);
        }
    }
}
