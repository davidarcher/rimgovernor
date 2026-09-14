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
    public sealed class NativeObservationTools
    {
        [Tool("rimgovernor/observations_read_status", Title = "Read colony status",
            Description = "Official StatusRequest ProtoJSON. Colonists/threats default true, detail false, predator radius30. Read-only simulation facts; no clock/UI operations. Pages1..256; unavailable instead of truncation.")]
        [ToolResponse("payload", "string", "Official observations StatusReply ProtoJSON.", Always = true)]
        public async Task<object> ReadStatus(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Raw value must be a StatusRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, "rimgovernor/observations_read_status", request!, Obs.StatusRequest.Parser, out var parsed, out var failure)
                || !ValidateStatus(parsed, out failure)) return ProtoBoundary.Encode(new Obs.StatusReply { Failure = failure });
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (!ProtoBoundary.ValidateIdentity(parsed.Scope?.ExpectedIdentity!, map, out var context, out failure))
                    return ProtoBoundary.Encode(new Obs.StatusReply { Failure = failure });
                try { return EncodeBounded(new Obs.StatusReply { Observed = Status(map, parsed, context) }); }
                catch (ReadLimit error) { return ProtoBoundary.Encode(new Obs.StatusReply { Unavailable = Unavailable(Common.UnavailableReason.LimitExceeded, error.Message) }); }
                catch (Exception) { return ProtoBoundary.Encode(new Obs.StatusReply { Unavailable = Unavailable(Common.UnavailableReason.ReadFailed, "Native status facts could not be read completely.") }); }
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("rimgovernor/observations_get_cells", Title = "Read bounded map cells",
            Description = "Official GetCellsRequest ProtoJSON. Exact cells or inclusive rectangle,1..256 cells. Returns native map dimensions. Absent fields select terrain/roof/visibility/traversal; explicit false skips. Other field families are unavailable until migrated.")]
        [ToolResponse("payload", "string", "Official observations GetCellsReply ProtoJSON.", Always = true)]
        public async Task<object> GetCells(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Raw value must be a GetCellsRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, "rimgovernor/observations_get_cells", request!, Obs.GetCellsRequest.Parser, out var parsed, out var failure)
                || !ValidateCells(parsed, out failure)) return ProtoBoundary.Encode(new Obs.GetCellsReply { Failure = failure });
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (!ProtoBoundary.ValidateIdentity(parsed.Scope?.ExpectedIdentity!, map, out var context, out failure))
                    return ProtoBoundary.Encode(new Obs.GetCellsReply { Failure = failure });
                try {
                    var cells = Selection(parsed);
                    if (cells.Any(cell => !cell.InBounds(map))) return ProtoBoundary.Encode(new Obs.GetCellsReply {
                        Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Selected cell is outside the current map.") });
                    var fields = Fields(parsed.Fields);
                    var snapshot = new Obs.CellsSnapshot { Context = context,
                        MapSize = new Obs.MapSize { Width = checked((uint)map.Size.x), Height = checked((uint)map.Size.z) },
                        Region = new Obs.Rectangle { Minimum = Cell(cells.Min(c => c.x), cells.Min(c => c.z)), Maximum = Cell(cells.Max(c => c.x), cells.Max(c => c.z)) },
                        AppliedFields = fields, Completeness = Complete(cells.Count) };
                    foreach (var cell in cells) {
                        var row = new Obs.CellState { Cell = Cell(cell.x, cell.z) };
                        if (fields.Terrain) row.Terrain = Identifier(cell.GetTerrain(map)?.defName);
                        if (fields.Roof) {
                            var roof = cell.GetRoof(map);
                            if (roof == null) row.Issues.Add(Issue("roof", Common.UnavailableReason.NotApplicable, "No roof at this cell."));
                            else row.Roof = Identifier(roof.defName);
                        }
                        if (fields.Visibility) row.Fogged = cell.Fogged(map);
                        if (fields.Traversal) { row.Walkable = cell.Walkable(map); row.Passable = !cell.Impassable(map); }
                        if (fields.Things) {
                            var here = cell.GetThingList(map);
                            RequireCount(here.Count, 256);
                            foreach (var thing in here) row.Things.Add(CellThingRow(thing, context));
                        }
                        snapshot.Cells.Add(row);
                    }
                    return EncodeBounded(new Obs.GetCellsReply { Observed = snapshot });
                }
                catch (ReadLimit error) { return ProtoBoundary.Encode(new Obs.GetCellsReply { Unavailable = Unavailable(Common.UnavailableReason.LimitExceeded, error.Message) }); }
                catch (Exception) { return ProtoBoundary.Encode(new Obs.GetCellsReply { Unavailable = Unavailable(Common.UnavailableReason.ReadFailed, "Native cell facts could not be read completely.") }); }
            }, cancellationToken).ConfigureAwait(false);
        }

        internal static bool ValidateStatus(Obs.StatusRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Valid identity scope, page1..256 and finite nonnegative predator radius required.");
            return request != null && request.Scope?.ExpectedIdentity != null && PageValid(request.Page)
                && (!request.HasPredatorRadius || !double.IsNaN(request.PredatorRadius) && !double.IsInfinity(request.PredatorRadius) && request.PredatorRadius >= 0);
        }
        internal static bool ValidateCells(Obs.GetCellsRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Valid identity, bounded unique exact cells or inclusive rectangle required.");
            if (request == null || request.Scope?.ExpectedIdentity == null || !PageValid(request.Page)) return false;
            var fields = request.Fields;
            if (fields != null && (fields.Zone || fields.Areas || fields.Designations || fields.Room || fields.Growth)) {
                failure = ProtoBoundary.Fail(Common.FailureCode.Unsupported, "Only terrain, roof, visibility, traversal and things cell fields are implemented."); return false;
            }
            try { Selection(request); return true; } catch (Exception) { return false; }
        }
        private static bool PageValid(Common.PageRequest? page) => page == null
            || (!page.HasLimit || page.Limit >= 1 && page.Limit <= 256) && (!page.HasCursor || page.Cursor.Length == 0);
        private static int Limit(Common.PageRequest? page) => page?.HasLimit == true ? (int)page.Limit : 256;
        private static bool HasCell(Common.Cell? cell) => cell != null && cell.HasX && cell.HasZ;
        internal static List<IntVec3> Selection(Obs.GetCellsRequest request)
        {
            var result = new List<IntVec3>(); var limit = Limit(request.Page);
            if (request.SelectionCase == Obs.GetCellsRequest.SelectionOneofCase.ExactCells) {
                if (request.ExactCells.Cells.Count < 1 || request.ExactCells.Cells.Count > limit) throw new ReadLimit("Exact cell count exceeds page limit.");
                foreach (var cell in request.ExactCells.Cells) {
                    if (!HasCell(cell)) throw new ArgumentException("Coordinate presence required.");
                    result.Add(new IntVec3(cell.X, 0, cell.Z));
                }
                if (result.Distinct().Count() != result.Count) throw new ArgumentException("Duplicate cells.");
            } else if (request.SelectionCase == Obs.GetCellsRequest.SelectionOneofCase.Rectangle) {
                var min = request.Rectangle.Minimum; var max = request.Rectangle.Maximum;
                if (!HasCell(min) || !HasCell(max) || max.X < min.X || max.Z < min.Z) throw new ArgumentException("Invalid rectangle.");
                var width = (long)max.X - min.X + 1; var height = (long)max.Z - min.Z + 1;
                if (width > limit || height > limit || width * height > limit) throw new ReadLimit("Rectangle exceeds page limit.");
                for (long z = min.Z; z <= max.Z; z++) for (long x = min.X; x <= max.X; x++) result.Add(new IntVec3((int)x, 0, (int)z));
            } else throw new ArgumentException("Cell selection required.");
            return result;
        }
        internal static Obs.CellFields Fields(Obs.CellFields? source) => new Obs.CellFields {
            Terrain = source == null || !source.HasTerrain || source.Terrain, Roof = source == null || !source.HasRoof || source.Roof,
            Visibility = source == null || !source.HasVisibility || source.Visibility, Traversal = source == null || !source.HasTraversal || source.Traversal,
            // Things is opt-in only (absence selects false), unlike
            // terrain/roof/visibility/traversal's absence-selects-true default:
            // a bounded per-cell thing scan is not something every caller wants.
            Things = source != null && source.HasThings && source.Things,
            Zone = false, Areas = false, Designations = false, Room = false, Growth = false };

        private static Obs.StatusSnapshot Status(Map map, Obs.StatusRequest request, Common.ObservationContext context)
        {
            var result = new Obs.StatusSnapshot { Context = context };
            var wantColonists = !request.HasColonists || request.Colonists;
            var wantThreats = !request.HasThreats || request.Threats;
            var limit = Limit(request.Page);
            if (!wantColonists && !wantThreats) {
                result.Issues.Add(Issue("colonists", Common.UnavailableReason.NotRequested, "Colonist section not requested."));
                result.Issues.Add(Issue("threats", Common.UnavailableReason.NotRequested, "Threat section not requested.")); return result;
            }
            var spawned = map.mapPawns.AllPawnsSpawned.ToList();
            var colonists = spawned.Where(p => !p.Dead && p.IsFreeColonist).ToList();
            if (wantColonists) {
                RequireCount(colonists.Count, limit); var snapshot = new Obs.PawnSnapshot { Context = context, Completeness = Complete(colonists.Count) };
                foreach (var pawn in colonists) snapshot.Pawns.Add(PawnRow(pawn, request.ColonistDetail, context));
                result.Colonists = snapshot;
            } else result.Issues.Add(Issue("colonists", Common.UnavailableReason.NotRequested, "Colonist section not requested."));
            if (!wantThreats) { result.Issues.Add(Issue("threats", Common.UnavailableReason.NotRequested, "Threat section not requested.")); return result; }
            var player = Faction.OfPlayerSilentFail ?? throw new InvalidOperationException("Player faction missing.");
            var threats = new Obs.ThreatsSnapshot(); var radius = request.HasPredatorRadius ? request.PredatorRadius : 30;
            foreach (var pawn in spawned.Where(p => !p.Dead && !p.IsColonist)) {
                var row = PawnRow(pawn, false, context); var nearest = colonists.Count == 0 ? (int?)null : colonists.Min(p => Math.Max(Math.Abs(p.Position.x-pawn.Position.x),Math.Abs(p.Position.z-pawn.Position.z)));
                if (nearest.HasValue) row.NearestColonistDistance = nearest.Value;
                var ours = pawn.Faction == player; var mental = pawn.MentalStateDef?.defName;
                var hostile = mental?.IndexOf("Manhunter", StringComparison.OrdinalIgnoreCase) >= 0 || pawn.Faction != null && !ours && pawn.Faction.HostileTo(player);
                row.Hostile = hostile;
                var threat = new Obs.ThreatPawn { Pawn = row };
                if (hostile) { row.HostileReason = mental?.IndexOf("Manhunter", StringComparison.OrdinalIgnoreCase) >= 0 ? "manhunter:"+mental : "faction:"+pawn.Faction!.GetUniqueLoadID(); threats.Hostiles.Add(threat); }
                else if (pawn.CurJobDef?.defName == "PredatorHunt") {
                    var target = pawn.CurJob.targetA.Thing; var prey = target as Pawn ?? (target as Corpse)?.InnerPawn;
                    threat.PredatorIsOurs = ours;
                    if (prey != null) { threat.Prey = Entity(prey); threat.PreyIsOurs = prey.Faction == player || prey.HostFaction == player; }
                    row.HostileReason = "predatorHunt";
                    if (ours || prey != null && !threat.PreyIsOurs) { threat.IgnoredReason = ours ? "player-owned predator" : "prey is not player-owned"; threats.IgnoredHunters.Add(threat); }
                    else threats.HuntingPredators.Add(threat);
                } else if (radius > 0 && nearest.HasValue && nearest.Value <= radius && !ours) {
                    if (pawn.Downed) { var downed = threat.Clone(); downed.Pawn.HostileReason = "downed"; threats.DownedNear.Add(downed); }
                    if (pawn.RaceProps.predator) { row.HostileReason = "predator_near"; threats.WildPredatorsNear.Add(threat); }
                }
            }
            var count = threats.Hostiles.Count+threats.HuntingPredators.Count+threats.IgnoredHunters.Count+threats.DownedNear.Count+threats.WildPredatorsNear.Count;
            RequireCount(count,limit); threats.Completeness=Complete(count); result.Threats=threats; return result;
        }

        internal static Obs.JobEvidence JobRow(Verse.AI.Job? job, int queuedJobs)
        {
            if (queuedJobs < 0) throw new InvalidOperationException("Negative native queued job count.");
            RequireCount(queuedJobs, 256);
            var row = new Obs.JobEvidence { PlayerForced = job?.playerForced ?? false, QueuedJobs = (uint)queuedJobs };
            if (job != null) {
                row.DefName = Identifier(job.def.defName);
                row.LoadId = job.loadID.ToString(System.Globalization.CultureInfo.InvariantCulture);
            } else row.Issues.Add(Issue("current_job", Common.UnavailableReason.NotApplicable, "Pawn has no current job."));
            return row;
        }

        internal static Obs.PawnState PawnRow(Pawn pawn, bool detail, Common.ObservationContext context)
        {
            var row = new Obs.PawnState { Pawn=Entity(pawn), KindDefName=Identifier(pawn.kindDef?.defName), Dead=pawn.Dead, Downed=pawn.Downed,
                Drafted=pawn.drafter?.Drafted == true, InBed=RestUtility.InBed(pawn), Colonist=pawn.IsColonist, FreeColonist=pawn.IsFreeColonist,
                Prisoner=pawn.IsPrisoner, Humanlike=pawn.RaceProps.Humanlike, Animal=pawn.RaceProps.Animal, Mechanoid=pawn.RaceProps.IsMechanoid,
                Predator=pawn.RaceProps.predator, ManhunterOnDamageChance=Finite(pawn.RaceProps.manhunterOnDamageChance) };
            if (pawn.Faction != null) row.FactionId=Identifier(pawn.Faction.GetUniqueLoadID());
            if (pawn.MentalStateDef != null) row.MentalState=Identifier(pawn.MentalStateDef.defName);
            if (pawn.jobs?.jobQueue != null) row.Job=JobRow(pawn.CurJob, pawn.jobs.jobQueue.Count);
            else row.Issues.Add(Issue("job",Common.UnavailableReason.NativeComponentMissing,"Pawn job tracker or queue is unavailable."));
            NativePawnControlObservation.Apply(pawn, row, context);
            var needs=new Obs.PawnNeeds(); row.Needs=needs;
            if (pawn.needs?.mood != null) needs.Mood=Finite(pawn.needs.mood.CurLevelPercentage);
            else needs.Issues.Add(Issue("mood",Common.UnavailableReason.NativeComponentMissing,"Mood tracker unavailable."));
            if (detail) {
                if(pawn.needs?.food != null) { needs.Food=Finite(pawn.needs.food.CurLevelPercentage); needs.HungerCategory=pawn.needs.food.CurCategory.ToString(); }
                else needs.Issues.Add(Issue("food",Common.UnavailableReason.NativeComponentMissing,"Food tracker unavailable."));
                if(pawn.needs?.rest != null) needs.Rest=Finite(pawn.needs.rest.CurLevelPercentage); else needs.Issues.Add(Issue("rest",Common.UnavailableReason.NativeComponentMissing,"Rest tracker unavailable."));
                if(pawn.needs?.joy != null) needs.Joy=Finite(pawn.needs.joy.CurLevelPercentage); else needs.Issues.Add(Issue("joy",Common.UnavailableReason.NativeComponentMissing,"Joy tracker unavailable."));
            }
            var breaker=pawn.mindState?.mentalBreaker;
            if(breaker != null) { needs.BreakThresholdMinor=Finite(breaker.BreakThresholdMinor); needs.BreakThresholdMajor=Finite(breaker.BreakThresholdMajor); needs.BreakThresholdExtreme=Finite(breaker.BreakThresholdExtreme);
                if(needs.HasMood) needs.BreakRisk=needs.Mood<=needs.BreakThresholdExtreme?"extreme":needs.Mood<=needs.BreakThresholdMajor?"major":needs.Mood<=needs.BreakThresholdMinor?"minor":"none"; }
            else needs.Issues.Add(Issue("break_thresholds",Common.UnavailableReason.NativeComponentMissing,"Mental breaker unavailable."));
            if(pawn.health?.summaryHealth == null || pawn.health.hediffSet == null) row.Issues.Add(Issue("health",Common.UnavailableReason.NativeComponentMissing,"Health tracker unavailable."));
            else {
                var health=new Obs.PawnHealth { SummaryFraction=Finite(pawn.health.summaryHealth.SummaryHealthPercent), NeedsTend=pawn.health.HasHediffsNeedingTend(false), BleedRatePerDay=Finite(pawn.health.hediffSet.BleedRateTotal) };
                health.Bleeding=health.BleedRatePerDay>0;
                if(detail) { RequireCount(pawn.health.hediffSet.hediffs.Count,256);
                    foreach(var h in pawn.health.hediffSet.hediffs) {
                        var condition=new Obs.Hediff { Definition=new Obs.DefinitionRef { DefName=Identifier(h.def.defName), Label=Diagnostic(h.LabelCap) }, Severity=Finite(h.Severity), SeverityLabel=Diagnostic(h.SeverityLabel??""), Visible=h.Visible };
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
            if (thing.Spawned && thing.Map != null) { row.MapId=thing.Map.uniqueID; row.Position=Cell(thing.Position.x,thing.Position.z); }
            return row;
        }
        // Populates each thing's own CAS token via NativeWasteOperations.Token,
        // the same self-computed hash NativeWasteOperations.Prepare checks: this
        // is the real production discovery path bridge.ReadWasteTarget (and
        // ReadFilthTarget) issue via a Things-scoped observations_get_cells read,
        // since no dedicated per-item lookup RPC exists for a generic loose thing.
        private static Obs.CellThing CellThingRow(Thing thing, Common.ObservationContext context)
        {
            var entity = Entity(thing);
            entity.Snapshot = new Obs.SnapshotRef { Context = context.Clone(), EntityId = entity.Id, Token = NativeWasteOperations.Token(context.Identity, thing) };
            return new Obs.CellThing {
                Thing = entity, ClassName = thing.GetType().Name, StackCount = thing.stackCount,
                Forbidden = thing.IsForbidden(Faction.OfPlayer),
            };
        }
        private static Common.Cell Cell(int x,int z)=>new Common.Cell { X=x,Z=z };
        private static string Identifier(string? value) => ProtoBoundary.IsIdentifier(value!) ? value! : throw new InvalidOperationException("Native identifier unavailable.");
        private static string Diagnostic(string value)=>PlacementPreviewOperation.Diagnostic(value);
        private static double Finite(double value)=>double.IsNaN(value)||double.IsInfinity(value)?throw new InvalidOperationException("Nonfinite native fact."):value;
        private static Common.Unavailable Unavailable(Common.UnavailableReason reason,string detail)=>new Common.Unavailable { Reason=reason,Detail=detail };
        private static Obs.ReadIssue Issue(string field,Common.UnavailableReason reason,string detail)=>new Obs.ReadIssue { Field=field,Unavailable=Unavailable(reason,detail) };
        private static Obs.Completeness Complete(int count)=>new Obs.Completeness { Page=new Common.PageInfo { Complete=true },Matched=(ulong)count,Returned=(ulong)count,Filtered=0,Unreadable=0 };
        private static void RequireCount(int count,int limit) { if(count>limit) throw new ReadLimit("Complete native collection exceeds page limit; frozen paging not implemented."); }
        private static object EncodeBounded(IMessage reply) { if(Encoding.UTF8.GetByteCount(JsonFormatter.Default.Format(reply))>1024*1024) throw new ReadLimit("Observation reply exceeds1MiB."); return ProtoBoundary.Encode(reply); }
        private sealed class ReadLimit:Exception { internal ReadLimit(string message):base(message) {} }
    }
}
