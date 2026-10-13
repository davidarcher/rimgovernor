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
    public sealed class NativePawnObservationTools
    {
        [Tool("rimgovernor/observations_list_pawns", Title = "Read map pawns",
            Description = "Official ListPawnsRequest ProtoJSON. Current map spawned pawns; includeDead also includes inner pawns of spawned corpses. Filters intersect, exact IDs, case-insensitive label substring, Chebyshev distance to another live colonist. All detail families default requested; unsupported fields carry issues. The list is complete.")]
        [ToolResponse("payload", "string", "Official observations ListPawnsReply ProtoJSON.", Always = true)]
        public async Task<object> ListPawns(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Raw value must be a ListPawnsRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse<Obs.ListPawnsRequest>(ctx, "rimgovernor/observations_list_pawns", request!, out var parsed, out var failure))
                return ProtoBoundary.Encode(new Obs.ListPawnsReply { Outcome = new Obs.ListPawnsReplyOutcome.FailureCase(failure) });
            // Read on the game thread; delta and format on an encoder
            // worker, since the delta digests the whole reply.
            var lease = await ReplyEncoder.Reserve(cancellationToken).ConfigureAwait(false);
            if (lease == null) return ProtoBoundary.Encode(new Obs.ListPawnsReply { Outcome = ProtoBoundary.Fail(Common.FailureCode.CapacityExhausted,
                "Pawn reply encoders stayed saturated for " + ReplyEncoder.ReserveTimeoutMs + " ms; nothing was read.") });
            try
            {
                var captured = await ProtoBoundary.CaptureOnMainThread(ctx, () => HandleListPawns(parsed), cancellationToken).ConfigureAwait(false);
                var owned = lease; lease = null;
                return await ProtoBoundary.EncodeDetached(captured, owned, reply => EncodePawns(reply, parsed), cancellationToken).ConfigureAwait(false);
            }
            finally { lease?.Dispose(); }
        }

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

        private static Dictionary<string, object?> EncodePawns(Obs.ListPawnsReply reply, Obs.ListPawnsRequest parsed)
        {
            if (reply.Outcome?.Observed == null) return ProtoBoundary.Encode(reply);
            return ProtoBoundary.Encode(reply);
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
            var ordered = selected.OrderBy(p => (p.Value.Pawn?.Id ?? ""), StringComparer.Ordinal).ToList();
            var page = ordered;
            var result = new Obs.PawnSnapshot { Context = context, Completeness = Complete(page.Count, source.Count-selected.Count), MeditateAssignmentAvailable = DefDatabase<TimeAssignmentDef>.GetNamedSilentFail("Meditate") != null };
            var details = NativePawnDetails.Defaults(parsed.Details);
            var raidArmor = details.Equipment.GetValueOrDefault() ? NativeGearFacts.RaidArmor(map) : null;
            // The tend detail is pairwise across the page, so it runs once over
            // the whole page before the per-row snapshot tokens are taken.
            if (details.Tend.GetValueOrDefault()) page = NativePawnDetails.Tend(page, details.TendReachBudgetMs is not null ? details.TendReachBudgetMs.GetValueOrDefault() : (uint?)null);
            foreach (var item in page) result = result with { Pawns = [.. result.Pawns, Detail(item.Key, colonists, item.Value, parsed.Details ?? new Obs.PawnDetails(), raidArmor, context)] };
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
            double? raidArmor = null; var armorRead = false;
            using var surgery = new NativePawnDetails.SurgeryScope();
            foreach (var pawn in source.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal)) {
                var details = !pawn.Dead && pawn.IsFreeColonist ? ColonistDetail : combat.Contains(Id(pawn.GetUniqueLoadID())) ? CombatDetail
                    : pawn.RaceProps.Animal ? AnimalDetail : CoreDetail;
                if (details.Equipment.GetValueOrDefault() && !armorRead) { raidArmor = NativeGearFacts.RaidArmor(map); armorRead = true; }
                result = result with { Pawns = [.. result.Pawns, Detail(pawn, colonists, Core(pawn, colonists, context), details, raidArmor, context)] };
            }
            return result;
        }

        // The table's detail families: a free colonist every one but tend,
        // a combat pawn health, gear, biography and animal state, another
        // animal its animal state, any other pawn none.
        private static readonly Obs.PawnDetails ColonistDetail = new Obs.PawnDetails { Needs = true, Health = true, Equipment = true, Biography = true, Settings = true, Social = true, Animals = true, Work = true, Schedule = true, Tend = false };
        private static readonly Obs.PawnDetails CombatDetail = new Obs.PawnDetails { Needs = false, Health = true, Equipment = true, Biography = true, Settings = false, Social = false, Animals = true };
        private static readonly Obs.PawnDetails AnimalDetail = new Obs.PawnDetails { Needs = false, Health = false, Equipment = false, Biography = false, Settings = false, Social = false, Animals = true };
        private static readonly Obs.PawnDetails CoreDetail = new Obs.PawnDetails { Needs = false, Health = false, Equipment = false, Biography = false, Settings = false, Social = false, Animals = false };

        // Detail is the one pawn row builder: the core row with the
        // requested detail families and the row's snapshot token.
        private static Obs.PawnState Detail(Pawn pawn, List<Pawn> colonists, Obs.PawnState row, Obs.PawnDetails details, double? raidArmor, Common.ObservationContext context)
        {
            if (raidArmor.HasValue && NativePawnDetails.Defaults(details).Equipment.GetValueOrDefault()) row = row with { RaidArmor = raidArmor.Value };
            row = NativePawnDetails.Apply(pawn, colonists, row, details, context);
            row = row with { Snapshot = PawnSnapshotToken(pawn, row, context) };
            return row;
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
            if (row.Dead.GetValueOrDefault() && (f?.IncludeDead).GetValueOrDefault() != true) return false;
            if (f==null) return true;
            return (f.Ids.Count==0 || f.Ids.Contains((row.Pawn?.Id ?? "")))
                && (f.NameContains is null || (row.Pawn?.Label ?? "").IndexOf((f.NameContains ?? ""),StringComparison.OrdinalIgnoreCase)>=0)
                && (f.Colonist is null || f.Colonist.GetValueOrDefault()==row.Colonist.GetValueOrDefault()) && (f.Prisoner is null || f.Prisoner.GetValueOrDefault()==row.Prisoner.GetValueOrDefault())
                && (f.Animal is null || f.Animal.GetValueOrDefault()==row.Animal.GetValueOrDefault()) && (f.Humanlike is null || f.Humanlike.GetValueOrDefault()==row.Humanlike.GetValueOrDefault())
                && (f.Mechanoid is null || f.Mechanoid.GetValueOrDefault()==row.Mechanoid.GetValueOrDefault()) && (f.Tame is null || f.Tame.GetValueOrDefault()==row.Tame.GetValueOrDefault())
                && (f.Wild is null || f.Wild.GetValueOrDefault()==row.Wild.GetValueOrDefault()) && (f.Hostile is null || f.Hostile.GetValueOrDefault()==row.Hostile.GetValueOrDefault())
                && (f.Downed is null || f.Downed.GetValueOrDefault()==row.Downed.GetValueOrDefault()) && (f.Drafted is null || f.Drafted.GetValueOrDefault()==row.Drafted.GetValueOrDefault())
                && (f.WithinColonistDistance is null || row.NearestColonistDistance is not null && row.NearestColonistDistance.GetValueOrDefault()<=f.WithinColonistDistance.GetValueOrDefault());
        }

        internal static Obs.PawnState Core(Pawn pawn, List<Pawn> colonists, Common.ObservationContext context)
        {
            var row=NativeObservationTools.PawnRow(pawn,false,context);
            row = row with { Pawn = (row.Pawn ?? new Obs.EntityRef()) with { Label = Text(pawn.LabelCap) } };
            var player=Faction.OfPlayerSilentFail ?? throw new InvalidOperationException("Player faction missing.");
            row = row with { Tame = pawn.RaceProps.Animal && pawn.Faction==player };
            row = row with { Wild = pawn.RaceProps.Animal && pawn.Faction==null };
            row = row with { Burning = pawn.IsBurning() }; row = row with { PlayerControlled = pawn.IsColonistPlayerControlled };
            var mental=pawn.MentalStateDef?.defName;
            var manhunter=mental?.IndexOf("Manhunter",StringComparison.OrdinalIgnoreCase)>=0;
            row = row with { Hostile = manhunter || pawn.Faction!=null && pawn.Faction!=player && pawn.Faction.HostileTo(player) };
            row = row with { HostileReason = manhunter ? "manhunter:"+mental : row.Hostile.GetValueOrDefault() ? "faction:"+Id(pawn.Faction!.GetUniqueLoadID()) : "none" };
            if (pawn.MapHeld!=null) { row = row with { Pawn = (row.Pawn ?? new Obs.EntityRef()) with { MapId = pawn.MapHeld.uniqueID, Position = Cell(pawn.PositionHeld) } }; }
            Pawn? nearest=null; long nearestDistance=0;
            foreach(var other in colonists) {
                if(other==pawn) continue;
                var d=Distance(other.Position,pawn.PositionHeld);
                if(nearest==null || d<nearestDistance
                    || d==nearestDistance && string.CompareOrdinal(other.GetUniqueLoadID(),nearest.GetUniqueLoadID())<0) { nearest=other; nearestDistance=d; }
            }
            if (nearest!=null) { row = row with { NearestColonist = NativeRef.Thing(nearest) }; row = row with { NearestColonistDistance = nearestDistance }; }
            else {
                row = row with { Issues = [.. row.Issues, Issue("nearest_colonist",Common.UnavailableReason.NotApplicable,"No other live colonist on this map.")] };
                row = row with { Issues = [.. row.Issues, Issue("nearest_colonist_distance",Common.UnavailableReason.NotApplicable,"No other live colonist on this map.")] };
            }
            if(pawn.RaceProps.Humanlike) row = row with { Standing = Standing(pawn) };
            if(pawn.Faction==null) row = row with { Issues = [.. row.Issues, Issue("faction",Common.UnavailableReason.NotApplicable,"Pawn has no faction.")] };
            if(pawn.MentalStateDef==null) row = row with { Issues = [.. row.Issues, Issue("mental_state",Common.UnavailableReason.NotApplicable,"Pawn has no mental state.")] };
            if (pawn.ownership?.OwnedBed!=null) row = row with { OwnedBed = NativeRef.Of(Id(pawn.ownership.OwnedBed.GetUniqueLoadID())) };
            else row = row with { Issues = [.. row.Issues, Issue("owned_bed",Common.UnavailableReason.NotApplicable,"No owned bed.")] };
            if (row.Job is { } jobRow && pawn.CurJob!=null) {
                var job=pawn.CurJob; var target=job.targetA;
                if (target.HasThing) { jobRow = jobRow with { TargetA = new Obs.TargetRef { Target=NativeRef.Thing(target.Thing) } }; if (target.Thing.Spawned) jobRow = jobRow with { TargetACell = Cell(target.Thing.Position) }; }
                else if (target.IsValid) jobRow = jobRow with { TargetA = new Obs.TargetRef { Target=Cell(target.Cell) } };
                else jobRow = jobRow with { TargetA = new Obs.TargetRef { Target=Unavailable(Common.UnavailableReason.NotApplicable,"Job has no target A.") } };
                var jobIssues = new List<Obs.ReadIssue>(jobRow.Issues) { Issue("order_generation",Common.UnavailableReason.Unsupported,"Order generation is not observed by this reader.") };
                foreach(var field in new[]{"report","interruptible","native_priority"})
                    jobIssues.Add(Issue(field,Common.UnavailableReason.Unsupported,"Job driver detail is not observed by this reader."));
                row = row with { Job = jobRow with { Issues = [.. jobIssues] } };
            }
            return row;
        }
        // Faction standing of a humanlike pawn, colonist or not.
        private static Obs.PawnStanding Standing(Pawn pawn)
        {
            var standing=new Obs.PawnStanding { QuestLodger=pawn.IsQuestLodger() };
            if(pawn.Faction!=null) standing = standing with { FactionDefName = Id(pawn.Faction.def.defName) };
            if(ModsConfig.RoyaltyActive && pawn.royalty?.MostSeniorTitle is RoyalTitle title) standing = standing with { RoyalTitle = Id(title.def.defName) };
            if(pawn.HostFaction!=null) {
                standing = standing with { HostFaction = NativeRef.Of(Id(pawn.HostFaction.GetUniqueLoadID())) };
                if(pawn.guest!=null) standing = standing with { GuestStatus = pawn.guest.GuestStatus.ToString() };
            }
            return standing;
        }
        internal static Obs.SnapshotRef PawnSnapshotToken(Pawn pawn,Obs.PawnState row,Common.ObservationContext context)
            => NativeObservationSnapshot.Snapshot("pawn-state", context, (row.Pawn?.Id ?? ""), w => {
                w.Write(row.Dead.GetValueOrDefault()); w.Write(row.Downed.GetValueOrDefault()); w.Write(row.Drafted.GetValueOrDefault()); w.Write(row.InBed.GetValueOrDefault()); w.Write(row.Hostile.GetValueOrDefault());
                w.Write(row.MentalState??""); w.Write(row.HostileReason??""); w.Write(row.Faction?.Id??"");
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
