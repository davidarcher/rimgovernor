using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimGovernor.Native
{
    // Typed resource source read. It reuses ResourceAcquisitionTools' eligibility,
    // designation and safety logic (Eligible/Designated/MiningBlocker) for what
    // counts as a reachable, safe source and reports the complete typed
    // ResourceSource rows of the ResourceSourcesSnapshot contract. Storage
    // capacity is populated by Storage below. Extraction-development detail
    // stays unset (an explicit Unsupported failure when development is
    // requested).
    internal static class NativeResourceSourcesTool
    {

        internal static RimGovernor.Brain.GameFacts.Observations.ResourceSourcesSnapshot HandleListResourceSources(RimGovernor.Brain.GameOrders.Observations.ResourceSourcesRequest request)
        {
            if (!Validate(request, out var failure)) throw NativeFaults.Refused(failure);
            if (!GameThread.ValidateIdentity(request.Scope?.ExpectedIdentity, out var map, out _, out var error))
                throw NativeFaults.Refused(error);
            return Read(map, request);
        }

        // Cheap is the read's speed switch: on, the census reads only
        // the plant and rock defs that can yield the resource, and the map's
        // reservations are read once (ExcavationSafety.Shortcut is the
        // support check's own switch). The reply is identical either
        // way; test/colony_facts_equality turns it off to prove that.
        internal static bool Cheap = true;

        // Read is the read on the main thread under a validated identity: the
        // reply its tool encodes, and the section the bundle carries.
        internal static RimGovernor.Brain.GameFacts.Observations.ResourceSourcesSnapshot Read(Map map, RimGovernor.Brain.GameOrders.Observations.ResourceSourcesRequest parsed)
        {
            var cheap = Cheap;
            var definition = DefDatabase<ThingDef>.GetNamedSilentFail(parsed.Resource)
                ?? throw NativeFaults.Unavailable(new RimGovernor.Brain.GameFacts.Common.Unavailable { Reason = RimGovernor.Brain.GameFacts.Common.UnavailableReason.NotApplicable, Detail = "Unknown resource definition." });
            try
            {
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
                // Buried ore: a supported deposit no colonist can reach,
                // usually fogged. The support check reads the true map, so it is
                // reported for a corridor excavation rather than dropped.
                var buried = new HashSet<Thing>(deposits.Where(t => !admitted.Contains(t) && ResourceAcquisitionTools.Buried(t, map)));
                eligible.AddRange(buried);
                var colonists = map.mapPawns.FreeColonistsSpawned.ToList();
                var distances = new Dictionary<Thing, double>(eligible.Count);
                foreach (var t in eligible) distances[t] = colonists.Min(p => p.Position.DistanceTo(t.Position));
                var ordered = eligible.OrderByDescending(ResourceAcquisitionTools.Designated).ThenBy(t => distances[t]).ThenBy(t => t.thingIDNumber).ToList();
                var reserved = cheap ? new HashSet<Thing>(map.reservationManager.AllReservedThings()) : null;
                return new RimGovernor.Brain.GameFacts.Observations.ResourceSourcesSnapshot
                {
                    Sources = [.. ordered.Select(thing => Project(thing, distances[thing], buried.Contains(thing), reserved))],
                    Storage = Storage(map, definition),
                };
            }
            catch (Exception readError)
            {
                ModLog.Error("observe", "Resource sources read failed: " + readError);
                throw NativeFaults.Unavailable(new RimGovernor.Brain.GameFacts.Common.Unavailable { Reason = RimGovernor.Brain.GameFacts.Common.UnavailableReason.ReadFailed, Detail = "Resource sources could not be read completely." });
            }
        }

        internal static bool Validate(RimGovernor.Brain.GameOrders.Observations.ResourceSourcesRequest request, out RimGovernor.Brain.GameFacts.Common.Failure failure)
        {
            failure = GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Identity scope and an exact resource definition are required.");
            if (request == null || request.Scope?.ExpectedIdentity == null) return false;
            if (request.Resource is null || !GameThread.IsIdentifier(request.Resource)) return false;
            return true;
        }

        // ResourceAcquisitionTools.Storage's hauler/capacity/candidate-cell
        // scan as the typed StorageCapacity message: haulers, capacity,
        // stored, stack_limit and candidates. Deep-drill portion sizing and
        // the hauling WorkType are not carried -- nothing on the brain side
        // reads either.
        private static RimGovernor.Brain.GameFacts.Observations.StorageCapacity Storage(Map map, ThingDef def)
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
                // Cheapest tests first; reachability last.
                .Where(c => c.InBounds(map) && map.zoneManager.ZoneAt(c) == null
                    && !c.GetThingList(map).Any(t => t is Building || t is Blueprint || t is Frame || t.def.category == ThingCategory.Item)
                    && Protected(c) && !Verse.CellRect.CenteredOn(c, margin).Any(q => q.InBounds(map) && q.GetEdifice(map) is Mineable)
                    && Accessible(c)).ToList();
            return new RimGovernor.Brain.GameFacts.Observations.StorageCapacity
            {
                Capacity = capacity,
                Stored = stored,
                Haulers = [.. haulers.Select(p => new RimGovernor.Brain.Domain.PawnId(p.GetUniqueLoadID()))],
                Candidates = [.. candidates.Select(c => new RimGovernor.Brain.Domain.Cell(c.x, c.z))],
            };
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
            // maxItemsInCell per cell, reached by touch since it is not standable.
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

        private static RimGovernor.Brain.GameFacts.Observations.ResourceSource Project(Thing thing, double distance, bool buried, HashSet<Thing>? reserved) => new()
        {
            Id = thing.GetUniqueLoadID(),
            DefName = thing.def.defName,
            Cell = new RimGovernor.Brain.Domain.Cell(thing.Position.x, thing.Position.z),
            Yield = thing is Plant plant ? plant.YieldNow() : thing.def.building.mineableYield,
            Buried = buried,
            Designated = ResourceAcquisitionTools.Designated(thing),
            Safety = ResourceAcquisitionTools.Safety(thing, thing.Map),
            Distance = distance,
            Taken = ResourceAcquisitionTools.Taken(thing, reserved),
        };
    }
}
