#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    // Typed successor to the legacy untyped "home/resource_sources" tool
    // (ResourceAcquisitionTools.Sources) production_policy.py's resource_method
    // still calls directly. This adapter reuses that class's exact eligibility,
    // designation and safety logic (ResourceAcquisitionTools.Eligible/
    // Designated/MiningBlocker) so both the old and new surfaces agree on what
    // counts as a reachable, safe source, but reports only the bounded typed
    // ResourceSource rows the new ResourceSourcesSnapshot contract wants.
    // Storage capacity is now populated (Storage below), porting
    // ResourceAcquisitionTools.Storage's exact hauler/capacity/candidate scan
    // so both surfaces agree on material-storage adequacy too.
    // Deliberately narrower than the legacy read in one
    // remaining respect: extraction-development detail stays unset (an
    // explicit Unsupported failure when development is requested).
    public sealed class NativeResourceSourcesTool
    {
        private const string ToolName = "rimgovernor/observations_list_resource_sources";
        private const int SourceLimit = 64;

        [Tool(ToolName, Title = "Read reachable native resource sources", Description = "Up to 64 visible, safely reachable native mining or mature wild-plant sources for one exact output resource definition. Yields are estimates; only ordinary pawn labor produces actual stock. Extraction-development detail is not yet implemented by this adapter.")]
        [ToolResponse("payload", "string", "Official ProtoJSON ResourceSourcesReply.", Always = true)]
        public async Task<object> ListResourceSources(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON ResourceSourcesRequest string in raw transport value.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, ToolName, request!, Obs.ResourceSourcesRequest.Parser, out var parsed, out var failure)
                || !Validate(parsed, out failure)) return ProtoBoundary.Encode(new Obs.ResourceSourcesReply { Failure = failure });
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (!ProtoBoundary.ValidateIdentity(parsed.Scope.ExpectedIdentity, map, out var context, out var error))
                    return ProtoBoundary.Encode(new Obs.ResourceSourcesReply { Failure = error });
                try
                {
                    var definition = DefDatabase<ThingDef>.GetNamedSilentFail(parsed.Resource);
                    if (definition == null)
                        return ProtoBoundary.Encode(new Obs.ResourceSourcesReply { Unavailable = Unavailable(Common.UnavailableReason.NotApplicable, "Unknown resource definition.") });
                    var deposits = map.listerThings.AllThings.Where(t => ResourceAcquisitionTools.Product(t)?.defName == parsed.Resource && !t.Position.Fogged(map)).ToList();
                    var eligible = deposits.Where(t => ResourceAcquisitionTools.Eligible(t, map)).ToList();
                    double Distance(Thing t) => map.mapPawns.FreeColonistsSpawned.Min(p => p.Position.DistanceTo(t.Position));
                    var ordered = eligible.OrderByDescending(ResourceAcquisitionTools.Designated).ThenBy(Distance).ThenBy(t => t.thingIDNumber).ToList();
                    Require(ordered.Count <= SourceLimit, "Reachable resource source collection exceeds the read bound; narrow the query.");
                    var snapshot = new Obs.ResourceSourcesSnapshot { Context = context, Resource = parsed.Resource,
                        Storage = Storage(map, definition),
                        Completeness = new Obs.Completeness { Page = new Common.PageInfo { Complete = true },
                            Matched = (ulong)ordered.Count, Returned = (ulong)ordered.Count, Filtered = (ulong)(deposits.Count - eligible.Count) } };
                    foreach (var thing in ordered) snapshot.Sources.Add(Project(thing, map, Distance(thing), context));
                    return Encode(new Obs.ResourceSourcesReply { Observed = snapshot });
                }
                catch (ReadLimit errorLimit) { return ProtoBoundary.Encode(new Obs.ResourceSourcesReply { Unavailable = Unavailable(Common.UnavailableReason.LimitExceeded, errorLimit.Message) }); }
                catch (Exception) { return ProtoBoundary.Encode(new Obs.ResourceSourcesReply { Unavailable = Unavailable(Common.UnavailableReason.ReadFailed, "Resource sources could not be read completely.") }); }
            }, cancellationToken).ConfigureAwait(false);
        }

        internal static bool Validate(Obs.ResourceSourcesRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Identity scope and an exact resource definition are required.");
            if (request == null || request.Scope?.ExpectedIdentity == null) return false;
            if (!request.HasResource || !ProtoBoundary.IsIdentifier(request.Resource)) return false;
            if (request.Page != null && request.Page.HasCursor && request.Page.Cursor.Length != 0)
            {
                failure = ProtoBoundary.Fail(Common.FailureCode.Unsupported, "Paginated resource source reads are not supported by this adapter.");
                return false;
            }
            if (request.IncludeDevelopment)
            {
                failure = ProtoBoundary.Fail(Common.FailureCode.Unsupported, "Extraction-development detail is not implemented by this read adapter.");
                return false;
            }
            return true;
        }

        // Ports ResourceAcquisitionTools.Storage's exact hauler/capacity/
        // candidate-cell scan (the legacy "home/resource_sources" storage
        // payload production_policy.py's resource_method keys its storage
        // branch off) into the typed StorageCapacity message: haulers,
        // capacity, stored, stack_limit and candidates match that method
        // field-for-field so both surfaces agree on material-storage
        // adequacy. Unlike the legacy untyped reply, deep-drill portion
        // sizing and the hauling WorkType are not carried -- nothing on the
        // Go side reads either yet.
        private static Obs.StorageCapacity Storage(Map map, ThingDef def)
        {
            var haulers = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Downed && !p.Drafted && !p.InMentalState
                && !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling)).ToList();
            bool Accessible(IntVec3 c) => !c.Fogged(map) && c.Standable(map) && haulers.Any(p => !c.IsForbidden(p)
                && p.CanReach(c, PathEndMode.OnCell, Danger.None));
            bool Protected(IntVec3 c) => def.GetStatValueAbstract(StatDefOf.DeteriorationRate) <= 0 || c.Roofed(map);
            // Count only empty floor slots, conservatively excluding shelves and partial stacks.
            var capacity = map.haulDestinationManager.AllGroups.Where(g => g.Settings.filter.Allows(def))
                .SelectMany(g => g.CellsList).Distinct().Count(c => Accessible(c) && Protected(c)
                    && c.GetEdifice(map) == null && !c.GetThingList(map).Any(t => t.def.category == ThingCategory.Item)) * def.stackLimit;
            var stored = map.haulDestinationManager.AllGroups.SelectMany(g => g.HeldThings
                .Where(t => t.def == def && g.Settings.AllowedToAccept(t))).Distinct().Sum(t => t.stackCount);
            var border = typeof(AutoHomeAreaMaker).GetField("BorderWidth", BindingFlags.Static | BindingFlags.NonPublic)?.GetRawConstantValue();
            var knownBorder = border is int width && width >= 0 && width <= 32;
            var margin = knownBorder ? (int)(border ?? 0) + 1 : 0;
            var candidates = haulers.Count == 0 || !knownBorder ? new List<IntVec3>() : GenRadial.RadialCellsAround(haulers[0].Position, 20, true)
                .Where(c => c.InBounds(map) && Accessible(c) && Protected(c) && map.zoneManager.ZoneAt(c) == null
                    && !CellRect.CenteredOn(c, margin).Any(q => q.InBounds(map) && q.GetEdifice(map) is Mineable)
                    && !c.GetThingList(map).Any(t => t is Building || t is Blueprint || t is Frame || t.def.category == ThingCategory.Item))
                .Take(8).ToList();
            var result = new Obs.StorageCapacity { Resource = def.defName, Capacity = capacity, Stored = stored, StackLimit = def.stackLimit };
            result.Haulers.AddRange(haulers.Select(p => new Obs.EntityRef { Id = p.GetUniqueLoadID(), DefName = p.def.defName,
                MapId = map.uniqueID, Position = new Common.Cell { X = p.Position.x, Z = p.Position.z } }));
            result.Candidates.AddRange(candidates.Select(c => new Common.Cell { X = c.x, Z = c.z }));
            return result;
        }

        private static Obs.ResourceSource Project(Thing thing, Map map, double distance, Common.ObservationContext context)
        {
            var mineable = thing is Mineable;
            var entity = new Obs.EntityRef { Id = thing.GetUniqueLoadID(), DefName = thing.def.defName, MapId = map.uniqueID,
                Position = new Common.Cell { X = thing.Position.x, Z = thing.Position.z } };
            // Only mine sources carry a snapshot token: AcquireResource can now
            // dispatch against a mined source (NativeMineAcquisition), but
            // harvest/hunt sources are still reached only through the
            // AcquisitionFacts census path, which already carries its own
            // token.
            if (thing is Mineable rock) entity.Snapshot = NativeMineAcquisition.Snapshot(rock, context);
            var row = new Obs.ResourceSource
            {
                Source = entity,
                Method = mineable ? "mine" : thing.def.plant.IsTree ? "cut" : "harvest",
                Yield = thing is Plant plant ? plant.YieldNow() : thing.def.building.mineableYield,
                Reachable = true,
                Designated = ResourceAcquisitionTools.Designated(thing),
                Safety = mineable ? "open_surface" : "native_eligible",
                Distance = distance,
            };
            return row;
        }

        private static Common.Unavailable Unavailable(Common.UnavailableReason reason, string detail) => new Common.Unavailable { Reason = reason, Detail = detail };
        private static object Encode(Obs.ResourceSourcesReply reply)
        {
            Require(Encoding.UTF8.GetByteCount(JsonFormatter.Default.Format(reply)) <= 1024 * 1024, "Complete resource sources reply exceeds 1 MiB.");
            return ProtoBoundary.Encode(reply);
        }
        private static void Require(bool value, string detail) { if (!value) throw new ReadLimit(detail); }
        private sealed class ReadLimit : Exception { internal ReadLimit(string message) : base(message) { } }
    }
}
