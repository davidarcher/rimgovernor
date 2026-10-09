#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI.Group;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    public sealed class NativeObservationTools
    {
        [Tool("rimgovernor/observations_read_status", Title = "Read colony status",
            Description = "Official StatusRequest ProtoJSON. Colonists/threats default true, detail false, predator radius30. Read-only simulation facts; no clock/UI operations. Lists are complete.")]
        [ToolResponse("payload", "string", "Official observations StatusReply ProtoJSON.", Always = true)]
        public async Task<object> ReadStatus(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Raw value must be a StatusRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, "rimgovernor/observations_read_status", request!, Obs.StatusRequest.Parser, out var parsed, out var failure)
                || !ValidateStatus(parsed, out failure)) return ProtoBoundary.Encode(new Obs.StatusReply { Failure = failure });
            return await ProtoBoundary.OnMainThread(ctx, () => {
                if (!ProtoBoundary.ValidateIdentity(parsed.Scope?.ExpectedIdentity, out var map, out var context, out failure))
                    return ProtoBoundary.Encode(new Obs.StatusReply { Failure = failure });
                try { return ProtoBoundary.Encode(new Obs.StatusReply { Observed = Status(map, parsed, context) }); }
                catch (Exception) { return ProtoBoundary.Encode(new Obs.StatusReply { Unavailable = Unavailable(Common.UnavailableReason.ReadFailed, "Native status facts could not be read completely.") }); }
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("rimgovernor/observations_get_cells", Title = "Read map cells",
            Description = "Official GetCellsRequest ProtoJSON. An inclusive rectangle of at most 1048576 cells on the map, returned with the map dimensions as a CellGrid keyframe (the snapshot frame grid's arrays; a fogged cell is not held). The grid carries each cell's things, terrain and foundation affordances. Read-only.")]
        [ToolResponse("payload", "string", "Official observations GetCellsReply ProtoJSON.", Always = true)]
        public async Task<object> GetCells(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Raw value must be a GetCellsRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, "rimgovernor/observations_get_cells", request!, Obs.GetCellsRequest.Parser, out var parsed, out var failure)
                || !ValidateCells(parsed, out failure)) return ProtoBoundary.Encode(new Obs.GetCellsReply { Failure = failure });
            return await ProtoBoundary.OnMainThread(ctx, () => {
                if (!ProtoBoundary.ValidateIdentity(parsed.Scope?.ExpectedIdentity, out var map, out var context, out failure))
                    return ProtoBoundary.Encode(new Obs.GetCellsReply { Failure = failure });
                return ProtoBoundary.Encode(ReadCells(map, parsed, context));
            }, cancellationToken).ConfigureAwait(false);
        }

        // ReadCells is the read on the main thread under a validated
        // identity: the rectangle as the frame grid reads it.
        internal static Obs.GetCellsReply ReadCells(Map map, Obs.GetCellsRequest parsed, Common.ObservationContext context)
        {
            try {
                var min = parsed.Rectangle.Minimum; var max = parsed.Rectangle.Maximum;
                if (!new IntVec3(min.X, 0, min.Z).InBounds(map) || !new IntVec3(max.X, 0, max.Z).InBounds(map)) return new Obs.GetCellsReply {
                    Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Selected cell is outside the current map.") };
                var read = CellGridEncoder.Read(map, min.X, min.Z, max.X - min.X + 1, max.Z - min.Z + 1);
                var snapshot = new Obs.CellsSnapshot { Context = context,
                    MapSize = new Obs.MapSize { Width = checked((uint)map.Size.x), Height = checked((uint)map.Size.z) },
                    Grid = CellGridEncoder.Encode(read, null) };
                return new Obs.GetCellsReply { Observed = snapshot };
            }
            catch (Exception) { return new Obs.GetCellsReply { Unavailable = Unavailable(Common.UnavailableReason.ReadFailed, "Native cell facts could not be read completely.") }; }
        }

        [Tool("rimgovernor/observations_read_excavation_site", Title = "Read excavation site",
            Description = "Official ExcavationSiteRequest ProtoJSON. Certifies exact rock cells for staged room/corridor excavation: per-cell rock, roof, fog, designation and eligibility plus counterfactual roof support after removing every requested cell, pending collapse and mining worker access. Fogged cells are unknown. Read-only.")]
        [ToolResponse("payload", "string", "Official observations ExcavationSiteReply ProtoJSON.", Always = true)]
        public async Task<object> ReadExcavationSite(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Raw value must be an ExcavationSiteRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, "rimgovernor/observations_read_excavation_site", request!, Obs.ExcavationSiteRequest.Parser, out var parsed, out var failure)
                || !NativeExcavationSite.Validate(parsed, out failure)) return ProtoBoundary.Encode(new Obs.ExcavationSiteReply { Failure = failure });
            return await ProtoBoundary.OnMainThread(ctx, () => {
                if (!ProtoBoundary.ValidateIdentity(parsed.Scope?.ExpectedIdentity, out var map, out var context, out failure))
                    return ProtoBoundary.Encode(new Obs.ExcavationSiteReply { Failure = failure });
                try { return ProtoBoundary.Encode(new Obs.ExcavationSiteReply { Observed = NativeExcavationSite.Read(map, parsed, context) }); }
                catch (Exception) { return ProtoBoundary.Encode(new Obs.ExcavationSiteReply { Unavailable = Unavailable(Common.UnavailableReason.ReadFailed, "Native excavation site facts could not be read completely.") }); }
            }, cancellationToken).ConfigureAwait(false);
        }


        // Traversal's doorway fact, shared with the planning
        // window view's capture.
        internal static bool CellDoorway(Map map, IntVec3 cell) => cell.GetDoor(map) != null || cell.GetThingList(map).Any(t => (t is Blueprint || t is Frame)
            && t.def.entityDefToBuild is ThingDef built && typeof(Building_Door).IsAssignableFrom(built.thingClass));
        internal static bool ValidateStatus(Obs.StatusRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Valid identity scope and finite nonnegative predator radius required.");
            return request != null && request.Scope?.ExpectedIdentity != null
                && (!request.HasPredatorRadius || !double.IsNaN(request.PredatorRadius) && !double.IsInfinity(request.PredatorRadius) && request.PredatorRadius >= 0);
        }
        internal static bool ValidateCells(Obs.GetCellsRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Valid identity and an inclusive rectangle of at most 1048576 cells required.");
            if (request == null || request.Scope?.ExpectedIdentity == null) return false;
            var min = request.Rectangle?.Minimum; var max = request.Rectangle?.Maximum;
            if (min == null || max == null || !min.HasX || !min.HasZ || !max.HasX || !max.HasZ || min.X < 0 || min.Z < 0 || max.X < min.X || max.Z < min.Z) return false;
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
            var result = new Obs.StatusSnapshot { Context = context };
            var wantColonists = !request.HasColonists || request.Colonists;
            var wantThreats = !request.HasThreats || request.Threats;
            if (!wantColonists && !wantThreats) {
                result.Issues.Add(Issue("colonists", Common.UnavailableReason.NotRequested, "Colonist section not requested."));
                result.Issues.Add(Issue("threats", Common.UnavailableReason.NotRequested, "Threat section not requested.")); return result;
            }
            var spawned = map.mapPawns.AllPawnsSpawned.ToList();
            var colonists = spawned.Where(p => !p.Dead && p.IsFreeColonist).ToList();
            // Colonists and threats are pawn table references.
            if (wantColonists) foreach (var pawn in colonists) result.Colonists.Add(NativePawnObservationTools.Ref(pawn));
            else result.Issues.Add(Issue("colonists", Common.UnavailableReason.NotRequested, "Colonist section not requested."));
            if (!wantThreats) { result.Issues.Add(Issue("threats", Common.UnavailableReason.NotRequested, "Threat section not requested.")); return result; }
            var player = Faction.OfPlayerSilentFail ?? throw new InvalidOperationException("Player faction missing.");
            var threats = new Obs.ThreatsSnapshot(); var radius = request.HasPredatorRadius ? request.PredatorRadius : 30;
            var colonistCells = colonists.Select(p => (p.Position.x, p.Position.z)).ToList();
            NativeThreatClassifier.Collect(spawned.Where(p => !p.Dead && !p.IsColonist).ToList(), pawn => ThreatFactsOf(pawn, player),
                colonistCells, radius, NativePawnObservationTools.Ref, pawn => NativeRef.Thing(HuntedPawn(pawn)!), threats);
            foreach (var building in HostileBuildings(map, player)) {
                var row = new Obs.ThreatBuilding { Building = Entity(building), HostileReason = "faction:"+building.Faction!.GetUniqueLoadID(),
                    HitPoints = building.HitPoints, MaxHitPoints = building.MaxHitPoints };
                row.BuildingSnapshot = new Obs.SnapshotRef { Context = context.Clone(), EntityId = row.Building.Id, Token = Token(context.Identity, building) };
                if (colonists.Count > 0) row.NearestColonistDistance = colonists.Min(p => Math.Max(Math.Abs(p.Position.x-building.Position.x),Math.Abs(p.Position.z-building.Position.z)));
                var rect = building.OccupiedRect();
                row.Occupied = new Obs.Rectangle { Minimum = Cell(rect.minX, rect.minZ), Maximum = Cell(rect.maxX, rect.maxZ) };
                if (building is Hive) row.Passive = !HiveEngaging(building, colonists, spawned, player);
                else if (Dormant(building) is bool dormant) row.Passive = dormant;
                row.Mortar = building.def.building?.IsMortar == true;
                threats.HostileBuildings.Add(row);
            }
            result.Threats=threats; return result;
        }

        // The cheap facts the threat filter branches on and its rows carry
        //; Go classifies them.
        private static ThreatFacts ThreatFactsOf(Pawn pawn, Faction player)
        {
            var ours = pawn.Faction == player; var hunt = pawn.CurJobDef?.defName == "PredatorHunt"; var held = pawn.HostFaction == player;
            var facts = new ThreatFacts { Ours = ours, Mental = pawn.MentalStateDef?.defName,
                FactionHostile = pawn.Faction != null && !ours && !held && pawn.Faction.HostileTo(player), PredatorHunt = hunt,
                PrisonBreak = held && PrisonBreakUtility.IsPrisonBreaking(pawn),
                Downed = pawn.Downed, Predator = pawn.RaceProps.predator, X = pawn.Position.x, Z = pawn.Position.z };
            if (facts.FactionHostile) facts.FactionId = pawn.Faction!.GetUniqueLoadID();
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
                row.DefName = Identifier(job.def.defName);
                row.LoadId = job.loadID.ToString(System.Globalization.CultureInfo.InvariantCulture);
                // The work giver's WorkTypeDef names the labor the job spends
                // (a cut designation, a bill, a frame); a job no giver issued
                // (a forced order, rest, a meal, wandering) carries none.
                var workType = job.workGiverDef?.workType?.defName;
                if (workType != null) row.WorkTypeDefName = Identifier(workType);
                // targetA attributes the job to the thing or cell it works
                //: a haul for one concern is no evidence for another.
                // A thing target is its Ref and cell; an invalid target reads
                // unavailable, so an absent field means an older producer.
                // A delivery (HaulToContainer) carries its material as
                // targetA and works for the frame or bench in targetB.
                var target = job.def == JobDefOf.HaulToContainer && job.targetB.HasThing ? job.targetB : job.targetA;
                if (target.HasThing) {
                    row.TargetA = new Obs.TargetRef { Entity = NativeRef.Thing(target.Thing) };
                    if (target.Thing.Spawned) row.TargetACell = Cell(target.Thing.Position.x, target.Thing.Position.z);
                } else if (target.IsValid) row.TargetA = new Obs.TargetRef { Cell = Cell(target.Cell.x, target.Cell.z) };
                else row.TargetA = new Obs.TargetRef { Unavailable = new Common.Unavailable { Reason = Common.UnavailableReason.NotApplicable, Detail = "Job has no target." } };
            } else row.Issues.Add(Issue("current_job", Common.UnavailableReason.NotApplicable, "Pawn has no current job."));
            return row;
        }

        internal static Obs.PawnState PawnRow(Pawn pawn, bool detail, Common.ObservationContext context)
        {
            var row = new Obs.PawnState { Pawn=Entity(pawn), KindDefName=Identifier(pawn.kindDef?.defName), Dead=pawn.Dead, Downed=pawn.Downed,
                Drafted=pawn.drafter?.Drafted == true, InBed=RestUtility.InBed(pawn), Colonist=pawn.IsColonist, FreeColonist=pawn.IsFreeColonist,
                Prisoner=pawn.IsPrisoner, Humanlike=pawn.RaceProps.Humanlike, Animal=pawn.RaceProps.Animal, Mechanoid=pawn.RaceProps.IsMechanoid };
            var constructionCapable = ConstructionSkillGuard.Capability(pawn);
            if (constructionCapable.HasValue) row.ConstructionCapable = constructionCapable.Value;
            if (pawn.Faction != null) row.Faction=NativeRef.Of(Identifier(pawn.Faction.GetUniqueLoadID()));
            // Fog is the discovery fact, not a guess at reachability: a hostile
            // the colony has never seen is no emergency.
            row.Fogged = pawn.Spawned && pawn.Map != null && pawn.Position.Fogged(pawn.Map);
            var mental = pawn.MentalState;
            if (mental != null) {
                row.MentalState=Identifier(mental.def.defName);
                row.MentalStateIsAggro=mental.def.IsAggro;
                row.MentalStateTicks=mental.Age;
            }
            // Inspiration: empty is a known "none"; a pawn without a
            // mind state handler leaves the field absent (unknown).
            if (pawn.mindState?.inspirationHandler != null)
                row.Inspiration=pawn.mindState.inspirationHandler.CurStateDef?.defName ?? "";
            // Lord evidence is the game's own group-AI class names: a raid's job
            // (assault/siege/stage-then-attack) and its current toil (the sapper
            // and breach toils are distinct classes). No lord means no field.
            var lord = pawn.GetLord();
            if (lord?.LordJob != null) { row.LordJobClass=Identifier(lord.LordJob.GetType().Name); if (lord.CurLordToil != null) row.LordToilClass=Identifier(lord.CurLordToil.GetType().Name); }
            if (pawn.jobs?.jobQueue != null) row.Job=JobRow(pawn.CurJob, pawn.jobs.jobQueue.Count);
            else row.Issues.Add(Issue("job",Common.UnavailableReason.NativeComponentMissing,"Pawn job tracker or queue is unavailable."));
            row.Biotech = NativeBiotechFacts.Pawn(pawn);
            row.Anomaly = NativeAnomalyFacts.Pawn(pawn);
            NativePawnRoyalty.Apply(pawn, row);
            NativePawnControlObservation.Apply(pawn, row, context);
            var needs=new Obs.PawnNeeds(); row.Needs=needs;
            if (pawn.needs?.mood != null) needs.Mood=Finite(pawn.needs.mood.CurLevelPercentage);
            else needs.Issues.Add(Issue("mood",Common.UnavailableReason.NativeComponentMissing,"Mood tracker unavailable."));
            if (detail) {
                if(pawn.needs?.food != null) { needs.Food=Finite(pawn.needs.food.CurLevelPercentage); needs.HungerCategory=NativeEnums.Hunger(pawn.needs.food.CurCategory); }
                else needs.Issues.Add(Issue("food",Common.UnavailableReason.NativeComponentMissing,"Food tracker unavailable."));
                if(pawn.needs?.rest != null) needs.Rest=Finite(pawn.needs.rest.CurLevelPercentage); else needs.Issues.Add(Issue("rest",Common.UnavailableReason.NativeComponentMissing,"Rest tracker unavailable."));
                if(pawn.needs?.joy != null) needs.Joy=Finite(pawn.needs.joy.CurLevelPercentage); else needs.Issues.Add(Issue("joy",Common.UnavailableReason.NativeComponentMissing,"Joy tracker unavailable."));
            }
            // Psyfocus: absent without Royalty or a psylink.
            if (ModsConfig.RoyaltyActive && pawn.psychicEntropy != null) { var psylink=pawn.GetPsylinkLevel();
                if (psylink > 0) { needs.Psyfocus=Finite(pawn.psychicEntropy.CurrentPsyfocus); needs.PsyfocusTarget=Finite(pawn.psychicEntropy.TargetPsyfocus); needs.PsylinkLevel=psylink; } }
            var breaker=pawn.mindState?.mentalBreaker;
            if(breaker != null) { needs.BreakThresholdMinor=Finite(breaker.BreakThresholdMinor); needs.BreakThresholdMajor=Finite(breaker.BreakThresholdMajor); needs.BreakThresholdExtreme=Finite(breaker.BreakThresholdExtreme);
                if(needs.HasMood) needs.BreakRisk=needs.Mood<=needs.BreakThresholdExtreme?Obs.BreakRisk.Extreme:needs.Mood<=needs.BreakThresholdMajor?Obs.BreakRisk.Major:needs.Mood<=needs.BreakThresholdMinor?Obs.BreakRisk.Minor:Obs.BreakRisk.None; }
            else needs.Issues.Add(Issue("break_thresholds",Common.UnavailableReason.NativeComponentMissing,"Mental breaker unavailable."));
            if(pawn.health?.summaryHealth == null || pawn.health.hediffSet == null) row.Issues.Add(Issue("health",Common.UnavailableReason.NativeComponentMissing,"Health tracker unavailable."));
            else {
                var health=new Obs.PawnHealth { SummaryFraction=Finite(pawn.health.summaryHealth.SummaryHealthPercent), NeedsTend=pawn.health.HasHediffsNeedingTend(false), BleedRatePerDay=Finite(pawn.health.hediffSet.BleedRateTotal) };
                health.Bleeding=health.BleedRatePerDay>0;
                if(detail) {
                    foreach(var h in pawn.health.hediffSet.hediffs) {
                        var condition=new Obs.Hediff { DefName=Identifier(h.def.defName), Severity=Finite(h.Severity), SeverityLabel=Diagnostic(h.SeverityLabel??""), Visible=h.Visible };
                        if(h.Part != null) { condition.PartDefName=Identifier(h.Part.def.defName); condition.PartLabel=Diagnostic(h.Part.LabelCap); condition.PartIndex=pawn.RaceProps.body.AllParts.IndexOf(h.Part); }
                        health.Hediffs.Add(condition);
                    }
                    health.HediffCompleteness=Complete(health.Hediffs.Count);
                } else health.Issues.Add(Issue("hediffs",Common.UnavailableReason.NotRequested,"Hediff detail not requested."));
                row.Health=health;
            }
            return row;
        }
        private static Obs.EntityRef Entity(Thing thing)
        {
            var row = new Obs.EntityRef { Id=Identifier(thing.GetUniqueLoadID()), DefName=Identifier(thing.def.defName), Label=Diagnostic(thing.LabelCap) };
            // A held thing (gear, a buried corpse) is at its holder's cell.
            if (thing.MapHeld != null) { row.MapId=thing.MapHeld.uniqueID; row.Position=Cell(thing.PositionHeld.x,thing.PositionHeld.z); }
            return row;
        }
        // Self-computed, self-checked CAS token for one thing; the hash input
        // is a wire contract (observation rows and combat orders compare it).
        internal static string Token(Common.Identity identity, Thing thing)
        {
            var rot = thing.TryGetComp<CompRottable>();
            var corpse = thing as Corpse;
            using (var bytes = new System.IO.MemoryStream())
            {
                using (var writer = new System.IO.BinaryWriter(bytes, Encoding.UTF8, true))
                {
                    writer.Write(identity.ColonyId); writer.Write(identity.LoadToken); writer.Write(identity.MapId);
                    writer.Write(thing.GetUniqueLoadID()); writer.Write(thing.def.defName);
                    writer.Write(thing.Position.x); writer.Write(thing.Position.z); writer.Write(thing.stackCount);
                    writer.Write(thing.IsForbidden(Faction.OfPlayer)); writer.Write(rot?.Stage.ToString() ?? "");
                    writer.Write(corpse?.InnerPawn?.Faction?.GetUniqueLoadID() ?? "");
                    writer.Write(corpse?.InnerPawn?.Name?.ToStringFull ?? "");
                }
                using (var hash = System.Security.Cryptography.SHA256.Create())
                    return "waste-" + BitConverter.ToString(hash.ComputeHash(bytes.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }
        // ThingRow is the one thing row builder: the cells read's
        // things and the bundle's things table. Each row carries the thing's
        // own CAS token via Token, the self-computed hash a combat order
        // checks.
        internal static Obs.Thing ThingRow(Thing thing, Common.ObservationContext context)
        {
            var entity = Entity(thing);
            var row = new Obs.Thing {
                Thing_ = entity, Snapshot = new Obs.SnapshotRef { Context = context.Clone(), EntityId = entity.Id, Token = Token(context.Identity, thing) }, StackCount = thing.stackCount,
                Forbidden = thing.IsForbidden(Faction.OfPlayer),
            };
            // The stuff a thing is made of keys its catalog stat row (def, stuff).
            if (thing.Stuff != null) row.Stuff = Identifier(thing.Stuff.defName);
            // A plant's growth marks the cell sown.
            if (thing is Plant plant) { row.Growth = Finite(plant.Growth); row.HarvestableNow = plant.HarvestableNow; }
            if (thing.def.category == ThingCategory.Item && (thing is Corpse || thing.def.IsIngestible)) FoodFacts(thing, row);
            return row;
        }

        // The food facts of an ingestible item or a corpse.
        private static void FoodFacts(Thing thing, Obs.Thing row)
        {
            var rot = thing.TryGetComp<CompRottable>();
            if (rot != null && rot.Active) row.RotTicks = Math.Max(0, rot!.TicksUntilRotAtCurrentTemp);
            row.TemperatureC = Finite(thing.AmbientTemperature);
            if (thing.Spawned) {
                row.Roofed = thing.Position.Roofed(thing.Map);
                var room = thing.Position.GetRoom(thing.Map);
                if (room != null) row.Room = NativeRef.Room(room);
            }
            row.IsHumanMeat = HumanFoodFacts.ContainsHumanMeat(thing);
            row.Corpse = thing is Corpse;
            if (thing is Corpse corpse) {
                row.MeatAmount = Finite(Math.Max(0, corpse.InnerPawn.GetStatValue(StatDefOf.MeatAmount)));
                row.BodySize = Finite(corpse.InnerPawn.BodySize);
                row.TileFootprint = 1;
            }
        }

        // The bundle's things table: the rows of things, each once,
        // in id order.
        internal static Obs.ThingsSnapshot Things(IEnumerable<Thing> things, Common.ObservationContext context)
        {
            var result = new Obs.ThingsSnapshot { Context = context };
            foreach (var thing in things.GroupBy(t => t.GetUniqueLoadID()).Select(g => g.First()).OrderBy(t => t.GetUniqueLoadID(), StringComparer.Ordinal))
                result.Things.Add(ThingRow(thing, context));
            return result;
        }

        private static Common.Cell Cell(int x,int z)=>new Common.Cell { X=x,Z=z };
        private static string Identifier(string? value) => ProtoBoundary.IsIdentifier(value!) ? value! : throw new InvalidOperationException("Native identifier unavailable.");
        private static string Diagnostic(string value)=>PlacementPreviewOperation.Diagnostic(value);
        private static double Finite(double value)=>double.IsNaN(value)||double.IsInfinity(value)?throw new InvalidOperationException("Nonfinite native fact."):value;
        private static Common.Unavailable Unavailable(Common.UnavailableReason reason,string detail)=>new Common.Unavailable { Reason=reason,Detail=detail };
        private static Obs.ReadIssue Issue(string field,Common.UnavailableReason reason,string detail)=>new Obs.ReadIssue { Field=field,Unavailable=Unavailable(reason,detail) };
        private static Obs.Completeness Complete(int count)=>new Obs.Completeness();
    }
}
