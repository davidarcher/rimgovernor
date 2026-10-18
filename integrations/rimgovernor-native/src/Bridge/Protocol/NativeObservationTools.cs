#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RimWorld;
using Verse;
using Verse.AI.Group;
using Common = RimGovernor.Contracts.Wire.Common;
using Defs = RimGovernor.Contracts.Wire.Defs;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    internal static class NativeObservationTools
    {
        // On the game thread: the typed core of the tool, shared with the in-process facade.
        internal static Obs.StatusReply HandleReadStatus(Obs.StatusRequest parsed)
        {
            if (!ValidateStatus(parsed, out var invalidRequest)) return new Obs.StatusReply { Outcome = invalidRequest };
            if (!ProtoBoundary.ValidateIdentity(parsed.Scope?.ExpectedIdentity, out var map, out var context, out var failure))
                return new Obs.StatusReply { Outcome = failure };
            try { return new Obs.StatusReply { Outcome = Status(map, parsed, context) }; }
            catch (Exception) { return new Obs.StatusReply { Outcome = Unavailable(Common.UnavailableReason.ReadFailed, "Native status facts could not be read completely.") }; }
        }

        // On the game thread: the typed core of the tool, shared with the in-process facade.
        internal static Obs.GetCellsReply HandleGetCells(Obs.GetCellsRequest parsed)
        {
            if (!ValidateCells(parsed, out var invalidRequest)) return new Obs.GetCellsReply { Outcome = invalidRequest };
            if (!ProtoBoundary.ValidateIdentity(parsed.Scope?.ExpectedIdentity, out var map, out var context, out var failure))
                return new Obs.GetCellsReply { Outcome = failure };
            return ReadCells(map, parsed, context);
        }

        // ReadCells is the read on the main thread under a validated
        // identity: the rectangle as the frame grid reads it.
        internal static Obs.GetCellsReply ReadCells(Map map, Obs.GetCellsRequest parsed, Common.ObservationContext context)
        {
            try {
                var min = parsed.Rectangle?.Minimum ?? new Common.Cell(); var max = parsed.Rectangle?.Maximum ?? new Common.Cell();
                if (!new IntVec3(min.X, 0, min.Z).InBounds(map) || !new IntVec3(max.X, 0, max.Z).InBounds(map)) return new Obs.GetCellsReply {
                    Outcome = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Selected cell is outside the current map.") };
                var grid = CellGridReader.Read(map, min.X, min.Z, max.X - min.X + 1, max.Z - min.Z + 1);
                var snapshot = new Obs.CellsSnapshot { Context = context,
                    MapSize = new Obs.MapSize { Width = checked((uint)map.Size.x), Height = checked((uint)map.Size.z) },
                    Grid = grid };
                return new Obs.GetCellsReply { Outcome = snapshot };
            }
            catch (Exception) { return new Obs.GetCellsReply { Outcome = Unavailable(Common.UnavailableReason.ReadFailed, "Native cell facts could not be read completely.") }; }
        }

        // On the game thread: the typed core of the tool.
        internal static Obs.ExcavationSiteReply HandleReadExcavationSite(Obs.ExcavationSiteRequest parsed)
        {
            if (!NativeExcavationSite.Validate(parsed, out var failure0)) return new Obs.ExcavationSiteReply { Outcome = failure0 };
            if (!ProtoBoundary.ValidateIdentity(parsed.Scope?.ExpectedIdentity, out var map, out var context, out var failure))
                return new Obs.ExcavationSiteReply { Outcome = failure };
            try { return new Obs.ExcavationSiteReply { Outcome = NativeExcavationSite.Read(map, parsed, context) }; }
            catch (Exception) { return new Obs.ExcavationSiteReply { Outcome = Unavailable(Common.UnavailableReason.ReadFailed, "Native excavation site facts could not be read completely.") }; }
        }


        // Traversal's doorway fact, shared with the planning
        // window view's capture.
        internal static bool CellDoorway(Map map, IntVec3 cell) => cell.GetDoor(map) != null || cell.GetThingList(map).Any(t => (t is Blueprint || t is Frame)
            && t.def.entityDefToBuild is ThingDef built && typeof(Building_Door).IsAssignableFrom(built.thingClass));
        internal static bool ValidateStatus(Obs.StatusRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Valid identity scope and finite nonnegative predator radius required.");
            return request != null && request.Scope?.ExpectedIdentity != null
                && (request.PredatorRadius is null || !double.IsNaN(request.PredatorRadius.GetValueOrDefault()) && !double.IsInfinity(request.PredatorRadius.GetValueOrDefault()) && request.PredatorRadius.GetValueOrDefault() >= 0);
        }
        internal static bool ValidateCells(Obs.GetCellsRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Valid identity and an inclusive rectangle of at most 1048576 cells required.");
            if (request == null || request.Scope?.ExpectedIdentity == null) return false;
            var min = request.Rectangle?.Minimum; var max = request.Rectangle?.Maximum;
            if (min == null || max == null || min.X < 0 || min.Z < 0 || max.X < min.X || max.Z < min.Z) return false;
            return ((long)max.X - min.X + 1) * ((long)max.Z - min.Z + 1) <= 1 << 20;
        }

        // The status read as a bundle section: the same facts ReadStatus
        // answers, with read failures as unavailable.
        internal static bool TryStatus(Map map, Obs.StatusRequest request, Common.ObservationContext context,
            [NotNullWhen(true)] out Obs.StatusSnapshot? snapshot, [NotNullWhen(false)] out Common.Unavailable? unavailable)
        {
            snapshot = null; unavailable = null;
            try { snapshot = Status(map, request, context); return true; }
            catch (Exception) { unavailable = Unavailable(Common.UnavailableReason.ReadFailed, "Native status facts could not be read completely."); return false; }
        }
        private static Obs.StatusSnapshot Status(Map map, Obs.StatusRequest request, Common.ObservationContext context)
        {
            var result = new Obs.StatusSnapshot { Context = context, DependentPatients = NativeDependentPatients.Observe(map) };
            var wantColonists = request.Colonists is null || request.Colonists.GetValueOrDefault();
            var wantThreats = request.Threats is null || request.Threats.GetValueOrDefault();
            if (!wantColonists && !wantThreats) {
                result = result with { Issues = [.. result.Issues, Issue("colonists", Common.UnavailableReason.NotRequested, "Colonist section not requested.")] };
                result = result with { Issues = [.. result.Issues, Issue("threats", Common.UnavailableReason.NotRequested, "Threat section not requested.")] }; return result;
            }
            var spawned = map.mapPawns.AllPawnsSpawned.ToList();
            var colonists = spawned.Where(p => !p.Dead && p.IsFreeColonist).ToList();
            // Colonists and threats are pawn table references.
            if (wantColonists) foreach (var pawn in colonists) result = result with { Colonists = [.. result.Colonists, NativePawnObservationTools.Ref(pawn)] };
            else result = result with { Issues = [.. result.Issues, Issue("colonists", Common.UnavailableReason.NotRequested, "Colonist section not requested.")] };
            if (!wantThreats) { result = result with { Issues = [.. result.Issues, Issue("threats", Common.UnavailableReason.NotRequested, "Threat section not requested.")] }; return result; }
            var player = Faction.OfPlayerSilentFail ?? throw new InvalidOperationException("Player faction missing.");
            var threats = new Obs.ThreatsSnapshot(); var radius = request.PredatorRadius is not null ? request.PredatorRadius.GetValueOrDefault() : 30;
            var colonistCells = colonists.Select(p => (p.Position.x, p.Position.z)).ToList();
            NativeThreatClassifier.Collect(spawned.Where(p => !p.Dead && !p.IsColonist).ToList(), pawn => ThreatFactsOf(pawn, player),
                colonistCells, radius, NativePawnObservationTools.Ref, pawn => NativeRef.Thing(HuntedPawn(pawn)!), ref threats);
            foreach (var building in HostileBuildings(map, player)) {
                var row = new Obs.ThreatBuilding { Building = Entity(building) };
                row = row with { BuildingSnapshot = new Obs.SnapshotRef { Context = context, EntityId = row.Building.Id, Token = Token(context.Identity, building) } };
                if (colonists.Count > 0) row = row with { NearestColonistDistance = colonists.Min(p => Math.Max(Math.Abs(p.Position.x-building.Position.x),Math.Abs(p.Position.z-building.Position.z))) };
                var rect = building.OccupiedRect();
                row = row with { Occupied = new Obs.Rectangle { Minimum = Cell(rect.minX, rect.minZ), Maximum = Cell(rect.maxX, rect.maxZ) } };
                if (building is Hive) row = row with { Passive = !HiveEngaging(building, colonists, spawned, player) };
                else row = row with { Passive = Dormant(building) ?? false };
                threats = threats with { HostileBuildings = [.. threats.HostileBuildings, row] };
            }
            result = result with { Threats = threats }; return result;
        }

        // The cheap facts the threat filter branches on and its rows carry; the brain classifies them.
        private static ThreatFacts ThreatFactsOf(Pawn pawn, Faction player)
        {
            var ours = pawn.Faction == player; var hunt = pawn.CurJobDef?.defName == "PredatorHunt"; var held = pawn.HostFaction == player;
            var facts = new ThreatFacts { Ours = ours, Mental = pawn.MentalStateDef?.defName,
                FactionHostile = pawn.Faction != null && !ours && !held && pawn.Faction.HostileTo(player), PredatorHunt = hunt,
                PrisonBreak = held && PrisonBreakUtility.IsPrisonBreaking(pawn),
                Downed = pawn.Downed, Predator = pawn.RaceProps.predator, X = pawn.Position.x, Z = pawn.Position.z };
            if (facts.FactionHostile && pawn.Faction == Faction.OfInsects) facts.Passive = !InsectEngaging(pawn, player);
            else if (facts.FactionHostile) facts.Passive = Dormant(pawn);
            if (hunt) { var prey = HuntedPawn(pawn); if (prey != null) { facts.HasPrey = true; facts.PreyOurs = prey.Faction == player || prey.HostFaction == player; } }
            return facts;
        }
        // Insects and hives: a dormant hive makes jelly but neither
        // spreads nor spawns, and its insects attack only what trespasses the
        // hive's boundary; leftover insects attack only a colonist that comes
        // very close. So an insect is engaging only while awake and targeting
        // something of the player's, and a hive only while awake with a
        // colonist inside its boundary or one of its insects engaging.
        // Tracked in #2652 (docs/developers/contracts/kept-constants.md).
        internal const int HiveBoundaryCells = 10;
        private static bool Awake(Thing thing) => thing.TryGetComp<CompCanBeDormant>()?.Awake ?? true;
        // A dormant mech cluster: its mechs and buildings sleep under
        // CompCanBeDormant until a wake-up comp fires, so each is passive
        // while asleep and engaging once awake. Null without the comp.
        private static bool? Dormant(Thing thing) => thing.TryGetComp<CompCanBeDormant>() is CompCanBeDormant comp ? !comp.Awake : (bool?)null;
        private static bool PlayerThing(Thing? thing, Faction player) => thing != null && (thing.Faction == player || thing is Pawn p && p.HostFaction == player);
        internal static bool InsectEngaging(Pawn pawn, Faction player) => Awake(pawn)
            && (PlayerThing(pawn.mindState?.enemyTarget, player) || PlayerThing(pawn.CurJob?.targetA.Thing, player));
        internal static bool HiveEngaging(Thing hive, IReadOnlyList<Pawn> colonists, IReadOnlyList<Pawn> spawned, Faction player)
        {
            if (!Awake(hive)) return false;
            if (colonists.Any(p => Math.Max(Math.Abs(p.Position.x-hive.Position.x),Math.Abs(p.Position.z-hive.Position.z)) <= HiveBoundaryCells)) return true;
            return spawned.Any(p => !p.Dead && p.Faction == Faction.OfInsects && InsectEngaging(p, player));
        }
        private static Pawn? HuntedPawn(Pawn pawn) { var target = pawn.CurJob?.targetA.Thing; return target as Pawn ?? (target as Corpse)?.InnerPawn; }

        // A hostile building is a combat target in its own right: an insect
        // hive (RimWorld.Hive is a ThingWithComps, so it is read by def, not
        // from the building lister) or any hostile-faction building with hit
        // points and combat power (crashed ship parts, mech-cluster pieces),
        // or a mortar (a siege's). Walls and other inert hostile
        // edifices are not threats.
        internal static bool HostileBuilding(Thing thing, Faction player) => HostileThing(thing, player)
            && (thing is Hive || thing.def.building != null && (thing.def.building.combatPower > 0 || thing.def.building.IsMortar));
        private static bool HostileThing(Thing thing, Faction player) => thing.Spawned && !thing.Destroyed && thing.def.useHitPoints
            && thing.Faction != null && thing.Faction != player && thing.Faction.HostileTo(player);
        // A hostile building thing by load id, what an attack order
        // may target beside a pawn: a census building, or any other spawned
        // hostile-faction building or frame with hit points (a ship part's
        // cluster walls, a siege's sandbag or mortar frame).
        internal static Thing? HostileBuildingThing(Map map, Faction player, string id) =>
            map.listerThings.ThingsOfDef(ThingDefOf.Hive).Concat(map.listerBuildings.allBuildingsNonColonist)
                .ById(id) is Thing t && HostileThing(t, player) ? t : null;
        internal static List<Thing> HostileBuildings(Map map, Faction player)
        {
            var found = new List<Thing>();
            foreach (var hive in map.listerThings.ThingsOfDef(ThingDefOf.Hive)) if (HostileBuilding(hive, player)) found.Add(hive);
            foreach (var building in map.listerBuildings.allBuildingsNonColonist) if (!(building is Hive) && HostileBuilding(building, player)) found.Add(building);
            return found.OrderBy(t => t.thingIDNumber).ToList();
        }

        internal static Obs.JobEvidence JobRow(Verse.AI.Job? job, int queuedJobs)
        {
            if (queuedJobs < 0) throw new InvalidOperationException("Negative native queued job count.");
            var row = new Obs.JobEvidence { PlayerForced = job?.playerForced ?? false, QueuedJobs = (uint)queuedJobs };
            if (job != null) {
                row = row with { DefName = Identifier(job.def.defName) };
                // The work giver's WorkTypeDef names the labor the job spends
                // (a cut designation, a bill, a frame); a job no giver issued
                // (a forced order, rest, a meal, wandering) carries none.
                var workType = job.workGiverDef?.workType?.defName;
                if (workType != null) row = row with { WorkTypeDefName = Identifier(workType) };
                // targetA attributes the job to the thing or cell it works
                //: a haul for one concern is no evidence for another.
                // A thing target is its Ref and cell; an invalid target reads
                // unavailable, so an absent field means an older producer.
                // A delivery (HaulToContainer) carries its material as
                // targetA and works for the frame or bench in targetB.
                var target = job.def == JobDefOf.HaulToContainer && job.targetB.HasThing ? job.targetB : job.targetA;
                if (target.HasThing) {
                    row = row with { TargetA = new Obs.TargetRef { Target = NativeRef.Thing(target.Thing) } };
                    if (target.Thing.Spawned) row = row with { TargetACell = Cell(target.Thing.Position.x, target.Thing.Position.z) };
                } else if (target.IsValid) row = row with { TargetA = new Obs.TargetRef { Target = Cell(target.Cell.x, target.Cell.z) } };
                else row = row with { TargetA = new Obs.TargetRef { Target = new Common.Unavailable { Reason = Common.UnavailableReason.NotApplicable, Detail = "Job has no target." } } };
            }
            return row;
        }

        internal static Obs.PawnState PawnRow(Pawn pawn, Common.ObservationContext context)
        {
            var row = new Obs.PawnState { Pawn=Entity(pawn), KindDefName=Identifier(pawn.kindDef?.defName), Dead=pawn.Dead, Downed=pawn.Downed,
                Drafted=pawn.drafter?.Drafted == true, InBed=RestUtility.InBed(pawn), Colonist=pawn.IsColonist, FreeColonist=pawn.IsFreeColonist,
                Prisoner=pawn.IsPrisoner, Humanlike=pawn.RaceProps.Humanlike, Animal=pawn.RaceProps.Animal, Mechanoid=pawn.RaceProps.IsMechanoid };
            var player=Faction.OfPlayerSilentFail ?? throw new InvalidOperationException("Player faction missing.");
            var manhunter=pawn.MentalStateDef?.defName.IndexOf("Manhunter",StringComparison.OrdinalIgnoreCase)>=0;
            row = row with {
                Hostile = manhunter || pawn.Faction!=null && pawn.Faction!=player && pawn.Faction.HostileTo(player) };
            var constructionCapable = ConstructionSkillGuard.Capability(pawn);
            if (constructionCapable.HasValue) row = row with { ConstructionCapable = constructionCapable.Value };
                        // Fog is the discovery fact, not a guess at reachability: a hostile
            // the colony has never seen is no emergency.
            row = row with { Fogged = pawn.Spawned && pawn.Map != null && pawn.Position.Fogged(pawn.Map) };
            var mental = pawn.MentalState;
            if (mental != null) row = row with { MentalState = new Obs.PawnMentalState { DefName = Identifier(mental.def.defName), IsAggro = mental.def.IsAggro, Ticks = mental.Age } };
            // Inspiration: empty is a known "none"; a pawn without a
            // mind state handler leaves the field absent (unknown).
            if (pawn.mindState?.inspirationHandler != null)
                row = row with { Inspiration = pawn.mindState.inspirationHandler.CurStateDef?.defName ?? "" };
            // Lord evidence is the game's own group-AI class names: a raid's job
            // (assault/siege/stage-then-attack) and its current toil (the sapper
            // and breach toils are distinct classes). No lord means no field.
            var lord = pawn.GetLord();
            if (lord?.LordJob != null) { row = row with { LordJobClass = Identifier(lord.LordJob.GetType().Name) }; if (lord.CurLordToil != null) row = row with { LordToilClass = Identifier(lord.CurLordToil.GetType().Name) }; }
            // A pawn without a job tracker (a corpse's) carries no job block.
            if (pawn.jobs?.jobQueue != null) row = row with { Job = JobRow(pawn.CurJob, pawn.jobs.jobQueue.Count) };
            row = row with { Biotech = NativeBiotechFacts.Pawn(pawn) };
            row = row with { Anomaly = NativeAnomalyFacts.Pawn(pawn) };
            row = NativePawnRoyalty.Apply(pawn, row);
            row = NativePawnControlObservation.Apply(pawn, row, context);
            return row;
        }
        private static Obs.EntityRef Entity(Thing thing)
        {
            var row = new Obs.EntityRef { Id=Identifier(thing.GetUniqueLoadID()), DefName=Identifier(thing.def.defName), Label=Diagnostic(thing.LabelCap) };
            // A held thing (gear, a buried corpse) is at its holder's cell.
            if (thing.MapHeld != null) { row = row with { MapId = thing.MapHeld.uniqueID }; row = row with { Position = Cell(thing.PositionHeld.x,thing.PositionHeld.z) }; }
            return row;
        }
        // Self-checked CAS token for one thing (observation rows and combat
        // orders compare it): its version over def, cell, stack, forbidden,
        // rot stage and a corpse's faction and name.
        internal static string Token(Common.Identity identity, Thing thing)
        {
            var rot = thing.TryGetComp<CompRottable>();
            var corpse = thing as Corpse;
            return NativeSnapshotVersions.Token("thing", identity, thing.GetUniqueLoadID(), writer => {
                writer.Write(thing.def.defName);
                writer.Write(thing.Position.x); writer.Write(thing.Position.z); writer.Write(thing.stackCount);
                writer.Write(thing.IsForbidden(Faction.OfPlayer)); writer.Write(rot?.Stage.ToString() ?? "");
                writer.Write(corpse?.InnerPawn?.Faction?.GetUniqueLoadID() ?? "");
                writer.Write(corpse?.InnerPawn?.Name?.ToStringFull ?? "");
            });
        }
        // ThingRow is the one thing row builder: the cells read's
        // things and the bundle's things table.
        internal static Obs.Thing ThingRow(Thing thing, Common.ObservationContext context)
        {
            var row = new Obs.Thing { Entity = Entity(thing), StackCount = thing.stackCount, Forbidden = thing.IsForbidden(Faction.OfPlayer), Corpse = thing is Corpse };
            if (thing.def.category == ThingCategory.Item && (thing is Corpse || thing.def.IsIngestible)) row = FoodFacts(thing, row);
            return row;
        }

        // The food facts of an ingestible item or a corpse.
        private static Obs.Thing FoodFacts(Thing thing, Obs.Thing row)
        {
            var rot = thing.TryGetComp<CompRottable>();
            if (rot != null && rot.Active) row = row with { RotTicks = Math.Max(0, rot!.TicksUntilRotAtCurrentTemp) };
            row = row with { TemperatureC = Finite(thing.AmbientTemperature) };
            if (thing.Spawned) {
                row = row with { Roofed = thing.Position.Roofed(thing.Map) };
                var room = thing.Position.GetRoom(thing.Map);
                if (room != null) row = row with { Room = NativeRef.Room(room) };
            }
            row = row with { IsHumanMeat = HumanFoodFacts.ContainsHumanMeat(thing) };
            if (thing is Corpse corpse) {
                row = row with { MeatAmount = Finite(Math.Max(0, corpse.InnerPawn.GetStatValue(StatDefOf.MeatAmount))) };
                row = row with { BodySize = Finite(corpse.InnerPawn.BodySize) };
                row = row with { TileFootprint = 1 };
            }
            return row;
        }

        // The bundle's things table: the rows of things, each once,
        // in id order.
        internal static Obs.ThingsSnapshot Things(IEnumerable<Thing> things, Common.ObservationContext context)
        {
            var result = new Obs.ThingsSnapshot();
            foreach (var thing in things.GroupBy(t => t.GetUniqueLoadID()).Select(g => g.First()).OrderBy(t => t.GetUniqueLoadID(), StringComparer.Ordinal))
                result = result with { Things = [.. result.Things, ThingRow(thing, context)] };
            return result;
        }

        private static Common.Cell Cell(int x,int z)=>new Common.Cell { X=x,Z=z };
        private static string Identifier(string? value) => ProtoBoundary.IsIdentifier(value!) ? value! : throw new InvalidOperationException("Native identifier unavailable.");
        private static string Diagnostic(string value)=>PlacementPreviewOperation.Diagnostic(value);
        private static double Finite(double value)=>double.IsNaN(value)||double.IsInfinity(value)?throw new InvalidOperationException("Nonfinite native fact."):value;
        private static Common.Unavailable Unavailable(Common.UnavailableReason reason,string detail)=>new Common.Unavailable { Reason=reason,Detail=detail };
        private static Obs.ReadIssue Issue(string field,Common.UnavailableReason reason,string detail)=>new Obs.ReadIssue { Field=field,Unavailable=Unavailable(reason,detail) };
    }
}
