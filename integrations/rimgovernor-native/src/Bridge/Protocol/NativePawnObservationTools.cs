#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;
using Common = RimGovernor.Contracts.Wire.Common;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    internal static class NativePawnObservationTools
    {
        // On the game thread: the typed core of the tool, shared with the in-process facade. A refused request
        // or pawn facts that cannot be read completely throw.
        internal static Obs.PawnSnapshot HandleListPawns(Obs.ListPawnsRequest parsed)
        {
            if (!Validate(parsed, out var invalidRequest)) throw NativeFaults.Refused(invalidRequest);
            if (!ProtoBoundary.ValidateIdentity(parsed.Scope?.ExpectedIdentity, out var map, out var context, out var invalid))
                throw NativeFaults.Refused(invalid);
            try { return Read(map, parsed, context); }
            catch (Exception error) { throw Failed("Pawn facts", error); }
        }

        // On the game thread: the opt-in tend table.
        internal static Obs.TendDoctorSnapshot HandleTendDoctors(Obs.TendDoctorsRequest parsed)
        {
            if (!ValidateTend(parsed, out var invalidRequest)) throw NativeFaults.Refused(invalidRequest);
            if (!ProtoBoundary.ValidateIdentity(parsed.Scope.ExpectedIdentity, out var map, out var context, out var invalid))
                throw NativeFaults.Refused(invalid);
            try {
                var wanted = new HashSet<string>(parsed.PawnIds, StringComparer.Ordinal);
                var pawns = OnMap(map, true).Where(p => wanted.Contains(p.GetUniqueLoadID())).OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToList();
                return new Obs.TendDoctorSnapshot { Context = context, Doctors = [.. NativePawnDetails.Tend(pawns, parsed.ReachBudgetMs)] };
            }
            catch (Exception error) { throw Failed("Tend facts", error); }
        }

        private static Exception Failed(string what, Exception error)
        {
            ModLog.Error("observe", what + " read failed: " + error);
            return NativeFaults.Unavailable(Unavailable(Common.UnavailableReason.ReadFailed,
                PlacementPreviewOperation.Diagnostic(what + " could not be read completely: " + error.GetType().Name + ": " + error.Message)));
        }

        // The map's spawned pawns, and with dead the corpses' inner pawns, each once.
        private static List<Pawn> OnMap(Map map, bool dead)
        {
            var source = map.mapPawns.AllPawnsSpawned.ToList();
            if (dead) source.AddRange(map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).Cast<Corpse>().Select(c => c.InnerPawn));
            if (source.Any(p => p == null)) throw new InvalidOperationException("Null native pawn.");
            return source.Distinct().ToList();
        }

        // On the main thread. The filter picks the pawns from native facts
        // before any row is built: a frame asks for the colonists by id, and
        // the map holds every animal too.
        private static Obs.PawnSnapshot Read(Map map, Obs.ListPawnsRequest parsed, Common.ObservationContext context)
        {
            var filter = parsed.Filter;
            var facts = new MapFacts(map);
            using var surgery = new NativePawnDetails.SurgeryScope();
            var rows = OnMap(map, filter?.IncludeDead == true)
                .Where(p => Matches(p, filter, facts))
                .OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal)
                .Select(p => Row(p, facts, context));
            return new Obs.PawnSnapshot { Context = context, Pawns = [.. rows] };
        }

        // The bundle's pawn table: every spawned pawn on the map, each a whole row.
        internal static Obs.PawnSnapshot Table(Map map, Common.ObservationContext context)
        {
            var facts = new MapFacts(map);
            using var surgery = new NativePawnDetails.SurgeryScope();
            var rows = OnMap(map, false).OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).Select(p => Row(p, facts, context));
            return new Obs.PawnSnapshot { Context = context, Pawns = [.. rows] };
        }

        // The pawn-independent facts of one map's rows: its live colonists
        // (the nearest-colonist distance) and its hostiles' armor, read once.
        // A pawn on no map (a caravan's) has neither.
        internal sealed class MapFacts
        {
            private readonly Map? map;
            private Obs.RaidArmor? armor;
            internal readonly List<Pawn> Colonists;

            internal MapFacts(Map? map)
            {
                this.map = map;
                Colonists = map == null ? new List<Pawn>() : map.mapPawns.AllPawnsSpawned.Where(p => !p.Dead && p.IsColonist && p.Spawned).ToList();
            }

            internal Obs.RaidArmor RaidArmor => armor ??= map == null ? new Obs.RaidArmor.NoHostiles() : NativeGearFacts.RaidArmor(map);
        }

        // The one pawn row builder: every section, for every pawn. A pawn
        // the game gives no tracker for a section reads that section's
        // named state; anything else it cannot read throws.
        internal static Obs.PawnState Row(Pawn pawn, MapFacts facts, Common.ObservationContext context)
        {
            var player = Faction.OfPlayerSilentFail ?? throw new InvalidOperationException("Player faction missing.");
            var mental = pawn.MentalState;
            var lord = pawn.GetLord();
            var lordJob = lord?.LordJob;
            // A pawn without a job tracker (a corpse's) has no job.
            var job = pawn.jobs?.jobQueue != null
                ? NativeObservationTools.JobRow(pawn.CurJob, pawn.jobs.jobQueue.Count, NativePawnActivities.Standing(pawn))
                : NativeObservationTools.JobRow(null, 0, false);
            return new Obs.PawnState {
                Pawn = Entity(pawn),
                KindDefName = Id(pawn.kindDef?.defName),
                Colonist = pawn.IsColonist,
                FreeColonist = pawn.IsFreeColonist,
                Prisoner = pawn.IsPrisoner,
                Humanlike = pawn.RaceProps.Humanlike,
                Animal = pawn.RaceProps.Animal,
                Mechanoid = pawn.RaceProps.IsMechanoid,
                Dead = pawn.Dead,
                Downed = pawn.Downed,
                Drafted = pawn.drafter?.Drafted == true,
                Hostile = Hostile(pawn, player),
                MentalState = mental == null ? null : new Obs.PawnMentalState { DefName = Id(mental.def.defName), IsAggro = mental.def.IsAggro, Ticks = mental.Age },
                InBed = RestUtility.InBed(pawn),
                ActivityExecution = NativePawnActivities.Read(pawn),
                Job = job,
                Needs = NativePawnDetails.Needs(pawn),
                Health = NativePawnDetails.Health(pawn, context),
                Equipment = NativePawnDetails.Equipment(pawn),
                Biography = NativePawnDetails.Biography(pawn),
                Settings = NativePawnDetails.Settings(pawn),
                Social = NativePawnDetails.Social(pawn),
                AnimalState = pawn.RaceProps.Animal ? new Obs.PawnAnimalState.Animal(NativePawnDetails.Animal(pawn)) : new Obs.PawnAnimalState.NotAnimal(),
                NearestColonist = Nearest(pawn, facts.Colonists),
                OwnedBed = pawn.ownership?.OwnedBed is Building_Bed bed ? NativeRef.Of(Id(bed.GetUniqueLoadID())) : null,
                // Lord evidence is the game's own group-AI class names: a raid's job
                // (assault/siege/stage-then-attack) and its current toil (the sapper
                // and breach toils are distinct classes).
                LordJobClass = lordJob == null ? null : Id(lordJob.GetType().Name),
                LordToilClass = lordJob == null || lord!.CurLordToil == null ? null : Id(lord.CurLordToil.GetType().Name),
                RaidArmor = facts.RaidArmor,
                // Fog is the discovery fact, not a guess at reachability: a hostile
                // the colony has never seen is no emergency.
                Fogged = pawn.Spawned && pawn.Map != null && pawn.Position.Fogged(pawn.Map),
                // A pawn without a mind state (a corpse's) has no inspiration.
                Inspiration = pawn.mindState?.inspirationHandler?.CurStateDef is InspirationDef inspiration ? Id(inspiration.defName) : null,
                PawnSnapshot = NativePawnControlObservation.Read(pawn, context),
                Biotech = NativeBiotechFacts.State(pawn),
                Anomaly = NativeAnomalyFacts.State(pawn),
                Royalty = NativePawnRoyalty.State(pawn),
                FactionDefName = pawn.Faction == null ? null : Id(pawn.Faction.def.defName),
                ConstructionCapable = ConstructionSkillGuard.Capability(pawn),
            };
        }

        private static bool Hostile(Pawn pawn, Faction player)
        {
            var manhunter = pawn.MentalStateDef?.defName.IndexOf("Manhunter", StringComparison.OrdinalIgnoreCase) >= 0;
            return manhunter || pawn.Faction != null && pawn.Faction != player && pawn.Faction.HostileTo(player);
        }

        // Chebyshev distance to the nearest other live colonist.
        private static Obs.NearestColonist Nearest(Pawn pawn, List<Pawn> colonists)
        {
            long? nearest = null;
            foreach (var other in colonists) {
                if (other == pawn) continue;
                var d = Distance(other.Position, pawn.PositionHeld);
                if (nearest == null || d < nearest) nearest = d;
            }
            return nearest.HasValue ? new Obs.NearestColonist.At(nearest.Value) : new Obs.NearestColonist.None();
        }

        // A reference to a pawn's table row: every other message names a
        // pawn this way.
        internal static Common.Ref Ref(Pawn pawn) => new Common.Ref { Id = Id(pawn.GetUniqueLoadID()) };

        internal static bool Validate(Obs.ListPawnsRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Identity scope, unique exact IDs, finite nonnegative distance are required.");
            if (request?.Scope?.ExpectedIdentity == null) return false;
            var filter=request.Filter;
            if (filter == null) return true;
            return Ids(filter.Ids)
                && (filter.NameContains is null || filter.NameContains.IndexOf('\0')<0 && Encoding.UTF8.GetByteCount(filter.NameContains)<=256)
                && (filter.WithinColonistDistance is not Obs.ColonistRange.Within within || IsFinite(within.Distance) && within.Distance>=0);
        }

        private static bool ValidateTend(Obs.TendDoctorsRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Identity scope and 1..256 unique exact pawn IDs are required.");
            return request?.Scope?.ExpectedIdentity != null && request.PawnIds.Count > 0 && Ids(request.PawnIds);
        }

        private static bool Ids(IReadOnlyCollection<string> ids) =>
            ids.Count<=256 && ids.All(ProtoBoundary.IsIdentifier) && ids.Distinct(StringComparer.Ordinal).Count()==ids.Count;

        // Pure predicate over the native pawn: Excluded is an actual
        // exclusion, never an omitted filter.
        private static bool Matches(Pawn pawn, Obs.PawnFilter? f, MapFacts facts)
        {
            if (pawn.Dead && f?.IncludeDead != true) return false;
            if (f==null) return true;
            return (f.Ids.Count==0 || f.Ids.Contains(pawn.GetUniqueLoadID()))
                && (f.NameContains is null || Text(pawn.LabelCap).IndexOf(f.NameContains,StringComparison.OrdinalIgnoreCase)>=0)
                && Admits(f.Colonist, pawn.IsColonist) && Admits(f.Prisoner, pawn.IsPrisoner)
                && Admits(f.Animal, pawn.RaceProps.Animal) && Admits(f.Humanlike, pawn.RaceProps.Humanlike)
                && Admits(f.Mechanoid, pawn.RaceProps.IsMechanoid)
                && (f.Hostile == Obs.PawnMembership.Any || Admits(f.Hostile, Hostile(pawn, Faction.OfPlayerSilentFail ?? throw new InvalidOperationException("Player faction missing."))))
                && Admits(f.Downed, pawn.Downed) && Admits(f.Drafted, pawn.drafter?.Drafted == true)
                && (f.WithinColonistDistance is not Obs.ColonistRange.Within within || Nearest(pawn, facts.Colonists) is Obs.NearestColonist.At at && at.Distance<=within.Distance);
        }

        private static bool Admits(Obs.PawnMembership want, bool has) => want switch {
            Obs.PawnMembership.Any => true,
            Obs.PawnMembership.Only => has,
            Obs.PawnMembership.Excluded => !has,
            _ => throw new InvalidOperationException("Unknown pawn membership " + want),
        };

        private static long Distance(IntVec3 a,IntVec3 b)=>Math.Max(Math.Abs((long)a.x-b.x),Math.Abs((long)a.z-b.z));
        internal static Obs.EntityRef Entity(Thing thing) {
            // A held thing (gear, a buried corpse) is at its holder's cell.
            Obs.EntityPlace place = thing.MapHeld is { } map ? new Obs.EntityPlace.OnMap(map.uniqueID, Cell(thing.PositionHeld)) : new Obs.EntityPlace.Unplaced();
            return new Obs.EntityRef { Id=Id(thing.GetUniqueLoadID()),DefName=Id(thing.def.defName),Label=Text(thing.LabelCap),Place=place };
        }
        internal static Common.Cell Cell(IntVec3 value)=>new Common.Cell {X=value.x,Z=value.z};
        internal static string Id(string? value)=>ProtoBoundary.IsIdentifier(value)?value:throw new InvalidOperationException("Native ID unavailable.");
        internal static string Text(string? value) {
            if(value==null) throw new InvalidOperationException("Native pawn text unavailable.");
            if(value.Length>4096) throw new ReadLimit("Native pawn text exceeds the bounded field size.");
            return value;
        }
        internal static bool IsFinite(double value)=>!double.IsNaN(value)&&!double.IsInfinity(value);
        internal static double Number(double value)=>IsFinite(value)?value:throw new InvalidOperationException("Nonfinite native fact.");
        internal static Common.Unavailable Unavailable(Common.UnavailableReason reason,string detail)=>new Common.Unavailable {Reason=reason,Detail=detail};
        internal static Obs.ReadIssue Issue(string field,Common.UnavailableReason reason,string detail)=>new Obs.ReadIssue {Field=field,Unavailable=Unavailable(reason,detail)};
        internal sealed class ReadLimit:Exception { internal ReadLimit(string message):base(message) {} }
    }
}
