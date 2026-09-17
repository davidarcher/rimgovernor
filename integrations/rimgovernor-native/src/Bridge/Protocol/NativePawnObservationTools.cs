#nullable enable
using System;
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
            Description = "Official ListPawnsRequest ProtoJSON. Current map spawned pawns; includeDead also includes inner pawns of spawned corpses. Filters intersect, exact IDs, case-insensitive label substring, Chebyshev distance to another live colonist. All detail families default requested; unsupported fields carry issues. Page limit1..256 is a whole-query bound, no cursors or truncation.")]
        [ToolResponse("payload", "string", "Official observations ListPawnsReply ProtoJSON.", Always = true)]
        public async Task<object> ListPawns(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Raw value must be a ListPawnsRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, "rimgovernor/observations_list_pawns", request!, Obs.ListPawnsRequest.Parser, out var parsed, out var failure)
                || !Validate(parsed, out failure)) return ProtoBoundary.Encode(new Obs.ListPawnsReply { Failure = failure });
            return await ProtoBoundary.OnMainThread(ctx, () => {
                var map = ProtoBoundary.ResolveMap(parsed.Scope?.ExpectedIdentity!);
                if (!ProtoBoundary.ValidateIdentity(parsed.Scope?.ExpectedIdentity!, map, out var context, out failure))
                    return ProtoBoundary.Encode(new Obs.ListPawnsReply { Failure = failure });
                try {
                    var source = map.mapPawns.AllPawnsSpawned.ToList();
                    if (parsed.Filter?.IncludeDead == true)
                        source.AddRange(map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).Cast<Corpse>().Select(c => c.InnerPawn));
                    if (source.Any(p => p == null)) throw new InvalidOperationException("Null native pawn.");
                    source = source.Distinct().ToList();
                    var colonists = source.Where(p => !p.Dead && p.IsColonist && p.Spawned).ToList();
                    var selected = new List<KeyValuePair<Pawn, Obs.PawnState>>();
                    foreach (var pawn in source) {
                        var row = Core(pawn, colonists, context);
                        if (Matches(row, parsed.Filter)) selected.Add(new KeyValuePair<Pawn, Obs.PawnState>(pawn, row));
                    }
                    var ordered = selected.OrderBy(p => p.Value.Pawn.Id, StringComparer.Ordinal).ToList();
                    var seed = QuerySeed(parsed.Filter);
                    var afterCursor = ordered;
                    if (parsed.Page != null && parsed.Page.HasCursor && parsed.Page.Cursor.Length != 0) {
                        if (!NativeObservationSnapshot.Cursor.TryDecode(context.Identity, seed, parsed.Page.Cursor, out var after))
                            return ProtoBoundary.Encode(new Obs.ListPawnsReply { Unavailable = Unavailable(Common.UnavailableReason.LimitExceeded, "Pawn cursor is stale or does not match this query.") });
                        afterCursor = ordered.Where(p => string.CompareOrdinal(p.Value.Pawn.Id, after) > 0).ToList();
                    }
                    var limit = parsed.Page?.HasLimit == true ? (int)parsed.Page.Limit : 256;
                    var page = afterCursor.Take(limit).ToList();
                    Require(page.Count, 256);
                    var truncated = afterCursor.Count > page.Count;
                    var result = new Obs.PawnSnapshot { Context = context, Completeness = Complete(page.Count, source.Count-selected.Count) };
                    result.Completeness.Page.Complete = !truncated;
                    if (truncated) result.Completeness.Page.NextCursor = NativeObservationSnapshot.Cursor.Encode(context.Identity, seed, page[page.Count-1].Value.Pawn.Id);
                    foreach (var item in page) {
                        NativePawnDetails.Apply(item.Key, colonists, item.Value, parsed.Details, context);
                        if (item.Value.Settings != null) {
                            item.Value.Settings.Snapshot = NativeWorkSettings.Snapshot(item.Key, context);
                            if (item.Value.Settings.Snapshot != null)
                                foreach (var issue in item.Value.Settings.Issues.Where(i => i.Field == "snapshot").ToArray()) item.Value.Settings.Issues.Remove(issue);
                        }
                        item.Value.Snapshot = PawnSnapshotToken(item.Key, item.Value, context);
                        result.Pawns.Add(item.Value);
                    }
                    return Encode(new Obs.ListPawnsReply { Observed = result });
                }
                catch (ReadLimit error) { return ProtoBoundary.Encode(new Obs.ListPawnsReply { Unavailable = Unavailable(Common.UnavailableReason.LimitExceeded, error.Message) }); }
                catch (Exception error) { return ProtoBoundary.Encode(new Obs.ListPawnsReply { Unavailable = Unavailable(Common.UnavailableReason.ReadFailed,
                    PlacementPreviewOperation.Diagnostic("Pawn facts could not be read completely: "+error)) }); }
            }, cancellationToken).ConfigureAwait(false);
        }

        internal static bool Validate(Obs.ListPawnsRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Identity scope, unique exact IDs, finite nonnegative distance and page limit1..256 without cursor are required.");
            if (request?.Scope?.ExpectedIdentity == null) return false;
            var page=request.Page; var filter=request.Filter;
            if (page != null && (page.HasLimit && (page.Limit<1 || page.Limit>256) || page.HasCursor && page.Cursor.Length>4096)) return false;
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
        private static string QuerySeed(Obs.PawnFilter? f) => f==null ? "" : string.Join("",
            f.IncludeDead, f.Colonist, f.Prisoner, f.Animal, f.Humanlike, f.Mechanoid, f.Tame, f.Wild, f.Hostile, f.Downed, f.Drafted,
            f.NameContains??"", f.WithinColonistDistance,
            string.Join(",", f.Ids.OrderBy(i=>i,StringComparer.Ordinal)));
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
        internal static string Id(string value)=>ProtoBoundary.IsIdentifier(value)?value:throw new InvalidOperationException("Native ID unavailable.");
        internal static string Text(string value) {
            if(value.Length>4096) throw new ReadLimit("Native pawn text exceeds the bounded field size.");
            return value;
        }
        internal static bool IsFinite(double value)=>!double.IsNaN(value)&&!double.IsInfinity(value);
        internal static double Number(double value)=>IsFinite(value)?value:throw new InvalidOperationException("Nonfinite native fact.");
        internal static Common.Unavailable Unavailable(Common.UnavailableReason reason,string detail)=>new Common.Unavailable {Reason=reason,Detail=detail};
        internal static Obs.ReadIssue Issue(string field,Common.UnavailableReason reason,string detail)=>new Obs.ReadIssue {Field=field,Unavailable=Unavailable(reason,detail)};
        internal static Obs.Completeness Complete(int count,int filtered=0)=>new Obs.Completeness {Page=new Common.PageInfo {Complete=true},Matched=(ulong)count,Returned=(ulong)count,Filtered=(ulong)filtered,Unreadable=0};
        internal static void Require(int count,int limit=256) { if(count>limit) throw new ReadLimit("Complete native pawn collection exceeds the requested bound; frozen paging is unavailable."); }
        internal static object Encode(Obs.ListPawnsReply reply) {
            if(Encoding.UTF8.GetByteCount(JsonFormatter.Default.Format(reply))>ProtoBoundary.MaximumEnvelopeBytes) throw new ReadLimit("Pawn reply exceeds one MiB.");
            return ProtoBoundary.Encode(reply);
        }
        internal sealed class ReadLimit:Exception { internal ReadLimit(string message):base(message) {} }
    }
}
