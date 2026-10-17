#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    internal static class NativePawnObservationTools
    {
        // On the game thread: the typed core of the tool, shared with the in-process facade.
        internal static Obs.ListPawnsReply HandleListPawns(Obs.ListPawnsRequest parsed)
        {
            if (!Validate(parsed, out var invalidRequest)) return new Obs.ListPawnsReply { Outcome = invalidRequest };
            if (!ProtoBoundary.ValidateIdentity(parsed.Scope?.ExpectedIdentity, out var map, out var context, out var invalid))
                return new Obs.ListPawnsReply { Outcome = invalid };
            try { return new Obs.ListPawnsReply { Outcome = Read(map, parsed, context) }; }
            catch (Exception error) { ModLog.Error("observe", "Pawn facts read failed: " + error); return new Obs.ListPawnsReply { Outcome = Unavailable(Common.UnavailableReason.ReadFailed,
                PlacementPreviewOperation.Diagnostic("Pawn facts could not be read completely: "+error)) }; }
        }

        // On the main thread.
        private static Obs.PawnSnapshot Read(Map map, Obs.ListPawnsRequest parsed, Common.ObservationContext context)
        {
            var source = map.mapPawns.AllPawnsSpawned.ToList();
            if ((parsed.Filter?.IncludeDead).GetValueOrDefault() == true)
                source.AddRange(map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).Cast<Corpse>().Select(c => c.InnerPawn));
            if (source.Any(p => p == null)) throw new InvalidOperationException("Null native pawn.");
            source = source.Distinct().ToList();
            var colonists = source.Where(p => !p.Dead && p.IsColonist && p.Spawned).ToList();
            // An id filter picks its pawns before any row is built: a frame asks
            // for the colonists by id, and the map holds every animal too.
            var wanted = parsed.Filter != null && parsed.Filter.Ids.Count > 0 ? new HashSet<string>(parsed.Filter.Ids, StringComparer.Ordinal) : null;
            var selected = new List<KeyValuePair<Pawn, Obs.PawnState>>();
            foreach (var pawn in source) {
                if (wanted != null && !wanted.Contains(pawn.GetUniqueLoadID())) continue;
                var row = Core(pawn, colonists, context);
                if (Matches(row, parsed.Filter)) selected.Add(new KeyValuePair<Pawn, Obs.PawnState>(pawn, row));
            }
            var ordered = selected.OrderBy(p => (p.Value.Pawn.Id ?? ""), StringComparer.Ordinal).ToList();
            var page = ordered;
            var result = new Obs.PawnSnapshot { Context = context, Completeness = Complete(page.Count, source.Count-selected.Count), MeditateAssignmentAvailable = DefDatabase<TimeAssignmentDef>.GetNamedSilentFail("Meditate") != null };
            var details = NativePawnDetails.Defaults(parsed.Details);
            var raidArmor = details.Equipment ? NativeGearFacts.RaidArmor(map) : null;
            // The tend detail is pairwise across the page, so it runs once over
            // the whole page before the per-row detail.
            if (details.Tend) page = NativePawnDetails.Tend(page, details.TendReachBudgetMs);
            foreach (var item in page) result = result with { Pawns = [.. result.Pawns, Detail(item.Key, item.Value, parsed.Details ?? new Obs.PawnDetails(), raidArmor, context)] };
            return result;
        }

        // The bundle's pawn table: every spawned pawn on the map,
        // each built by the one row builder with the detail its kind needs;
        // combat names the pawns the defense planner reads combat detail of.
        internal static Obs.PawnSnapshot Table(Map map, Common.ObservationContext context, ISet<string> combat)
        {
            var source = map.mapPawns.AllPawnsSpawned.ToList();
            if (source.Any(p => p == null)) throw new InvalidOperationException("Null native pawn.");
            var colonists = source.Where(p => !p.Dead && p.IsColonist && p.Spawned).ToList();
            var result = new Obs.PawnSnapshot { Context = context, Completeness = Complete(source.Count), MeditateAssignmentAvailable = DefDatabase<TimeAssignmentDef>.GetNamedSilentFail("Meditate") != null };
            var dependentPatients = NativeDependentPatients.Read(map).Select(p => p.Pawn).ToHashSet();
            double? raidArmor = null; var armorRead = false;
            using var surgery = new NativePawnDetails.SurgeryScope();
            foreach (var pawn in source.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal)) {
                var details = !pawn.Dead && pawn.IsFreeColonist ? ColonistDetail : combat.Contains(Id(pawn.GetUniqueLoadID())) ? CombatDetail
                    : dependentPatients.Contains(pawn) ? PatientDetail : pawn.RaceProps.Animal ? AnimalDetail : CoreDetail;
                if (details.Equipment.GetValueOrDefault() && !armorRead) { raidArmor = NativeGearFacts.RaidArmor(map); armorRead = true; }
                result = result with { Pawns = [.. result.Pawns, Detail(pawn, Core(pawn, colonists, context), details, raidArmor, context)] };
            }
            return result;
        }

        // The table's detail families: a free colonist every one but tend,
        // a combat pawn health, gear, biography and animal state, another
        // animal its animal state, any other pawn none.
        private static readonly Obs.PawnDetails ColonistDetail = new Obs.PawnDetails { Needs = true, Health = true, Equipment = true, Biography = true, Settings = true, Social = true, Animals = true, Work = true, Schedule = true, Tend = false };
        private static readonly Obs.PawnDetails CombatDetail = new Obs.PawnDetails { Needs = false, Health = true, Equipment = true, Biography = true, Settings = false, Social = false, Animals = true };
        private static readonly Obs.PawnDetails AnimalDetail = new Obs.PawnDetails { Needs = false, Health = false, Equipment = false, Biography = false, Settings = false, Social = false, Animals = true };
        private static readonly Obs.PawnDetails PatientDetail = new Obs.PawnDetails { Needs = false, Health = true, Equipment = false, Biography = false, Settings = false, Social = false, Animals = false };
        private static readonly Obs.PawnDetails CoreDetail = new Obs.PawnDetails { Needs = false, Health = false, Equipment = false, Biography = false, Settings = false, Social = false, Animals = false };

        // Detail is the one pawn row builder: the core row with the
        // requested detail families and the row's snapshot token.
        private static Obs.PawnState Detail(Pawn pawn, Obs.PawnState row, Obs.PawnDetails details, double? raidArmor, Common.ObservationContext context)
        {
            if (raidArmor.HasValue && NativePawnDetails.Defaults(details).Equipment) row = row with { RaidArmor = raidArmor.Value };
            row = NativePawnDetails.Apply(pawn, row, details, context);
            return row with { ActivityExecution = NativePawnActivities.Read(pawn) };
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
            return filter.Ids.Count<=256 && filter.Ids.All(ProtoBoundary.IsIdentifier)
                && filter.Ids.Distinct(StringComparer.Ordinal).Count()==filter.Ids.Count
                && (filter.NameContains is null || (filter.NameContains ?? "").IndexOf('\0')<0 && Encoding.UTF8.GetByteCount((filter.NameContains ?? ""))<=256)
                && (filter.WithinColonistDistance is null || IsFinite(filter.WithinColonistDistance.GetValueOrDefault()) && filter.WithinColonistDistance.GetValueOrDefault()>=0);
        }

        // Pure predicate: optional false is an actual exclusion, never an omitted filter.
        internal static bool Matches(Obs.PawnState row, Obs.PawnFilter? f)
        {
            if (row.Dead && (f?.IncludeDead).GetValueOrDefault() != true) return false;
            if (f==null) return true;
            return (f.Ids.Count==0 || f.Ids.Contains((row.Pawn.Id ?? "")))
                && (f.NameContains is null || (row.Pawn.Label ?? "").IndexOf((f.NameContains ?? ""),StringComparison.OrdinalIgnoreCase)>=0)
                && (f.Colonist is null || f.Colonist==row.Colonist) && (f.Prisoner is null || f.Prisoner==row.Prisoner)
                && (f.Animal is null || f.Animal==row.Animal) && (f.Humanlike is null || f.Humanlike==row.Humanlike)
                && (f.Mechanoid is null || f.Mechanoid==row.Mechanoid) && (f.Tame is null || f.Tame==row.Tame)
                && (f.Wild is null || f.Wild==row.Wild) && (f.Hostile is null || f.Hostile==row.Hostile)
                && (f.Downed is null || f.Downed==row.Downed) && (f.Drafted is null || f.Drafted==row.Drafted)
                && (f.WithinColonistDistance is null || row.NearestColonistDistance is not null && row.NearestColonistDistance.GetValueOrDefault()<=f.WithinColonistDistance.GetValueOrDefault());
        }

        internal static Obs.PawnState Core(Pawn pawn, List<Pawn> colonists, Common.ObservationContext context)
        {
            var row=NativeObservationTools.PawnRow(pawn,context);
            row = row with { Pawn = row.Pawn with { Label = Text(pawn.LabelCap) } };
            if (pawn.MapHeld!=null) { row = row with { Pawn = row.Pawn with { MapId = pawn.MapHeld.uniqueID, Position = Cell(pawn.PositionHeld) } }; }
            Pawn? nearest=null; long nearestDistance=0;
            foreach(var other in colonists) {
                if(other==pawn) continue;
                var d=Distance(other.Position,pawn.PositionHeld);
                if(nearest==null || d<nearestDistance
                    || d==nearestDistance && string.CompareOrdinal(other.GetUniqueLoadID(),nearest.GetUniqueLoadID())<0) { nearest=other; nearestDistance=d; }
            }
            if (nearest!=null) row = row with { NearestColonistDistance = nearestDistance };
            // Faction standing of a humanlike pawn, colonist or not.
            if(pawn.RaceProps.Humanlike) row = row with { Standing = new Obs.PawnStanding { FactionDefName = pawn.Faction==null ? null : Id(pawn.Faction.def.defName) } };
            if (pawn.ownership?.OwnedBed!=null) row = row with { OwnedBed = NativeRef.Of(Id(pawn.ownership.OwnedBed.GetUniqueLoadID())) };
            else row = row with { Issues = [.. row.Issues, Issue("owned_bed",Common.UnavailableReason.NotApplicable,"No owned bed.")] };
            return row;
        }
        internal static Obs.SnapshotRef PawnSnapshotToken(Obs.PawnState row,Common.ObservationContext context)
            => NativeObservationSnapshot.Snapshot("pawn-state", context, (row.Pawn.Id ?? ""), w => {
                w.Write(row.Dead); w.Write(row.Downed); w.Write(row.Drafted); w.Write(row.InBed); w.Write(row.Hostile);
                w.Write(row.MentalState?.DefName??""); w.Write(row.Faction?.Id??"");
            });
        private static long Distance(IntVec3 a,IntVec3 b)=>Math.Max(Math.Abs((long)a.x-b.x),Math.Abs((long)a.z-b.z));
        internal static Obs.EntityRef Entity(Thing thing) {
            var row=new Obs.EntityRef { Id=Id(thing.GetUniqueLoadID()),DefName=Id(thing.def.defName),Label=Text(thing.LabelCap) };
            if(thing.MapHeld!=null) { row = row with { MapId = thing.MapHeld.uniqueID };row = row with { Position = Cell(thing.PositionHeld) }; } return row;
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
        internal static Obs.Completeness Complete(int count,int filtered=0)=>new Obs.Completeness { Filtered = (ulong)filtered };
        internal sealed class ReadLimit:Exception { internal ReadLimit(string message):base(message) {} }
    }
}
