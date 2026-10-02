#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

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
            if (!ProtoBoundary.TryParse(ctx, "rimgovernor/observations_list_pawns", request!, Obs.ListPawnsRequest.Parser, out var parsed, out var failure)
                || !Validate(parsed, out failure)) return ProtoBoundary.Encode(new Obs.ListPawnsReply { Failure = failure });
            // Read on the game thread; delta (#773) and format on an encoder
            // worker (#644), since the delta digests the whole reply.
            var lease = await ReplyEncoder.Reserve(cancellationToken).ConfigureAwait(false);
            if (lease == null) return ProtoBoundary.Encode(new Obs.ListPawnsReply { Failure = ProtoBoundary.Fail(Common.FailureCode.CapacityExhausted,
                "Pawn reply encoders stayed saturated for " + ReplyEncoder.ReserveTimeoutMs + " ms; nothing was read.") });
            try
            {
                var captured = await ProtoBoundary.CaptureOnMainThread(ctx, () => {
                    if (!ProtoBoundary.ValidateIdentity(parsed.Scope?.ExpectedIdentity, out var map, out var context, out var invalid))
                        return new Obs.ListPawnsReply { Failure = invalid };
                    try { return new Obs.ListPawnsReply { Observed = Read(map, parsed, context) }; }
                    catch (Exception error) { return new Obs.ListPawnsReply { Unavailable = Unavailable(Common.UnavailableReason.ReadFailed,
                        PlacementPreviewOperation.Diagnostic("Pawn facts could not be read completely: "+error)) }; }
                }, cancellationToken).ConfigureAwait(false);
                var owned = lease; lease = null;
                return await ProtoBoundary.EncodeDetached(captured, owned, reply => EncodePawns(reply, parsed), cancellationToken).ConfigureAwait(false);
            }
            finally { lease?.Dispose(); }
        }

        private static Dictionary<string, object?> EncodePawns(Obs.ListPawnsReply reply, Obs.ListPawnsRequest parsed)
        {
            if (reply.Observed == null) return ProtoBoundary.Encode(reply);
            return ProtoBoundary.Encode(reply);
        }

        // The pawn list as a frame section (issue #180): the same rows the
        // tool answers, or false for any read failure the frame then omits.
        internal static bool TryRead(Map map, Obs.ListPawnsRequest request, Common.ObservationContext context, [NotNullWhen(true)] out Obs.PawnSnapshot? snapshot)
        {
            snapshot = null;
            try { snapshot = Read(map, request, context); return true; }
            catch (Exception) { return false; }
        }

        // On the main thread.
        private static Obs.PawnSnapshot Read(Map map, Obs.ListPawnsRequest parsed, Common.ObservationContext context)
        {
            var source = map.mapPawns.AllPawnsSpawned.ToList();
            if (parsed.Filter?.IncludeDead == true)
                source.AddRange(map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).Cast<Corpse>().Select(c => c.InnerPawn));
            if (source.Any(p => p == null)) throw new InvalidOperationException("Null native pawn.");
            source = source.Distinct().ToList();
            var colonists = source.Where(p => !p.Dead && p.IsColonist && p.Spawned).ToList();
            // An id filter picks its pawns before any row is built: a frame asks
            // for the colonists by id, and the map holds every animal too (#858).
            var wanted = parsed.Filter != null && parsed.Filter.Ids.Count > 0 ? new HashSet<string>(parsed.Filter.Ids, StringComparer.Ordinal) : null;
            var selected = new List<KeyValuePair<Pawn, Obs.PawnState>>();
            foreach (var pawn in source) {
                if (wanted != null && !wanted.Contains(pawn.GetUniqueLoadID())) continue;
                var row = Core(pawn, colonists, context);
                if (Matches(row, parsed.Filter)) selected.Add(new KeyValuePair<Pawn, Obs.PawnState>(pawn, row));
            }
            var ordered = selected.OrderBy(p => p.Value.Pawn.Id, StringComparer.Ordinal).ToList();
            var page = ordered;
            var result = new Obs.PawnSnapshot { Context = context, Completeness = Complete(page.Count, source.Count-selected.Count), MeditateAssignmentAvailable = DefDatabase<TimeAssignmentDef>.GetNamedSilentFail("Meditate") != null };
            var details = NativePawnDetails.Defaults(parsed.Details);
            var raidArmor = details.Equipment ? NativeGearFacts.RaidArmor(map) : null;
            // The tend detail is pairwise across the page, so it runs once over
            // the whole page before the per-row snapshot tokens are taken.
            if (details.Tend) NativePawnDetails.Tend(page);
            foreach (var item in page) result.Pawns.Add(Detail(item.Key, colonists, item.Value, parsed.Details, raidArmor, context));
            return result;
        }

        // The bundle's pawn table (#1343): every spawned pawn on the map,
        // each built by the one row builder with the detail its kind needs;
        // combat names the pawns the defense planner reads combat detail of.
        internal static Obs.PawnSnapshot Table(Map map, Common.ObservationContext context, ISet<string> combat)
        {
            var source = map.mapPawns.AllPawnsSpawned.ToList();
            if (source.Any(p => p == null)) throw new InvalidOperationException("Null native pawn.");
            var colonists = source.Where(p => !p.Dead && p.IsColonist && p.Spawned).ToList();
            var result = new Obs.PawnSnapshot { Context = context, Completeness = Complete(source.Count), MeditateAssignmentAvailable = DefDatabase<TimeAssignmentDef>.GetNamedSilentFail("Meditate") != null };
            double? raidArmor = null; var armorRead = false;
            foreach (var pawn in source.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal)) {
                var details = !pawn.Dead && pawn.IsFreeColonist ? ColonistDetail : combat.Contains(Id(pawn.GetUniqueLoadID())) ? CombatDetail
                    : pawn.RaceProps.Animal ? AnimalDetail : CoreDetail;
                if (details.Equipment && !armorRead) { raidArmor = NativeGearFacts.RaidArmor(map); armorRead = true; }
                result.Pawns.Add(Detail(pawn, colonists, Core(pawn, colonists, context), details, raidArmor, context));
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

        // Detail is the one pawn row builder (#1343): the core row with the
        // requested detail families and the row's snapshot token.
        private static Obs.PawnState Detail(Pawn pawn, List<Pawn> colonists, Obs.PawnState row, Obs.PawnDetails details, double? raidArmor, Common.ObservationContext context)
        {
            if (raidArmor.HasValue && NativePawnDetails.Defaults(details).Equipment) row.RaidArmor = raidArmor.Value;
            NativePawnDetails.Apply(pawn, colonists, row, details, context);
            row.Snapshot = PawnSnapshotToken(pawn, row, context);
            return row;
        }

        // A reference to a pawn's table row: every other message names a
        // pawn this way (#1343).
        internal static Obs.EntityRef Ref(Pawn pawn) => new Obs.EntityRef { Id = Id(pawn.GetUniqueLoadID()) };

        internal static bool Validate(Obs.ListPawnsRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Identity scope, unique exact IDs, finite nonnegative distance are required.");
            if (request?.Scope?.ExpectedIdentity == null) return false;
            var filter=request.Filter;
            if (filter == null) return true;
            return filter.Ids.Count<=256 && filter.Ids.All(ProtoBoundary.IsIdentifier)
                && filter.Ids.Distinct(StringComparer.Ordinal).Count()==filter.Ids.Count
                && (!filter.HasNameContains || filter.NameContains.IndexOf('\0')<0 && Encoding.UTF8.GetByteCount(filter.NameContains)<=256)
                && (!filter.HasWithinColonistDistance || IsFinite(filter.WithinColonistDistance) && filter.WithinColonistDistance>=0);
        }

        // Pure predicate: optional false is an actual exclusion, never an omitted filter.
        internal static bool Matches(Obs.PawnState row, Obs.PawnFilter? f)
        {
            if (row.Dead && f?.IncludeDead != true) return false;
            if (f==null) return true;
            return (f.Ids.Count==0 || f.Ids.Contains(row.Pawn.Id))
                && (!f.HasNameContains || row.Pawn.Label.IndexOf(f.NameContains,StringComparison.OrdinalIgnoreCase)>=0)
                && (!f.HasColonist || f.Colonist==row.Colonist) && (!f.HasPrisoner || f.Prisoner==row.Prisoner)
                && (!f.HasAnimal || f.Animal==row.Animal) && (!f.HasHumanlike || f.Humanlike==row.Humanlike)
                && (!f.HasMechanoid || f.Mechanoid==row.Mechanoid) && (!f.HasTame || f.Tame==row.Tame)
                && (!f.HasWild || f.Wild==row.Wild) && (!f.HasHostile || f.Hostile==row.Hostile)
                && (!f.HasDowned || f.Downed==row.Downed) && (!f.HasDrafted || f.Drafted==row.Drafted)
                && (!f.HasWithinColonistDistance || row.HasNearestColonistDistance && row.NearestColonistDistance<=f.WithinColonistDistance);
        }

        internal static Obs.PawnState Core(Pawn pawn, List<Pawn> colonists, Common.ObservationContext context)
        {
            var row=NativeObservationTools.PawnRow(pawn,false,context);
            row.Pawn.Label=Text(pawn.LabelCap);
            var player=Faction.OfPlayerSilentFail ?? throw new InvalidOperationException("Player faction missing.");
            row.Tame=pawn.RaceProps.Animal && pawn.Faction==player;
            row.Wild=pawn.RaceProps.Animal && pawn.Faction==null;
            row.Burning=pawn.IsBurning(); row.PlayerControlled=pawn.IsColonistPlayerControlled;
            var mental=pawn.MentalStateDef?.defName;
            var manhunter=mental?.IndexOf("Manhunter",StringComparison.OrdinalIgnoreCase)>=0;
            row.Hostile=manhunter || pawn.Faction!=null && pawn.Faction!=player && pawn.Faction.HostileTo(player);
            row.HostileReason=manhunter ? "manhunter:"+mental : row.Hostile ? "faction:"+Id(pawn.Faction!.GetUniqueLoadID()) : "none";
            if (pawn.MapHeld!=null) { row.Pawn.MapId=pawn.MapHeld.uniqueID; row.Pawn.Position=Cell(pawn.PositionHeld); }
            var nearest=colonists.Where(p => p!=pawn).OrderBy(p => Distance(p.Position,pawn.PositionHeld)).ThenBy(p => p.GetUniqueLoadID(),StringComparer.Ordinal).FirstOrDefault();
            if (nearest!=null) { row.NearestColonist=Entity(nearest); row.NearestColonistDistance=Distance(nearest.Position,pawn.PositionHeld); }
            else {
                row.Issues.Add(Issue("nearest_colonist",Common.UnavailableReason.NotApplicable,"No other live colonist on this map."));
                row.Issues.Add(Issue("nearest_colonist_distance",Common.UnavailableReason.NotApplicable,"No other live colonist on this map."));
            }
            if(pawn.Faction==null) row.Issues.Add(Issue("faction_id",Common.UnavailableReason.NotApplicable,"Pawn has no faction."));
            if(pawn.MentalStateDef==null) row.Issues.Add(Issue("mental_state",Common.UnavailableReason.NotApplicable,"Pawn has no mental state."));
            if (pawn.ownership?.OwnedBed!=null) row.OwnedBedId=Id(pawn.ownership.OwnedBed.GetUniqueLoadID());
            else row.Issues.Add(Issue("owned_bed_id",Common.UnavailableReason.NotApplicable,"No owned bed."));
            if (row.Job!=null && pawn.CurJob!=null) {
                var job=pawn.CurJob; var target=job.targetA;
                if (target.HasThing) row.Job.TargetA=new Obs.TargetRef { Entity=Entity(target.Thing) };
                else if (target.IsValid) row.Job.TargetA=new Obs.TargetRef { Cell=Cell(target.Cell) };
                else row.Job.TargetA=new Obs.TargetRef { Unavailable=Unavailable(Common.UnavailableReason.NotApplicable,"Job has no target A.") };
                row.Job.Issues.Add(Issue("order_generation",Common.UnavailableReason.Unsupported,"Order generation is not observed by this reader."));
                foreach(var field in new[]{"report","interruptible","native_priority"})
                    row.Job.Issues.Add(Issue(field,Common.UnavailableReason.Unsupported,"Job driver detail is not observed by this reader."));
            }
            return row;
        }
        internal static Obs.SnapshotRef PawnSnapshotToken(Pawn pawn,Obs.PawnState row,Common.ObservationContext context)
            => NativeObservationSnapshot.Snapshot("pawn-state", context, row.Pawn.Id, w => {
                w.Write(row.Dead); w.Write(row.Downed); w.Write(row.Drafted); w.Write(row.InBed); w.Write(row.Hostile);
                w.Write(row.MentalState??""); w.Write(row.HostileReason??""); w.Write(row.FactionId??"");
            });
        private static long Distance(IntVec3 a,IntVec3 b)=>Math.Max(Math.Abs((long)a.x-b.x),Math.Abs((long)a.z-b.z));
        internal static Obs.EntityRef Entity(Thing thing) {
            var row=new Obs.EntityRef { Id=Id(thing.GetUniqueLoadID()),DefName=Id(thing.def.defName),Label=Text(thing.LabelCap) };
            if(thing.MapHeld!=null) { row.MapId=thing.MapHeld.uniqueID;row.Position=Cell(thing.PositionHeld); } return row;
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
