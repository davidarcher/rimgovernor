using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace RimGovernor.Native
{
    internal static class NativeObservationTools
    {
        // On the game thread: the typed core of the tool, shared with the in-process facade.
        // A refused request throws NativeRefusedException; a census that cannot be read completely throws an unavailable fault.
        internal static RimGovernor.Brain.GameFacts.Observations.StatusSnapshot HandleReadStatus(RimGovernor.Brain.GameOrders.Observations.StatusRequest parsed)
        {
            if (!ValidateStatus(parsed, out var invalidRequest)) throw NativeFaults.Refused(invalidRequest);
            if (!GameThread.ValidateIdentity(parsed.Scope?.ExpectedIdentity, out var map, out var context, out var failure))
                throw NativeFaults.Refused(failure);
            try { return Status(map, context); }
            catch (Exception readError)
            {
                ModLog.Error("observe", "Status read failed: " + readError);
                throw NativeFaults.Unavailable(Unavailable(RimGovernor.Brain.GameFacts.Common.UnavailableReason.ReadFailed,
                    PlacementPreviewOperation.Diagnostic("Native status facts could not be read completely: " + readError.GetType().Name + ": " + readError.Message)));
            }
        }

        // On the game thread: the typed core of the tool, shared with the in-process facade: the rectangle as the
        // frame grid reads it. A refused request or a rectangle that cannot be read completely throws.
        internal static RimGovernor.Brain.GameFacts.Observations.CellsSnapshot HandleGetCells(RimGovernor.Brain.GameOrders.Observations.GetCellsRequest parsed)
        {
            if (!ValidateCells(parsed, out var invalidRequest)) throw NativeFaults.Refused(invalidRequest);
            if (!GameThread.ValidateIdentity(parsed.Scope?.ExpectedIdentity, out var map, out var context, out var failure))
                throw NativeFaults.Refused(failure);
            var rectangle = parsed.Rectangle ?? throw new InvalidOperationException("Rectangle missing.");
            var min = rectangle.Minimum; var max = rectangle.Maximum;
            if (!new IntVec3(min.X, 0, min.Z).InBounds(map) || !new IntVec3(max.X, 0, max.Z).InBounds(map))
                throw NativeFaults.Refused(GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Selected cell is outside the current map."));
            try
            {
                return new RimGovernor.Brain.GameFacts.Observations.CellsSnapshot { Context = context,
                    MapSize = new RimGovernor.Brain.GameFacts.Observations.MapSize { Width = checked((uint)map.Size.x), Height = checked((uint)map.Size.z) },
                    Grid = CellGridReader.Read(map, min.X, min.Z, max.X - min.X + 1, max.Z - min.Z + 1) };
            }
            catch (Exception readError)
            {
                ModLog.Error("observe", "Cell read failed: " + readError);
                throw NativeFaults.Unavailable(Unavailable(RimGovernor.Brain.GameFacts.Common.UnavailableReason.ReadFailed,
                    PlacementPreviewOperation.Diagnostic("Native cell facts could not be read completely: " + readError.GetType().Name + ": " + readError.Message)));
            }
        }

        // On the game thread: the typed core of the tool.
        // A refused request throws NativeRefusedException; a site that cannot be read completely throws an unavailable fault.
        internal static RimGovernor.Brain.GameFacts.Observations.ExcavationSiteSnapshot HandleReadExcavationSite(RimGovernor.Brain.GameOrders.Observations.ExcavationSiteRequest parsed)
        {
            if (!NativeExcavationSite.Validate(parsed, out var invalid)) throw NativeFaults.Refused(invalid);
            if (!GameThread.ValidateIdentity(parsed.Scope?.ExpectedIdentity, out var map, out var context, out var failure))
                throw NativeFaults.Refused(failure);
            if (!NativeExcavationSite.OnMap(parsed, map, out var offMap)) throw NativeFaults.Refused(offMap);
            try { return NativeExcavationSite.Read(map, parsed, context); }
            catch (Exception readError)
            {
                ModLog.Error("observe", "Excavation site read failed: " + readError);
                throw NativeFaults.Unavailable(new RimGovernor.Brain.GameFacts.Common.Unavailable { Reason = RimGovernor.Brain.GameFacts.Common.UnavailableReason.ReadFailed,
                    Detail = PlacementPreviewOperation.Diagnostic("Native excavation site facts could not be read completely: " + readError.GetType().Name + ": " + readError.Message) });
            }
        }


        // Traversal's doorway fact, shared with the planning
        // window view's capture.
        internal static bool CellDoorway(Map map, IntVec3 cell) => cell.GetDoor(map) != null || cell.GetThingList(map).Any(t => (t is Blueprint || t is Frame)
            && t.def.entityDefToBuild is ThingDef built && typeof(Building_Door).IsAssignableFrom(built.thingClass));
        internal static bool ValidateStatus(RimGovernor.Brain.GameOrders.Observations.StatusRequest request, out RimGovernor.Brain.GameFacts.Common.Failure failure)
        {
            failure = GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Valid identity scope required.");
            return request != null && request.Scope?.ExpectedIdentity != null;
        }
        internal static bool ValidateCells(RimGovernor.Brain.GameOrders.Observations.GetCellsRequest request, out RimGovernor.Brain.GameFacts.Common.Failure failure)
        {
            failure = GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Valid identity and an inclusive rectangle of at most 1048576 cells required.");
            if (request == null || request.Scope?.ExpectedIdentity == null) return false;
            if (request.Rectangle == null) return false;
            var min = request.Rectangle.Minimum; var max = request.Rectangle.Maximum;
            if (min.X < 0 || min.Z < 0 || max.X < min.X || max.Z < min.Z) return false;
            return ((long)max.X - min.X + 1) * ((long)max.Z - min.Z + 1) <= 1 << 20;
        }

        /// The Chebyshev radius around a colonist within which an unowned downed pawn or predator is a threat row; the
        /// brain classifies with the same radius (EmergencyDecode.ThreatProximityRadius).
        internal const double PredatorRadius = 30;

        // The status census, whole: the free colonists, the dependent patients and every threat row. The status read and
        // the bundle's emergency section both read it; anything it cannot read throws.
        internal static RimGovernor.Brain.GameFacts.Observations.StatusSnapshot Status(Map map, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var spawned = map.mapPawns.AllPawnsSpawned.ToList();
            var colonists = spawned.Where(p => !p.Dead && p.IsFreeColonist).ToList();
            var player = Faction.OfPlayerSilentFail ?? throw new InvalidOperationException("Player faction missing.");
            var threats = new RimGovernor.Brain.GameFacts.Observations.ThreatsSnapshot { Pawns = [], HostileBuildings = [] };
            var colonistCells = colonists.Select(p => (p.Position.x, p.Position.z)).ToList();
            NativeThreatClassifier.Collect(spawned.Where(p => !p.Dead && !p.IsColonist).ToList(), pawn => ThreatFactsOf(pawn, player),
                colonistCells, PredatorRadius, NativePawnObservationTools.Ref, ref threats);
            var buildings = new List<RimGovernor.Brain.GameFacts.Observations.ThreatBuilding>();
            foreach (var building in HostileBuildings(map, player)) {
                var rect = building.OccupiedRect();
                buildings.Add(new RimGovernor.Brain.GameFacts.Observations.ThreatBuilding {
                    Building = Entity(building),
                    Snapshot = Token(context.Identity, building),
                    NearestColonist = colonists.Count == 0 ? new RimGovernor.Brain.GameFacts.Observations.ColonistDistance.NoColonist()
                        : new RimGovernor.Brain.GameFacts.Observations.ColonistDistance.Nearest(colonists.Min(p => Math.Max(Math.Abs(p.Position.x-building.Position.x),Math.Abs(p.Position.z-building.Position.z)))),
                    Occupied = new RimGovernor.Brain.GameFacts.Observations.Rectangle { Minimum = Cell(rect.minX, rect.minZ), Maximum = Cell(rect.maxX, rect.maxZ) },
                    Passive = building is Hive ? !HiveEngaging(building, colonists, spawned, player) : Dormant(building),
                    Hive = building is Hive,
                    Fogged = building.Position.Fogged(map),
                });
            }
            return new RimGovernor.Brain.GameFacts.Observations.StatusSnapshot {
                Context = context,
                DependentPatients = NativeDependentPatients.Observe(map),
                Colonists = [.. colonists.Select(NativePawnObservationTools.Ref)],
                Threats = threats with { HostileBuildings = [.. buildings] },
            };
        }

        // The cheap facts the threat filter branches on and its rows carry; the brain classifies them.
        private static ThreatFacts ThreatFactsOf(Pawn pawn, Faction player)
        {
            var ours = pawn.Faction == player; var hunt = pawn.CurJobDef?.defName == "PredatorHunt"; var held = pawn.HostFaction == player;
            var facts = new ThreatFacts { Ours = ours, Mental = pawn.MentalStateDef?.defName,
                FactionHostile = pawn.Faction != null && !ours && !held && pawn.Faction.HostileTo(player), PredatorHunt = hunt,
                PrisonBreak = held && PrisonBreakUtility.IsPrisonBreaking(pawn),
                Downed = pawn.Downed, Predator = pawn.RaceProps.predator, X = pawn.Position.x, Z = pawn.Position.z };
            facts.Passive = facts.FactionHostile && (pawn.Faction == Faction.OfInsects ? !InsectEngaging(pawn, player) : Dormant(pawn));
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
        // while asleep and engaging once awake. A thing without the comp never sleeps.
        private static bool Dormant(Thing thing) => thing.TryGetComp<CompCanBeDormant>() is CompCanBeDormant comp && !comp.Awake;
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
        // hive (RimWorld.Hive is a ThingWithComps, so it is read by type, not
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
            map.listerThings.GetThingsOfType<Hive>().Concat<Thing>(map.listerBuildings.allBuildingsNonColonist)
                .ById(id) is Thing t && HostileThing(t, player) ? t : null;
        internal static List<Thing> HostileBuildings(Map map, Faction player)
        {
            var found = new List<Thing>();
            foreach (var hive in map.listerThings.GetThingsOfType<Hive>()) if (HostileBuilding(hive, player)) found.Add(hive);
            foreach (var building in map.listerBuildings.allBuildingsNonColonist) if (!(building is Hive) && HostileBuilding(building, player)) found.Add(building);
            return found.OrderBy(t => t.thingIDNumber).ToList();
        }
        internal static RimGovernor.Brain.GameFacts.Observations.JobEvidence JobRow(Verse.AI.Job? job, int queuedJobs)
        {
            if (queuedJobs < 0) throw new InvalidOperationException("Negative native queued job count.");
            var row = new RimGovernor.Brain.GameFacts.Observations.JobEvidence { PlayerForced = job?.playerForced ?? false, QueuedJobs = (uint)queuedJobs };
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
                    row = row with { TargetA = new RimGovernor.Brain.GameFacts.Observations.TargetRef { Target = new RimGovernor.Brain.GameFacts.Observations.TargetRefTarget.EntityCase(NativeRef.Thing(target.Thing), target.Thing.Spawned ? new RimGovernor.Brain.GameFacts.Observations.EntityPlace.OnMap(target.Thing.Map.uniqueID, Cell(target.Thing.Position.x, target.Thing.Position.z)) : new RimGovernor.Brain.GameFacts.Observations.EntityPlace.Unplaced()) } };
                } else if (target.IsValid) row = row with { TargetA = new RimGovernor.Brain.GameFacts.Observations.TargetRef { Target = Cell(target.Cell.x, target.Cell.z) } };
                else row = row with { TargetA = new RimGovernor.Brain.GameFacts.Observations.TargetRef { Target = new RimGovernor.Brain.GameFacts.Common.Unavailable { Reason = RimGovernor.Brain.GameFacts.Common.UnavailableReason.NotApplicable, Detail = "Job has no target." } } };
            }
            return row;
        }

        private static RimGovernor.Brain.GameFacts.Observations.EntityRef Entity(Thing thing)
        {
            // A held thing (gear, a buried corpse) is at its holder's cell.
            RimGovernor.Brain.GameFacts.Observations.EntityPlace place = thing.MapHeld is { } map ? new RimGovernor.Brain.GameFacts.Observations.EntityPlace.OnMap(map.uniqueID, Cell(thing.PositionHeld.x, thing.PositionHeld.z)) : new RimGovernor.Brain.GameFacts.Observations.EntityPlace.Unplaced();
            return new RimGovernor.Brain.GameFacts.Observations.EntityRef { Id=Identifier(thing.GetUniqueLoadID()), DefName=Identifier(thing.def.defName), Label=Diagnostic(thing.LabelCap), Place=place };
        }
        // Self-checked CAS token for one thing (observation rows and combat
        // orders compare it): its version over def, cell, stack, forbidden,
        // rot stage and a corpse's faction and name.
        internal static string Token(RimGovernor.Brain.GameFacts.Common.Identity identity, Thing thing)
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
        internal static RimGovernor.Brain.GameFacts.Observations.Thing ThingRow(Thing thing, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            return new RimGovernor.Brain.GameFacts.Observations.Thing { Entity = Entity(thing), Forbidden = thing.IsForbidden(Faction.OfPlayer), Corpse = thing is Corpse };
        }

        // The bundle's things table: the rows of things, each once,
        // in id order.
        internal static RimGovernor.Brain.GameFacts.Observations.ThingsSnapshot Things(IEnumerable<Thing> things, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var result = new RimGovernor.Brain.GameFacts.Observations.ThingsSnapshot();
            foreach (var thing in things.GroupBy(t => t.GetUniqueLoadID()).Select(g => g.First()).OrderBy(t => t.GetUniqueLoadID(), StringComparer.Ordinal))
                result = result with { Things = [.. result.Things, ThingRow(thing, context)] };
            return result;
        }

        private static RimGovernor.Brain.Domain.Cell Cell(int x,int z)=>new RimGovernor.Brain.Domain.Cell { X=x,Z=z };
        private static string Identifier(string? value) => GameThread.IsIdentifier(value) ? value : throw new InvalidOperationException("Native identifier unavailable.");
        private static string Diagnostic(string value)=>PlacementPreviewOperation.Diagnostic(value);
        private static double Finite(double value)=>double.IsNaN(value)||double.IsInfinity(value)?throw new InvalidOperationException("Nonfinite native fact."):value;
        private static RimGovernor.Brain.GameFacts.Common.Unavailable Unavailable(RimGovernor.Brain.GameFacts.Common.UnavailableReason reason,string detail)=>new RimGovernor.Brain.GameFacts.Common.Unavailable { Reason=reason,Detail=detail };
    }
}
