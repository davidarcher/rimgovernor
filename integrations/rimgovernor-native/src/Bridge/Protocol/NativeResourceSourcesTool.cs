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
    // Typed successor to the legacy untyped home/resource_sources tool
    // (ResourceAcquisitionTools.Sources) production_policy.py's resource_method
    // still calls directly. This adapter reuses that class's exact eligibility,
    // designation and safety logic (ResourceAcquisitionTools.Eligible/
    // Designated/MiningBlocker) so both the old and new surfaces agree on what
    // counts as a reachable, safe source, but reports the complete typed
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

        [Tool(ToolName, Title = "Read reachable native resource sources", Description = "Every visible, safely reachable native mining or mature wild-plant sources for one exact output resource definition. Yields are estimates; only ordinary pawn labor produces actual stock. Extraction-development detail is not yet implemented by this adapter.")]
        [ToolResponse("payload", "string", "Official ProtoJSON ResourceSourcesReply.", Always = true)]
        public async Task<object> ListResourceSources(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON ResourceSourcesRequest string in raw transport value.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, ToolName, request!, Obs.ResourceSourcesRequest.Parser, out var parsed, out var failure)
                || !Validate(parsed, out failure)) return ProtoBoundary.Encode(new Obs.ResourceSourcesReply { Failure = failure });
            return await ProtoBoundary.OnMainThread(ctx, () => {
                if (!ProtoBoundary.ValidateIdentity(parsed.Scope?.ExpectedIdentity, out var map, out var context, out var error))
                    return ProtoBoundary.Encode(new Obs.ResourceSourcesReply { Failure = error });
                return ProtoBoundary.Encode(Read(map, parsed, context));
            }, cancellationToken).ConfigureAwait(false);
        }

        // Cheap is the read's speed switch (#1295): on, the census reads only
        // the plant and rock defs that can yield the resource, and the map's
        // reservations are read once (ExcavationSafety.Shortcut is the
        // support check's own switch). The reply is identical either
        // way; test/colony_facts_equality turns it off to prove that.
        internal static bool Cheap = true;

        // Read is the read on the main thread under a validated identity: the
        // reply its tool encodes, and the section the bundle carries (#593).
        internal static Obs.ResourceSourcesReply Read(Map map, Obs.ResourceSourcesRequest parsed, Common.ObservationContext context)
        {
            var cheap = Cheap;
            try
            {
                var definition = DefDatabase<ThingDef>.GetNamedSilentFail(parsed.Resource);
                if (definition == null)
                    return new Obs.ResourceSourcesReply { Unavailable = Unavailable(Common.UnavailableReason.NotApplicable, "Unknown resource definition.") };
                // Only a plant or rock def can yield the resource, so the
                // census reads those defs' lister lists rather than every
                // thing on the map; Product still decides per thing. The
                // final sort is total, so census order does not matter.
                List<Thing> deposits;
                if (cheap)
                {
                    deposits = new List<Thing>();
                    foreach (var def in DefDatabase<ThingDef>.AllDefsListForReading)
                        if (def.plant?.harvestedThingDef?.defName == parsed.Resource || def.building?.mineableThing?.defName == parsed.Resource)
                            foreach (var t in map.listerThings.ThingsOfDef(def))
                                if (ResourceAcquisitionTools.Product(t)?.defName == parsed.Resource) deposits.Add(t);
                }
                else deposits = map.listerThings.AllThings.Where(t => ResourceAcquisitionTools.Product(t)?.defName == parsed.Resource).ToList();
                var eligible = deposits.Where(t => ResourceAcquisitionTools.Eligible(t, map)).ToList();
                var admitted = new HashSet<Thing>(eligible);
                // Buried ore (#1072): a supported deposit no colonist can reach,
                // usually fogged. The support check reads the true map, so it is
                // reported for a corridor excavation rather than dropped.
                var buried = new HashSet<Thing>(deposits.Where(t => !admitted.Contains(t) && ResourceAcquisitionTools.Buried(t, map)));
                eligible.AddRange(buried);
                var colonists = map.mapPawns.FreeColonistsSpawned.ToList();
                var distances = new Dictionary<Thing, double>(eligible.Count);
                foreach (var t in eligible) distances[t] = colonists.Min(p => p.Position.DistanceTo(t.Position));
                var ordered = eligible.OrderByDescending(ResourceAcquisitionTools.Designated).ThenBy(t => distances[t]).ThenBy(t => t.thingIDNumber).ToList();
                var snapshot = new Obs.ResourceSourcesSnapshot { Context = context, Resource = parsed.Resource,
                    Storage = Storage(map, definition),
                    Completeness = new Obs.Completeness { Filtered = (ulong)(deposits.Count - eligible.Count) } };
                var reserved = cheap ? new HashSet<Thing>(map.reservationManager.AllReservedThings()) : null;
                foreach (var thing in ordered) snapshot.Sources.Add(Project(thing, map, distances[thing], context, buried.Contains(thing), reserved));
                return new Obs.ResourceSourcesReply { Observed = snapshot };
            }
            catch (Exception) { return new Obs.ResourceSourcesReply { Unavailable = Unavailable(Common.UnavailableReason.ReadFailed, "Resource sources could not be read completely.") }; }
        }

        internal static bool Validate(Obs.ResourceSourcesRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Identity scope and an exact resource definition are required.");
            if (request == null || request.Scope?.ExpectedIdentity == null) return false;
            if (!request.HasResource || !ProtoBoundary.IsIdentifier(request.Resource)) return false;
            if (request.IncludeDevelopment)
            {
                failure = ProtoBoundary.Fail(Common.FailureCode.Unsupported, "Extraction-development detail is not implemented by this read adapter.");
                return false;
            }
            return true;
        }

        // Ports ResourceAcquisitionTools.Storage's exact hauler/capacity/
        // candidate-cell scan (the legacy home/resource_sources storage
        // payload production_policy.py's resource_method keys its storage
        // branch off) into the typed StorageCapacity message: haulers,
        // capacity, stored, stack_limit and candidates match that method
        // field-for-field so both surfaces agree on material-storage
        // adequacy. Unlike the legacy untyped reply, deep-drill portion
        // sizing and the hauling WorkType are not carried -- nothing on the
        // Go side reads either yet.
        private static Obs.StorageCapacity Storage(Map map, ThingDef def)
        {
            var haulers = Haulers(map);
            var capacity = Capacity(map, def, haulers, out var stored);
            bool Accessible(IntVec3 c) => !c.Fogged(map) && c.Standable(map) && haulers.Any(p => !c.IsForbidden(p)
                && p.CanReach(c, PathEndMode.OnCell, Danger.None));
            var durable = def.GetStatValueAbstract(StatDefOf.DeteriorationRate) <= 0;
            bool Protected(IntVec3 c) => durable || c.Roofed(map);
            var border = typeof(AutoHomeAreaMaker).GetField("BorderWidth", BindingFlags.Static | BindingFlags.NonPublic)?.GetRawConstantValue();
            var knownBorder = border is int width && width >= 0 && width <= 32;
            var margin = knownBorder ? (int)(border ?? 0) + 1 : 0;
            var candidates = haulers.Count == 0 || !knownBorder ? new List<IntVec3>() : GenRadial.RadialCellsAround(haulers[0].Position, 20, true)
                // Cheapest tests first; reachability last (#1295).
                .Where(c => c.InBounds(map) && map.zoneManager.ZoneAt(c) == null
                    && !c.GetThingList(map).Any(t => t is Building || t is Blueprint || t is Frame || t.def.category == ThingCategory.Item)
                    && Protected(c) && !CellRect.CenteredOn(c, margin).Any(q => q.InBounds(map) && q.GetEdifice(map) is Mineable)
                    && Accessible(c)).ToList();
            var result = new Obs.StorageCapacity { Resource = def.defName, Capacity = capacity, Stored = stored, StackLimit = def.stackLimit };
            result.Haulers.AddRange(NativeRef.All(haulers));
            result.Candidates.AddRange(candidates.Select(c => new Common.Cell { X = c.x, Z = c.z }));
            return result;
        }

        internal static List<Pawn> Haulers(Map map) => map.mapPawns.FreeColonistsSpawned.Where(p => !p.Downed && !p.Drafted
            && !p.InMentalState && !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling)).ToList();

        // Empty-slot capacity (count) of protected, hauler-reachable storage
        // accepting def, and the count of def already stored there.
        internal static long Capacity(Map map, ThingDef def, List<Pawn> haulers, out long stored)
        {
            bool Accessible(IntVec3 c) => !c.Fogged(map) && c.Standable(map) && haulers.Any(p => !c.IsForbidden(p)
                && p.CanReach(c, PathEndMode.OnCell, Danger.None));
            var durable = def.GetStatValueAbstract(StatDefOf.DeteriorationRate) <= 0;
            bool Protected(IntVec3 c) => durable || c.Roofed(map);
            // Count empty stack slots, conservatively excluding partial stacks: one per
            // empty floor cell, and a storage building's (shelf's) free slots of its
            // maxItemsInCell per cell (#721), reached by touch since it is not standable.
            int Slots(IntVec3 c)
            {
                var items = c.GetThingList(map).Count(t => t.def.category == ThingCategory.Item);
                var edifice = c.GetEdifice(map);
                if (edifice == null) return Accessible(c) && items == 0 ? 1 : 0;
                if (!(edifice is Building_Storage) || c.Fogged(map)) return 0;
                var reached = haulers.Any(p => !c.IsForbidden(p) && p.CanReach(c, PathEndMode.Touch, Danger.None));
                return reached ? Math.Max(0, edifice.def.building.maxItemsInCell - items) : 0;
            }
            stored = map.haulDestinationManager.AllGroups.SelectMany(g => g.HeldThings
                .Where(t => t.def == def && g.Settings.AllowedToAccept(t))).Distinct().Sum(t => (long)t.stackCount);
            return (long)map.haulDestinationManager.AllGroups.Where(g => g.Settings.filter.Allows(def))
                .SelectMany(g => g.CellsList).Distinct().Where(Protected).Sum(Slots) * def.stackLimit;
        }

        private static Obs.ResourceSource Project(Thing thing, Map map, double distance, Common.ObservationContext context, bool buried, HashSet<Thing>? reserved)
        {
            var mineable = thing is Mineable;
            var row = new Obs.ResourceSource
            {
                Source = NativeRef.Thing(thing),
                Method = mineable ? "mine" : thing.def.plant.IsTree ? "cut" : "harvest",
                Yield = thing is Plant plant ? plant.YieldNow() : thing.def.building.mineableYield,
                Reachable = !buried,
                Buried = buried,
                Designated = ResourceAcquisitionTools.Designated(thing),
                Safety = ResourceAcquisitionTools.Safety(thing, thing.Map),
                Distance = distance,
            };
            // Only mine sources carry a snapshot token (the routine reads it as
            // the source row's identity); harvest/hunt sources come from the
            // AcquisitionFacts census, which carries its own.
            if (thing is Mineable rock) { row.SourceSnapshot = NativeMineAcquisition.Snapshot(rock, context); row.Cell = new RimGovernor.Protocol.Common.Cell { X = rock.Position.x, Z = rock.Position.z }; }
            if (mineable)
            {
                row.Taken = ResourceAcquisitionTools.Taken(thing, reserved);
                var tick = ResourceAcquisitionTools.DesignatedTick(thing, row.Designated);
                if (tick.HasValue) row.DesignatedTick = tick.Value;
            }
            return row;
        }

        private static Common.Unavailable Unavailable(Common.UnavailableReason reason, string detail) => new Common.Unavailable { Reason = reason, Detail = detail };
    }
}
