using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Facts;
using RimGovernor.Brain.GameAccess;
using DefinitionCatalog = RimGovernor.Brain.Bridge.DefinitionCatalog;
using CatalogStats = RimGovernor.Brain.Bridge.CatalogStats;
using RoomRole = RimGovernor.Brain.GameFacts.Observations.RoomRole;
namespace RimGovernor.Brain.Bridge;

public static class SleepingUpkeepReads
{
    internal static readonly RoomQualityStatKeys QualityStatKeys = new(nameof(RimWorld.RoomStatDefOf.Space), nameof(RimWorld.RoomStatDefOf.Beauty),
        nameof(RimWorld.RoomStatDefOf.Cleanliness), nameof(RimWorld.RoomStatDefOf.Wealth), nameof(RimWorld.RoomStatDefOf.Impressiveness));
    /// <summary>
    /// The sleeping census: the upkeep people, slaves, guests and beds, each bed joined to its building table row and
    /// its def's catalog rows. A bed the building table does not hold, or a def the catalog cannot answer for, throws.
    /// </summary>
    internal static SleepingObservation ColonySleeping(RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot v, Buildings buildings, Pawns pawns, DefinitionCatalog? catalog)
    {
        var u = v.Upkeep;
        EquatableArray<SleepingPerson> People(IEnumerable<RimGovernor.Brain.GameFacts.Observations.UpkeepPerson> rows)
        {
            var output = new List<SleepingPerson>();
            foreach (var p in rows)
            {
                if (!pawns.TryGetValue(p.Pawn.Value, out var pawn))
                    throw new ObservationContractException($"sleeping person {p.Pawn} has no pawn row");
                output.Add(new SleepingPerson
                {
                    Native = p,
                    Pawn = pawn,
                    Title = p.Title is RimGovernor.Brain.GameFacts.Observations.RoyalStanding.Titled t ? ColonyPlanningReads.RequireCatalog(catalog).RoyalTitleOf(t.Title, t.Ascetic, t.Precepts) : null,
                });
            }
            return EquatableArray.From(output);
        }
        if (u.People.Count != v.ColonistCount)
        {
            throw new ObservationContractException($"sleeping census lists {u.People.Count} colonists for a colony of {v.ColonistCount}");
        }
        var r = new SleepingObservation
        {
            Colonists = (int)v.ColonistCount,
            People = People(u.People),
            Slaves = People(u.Slaves),
            Guests = People(u.Guests),
        };
        var beds = new List<SleepingBed>();
        foreach (var b in u.Beds)
        {
            if (!buildings.TryGetValue(b.Id, out var row))
            {
                throw new ObservationContractException($"sleeping bed {b.Id} has no building row");
            }
            var head = row.Building;
            if (head.Place is not RimGovernor.Brain.GameFacts.Observations.EntityPlace.OnMap)
                throw new ObservationContractException("sleeping building is not on the map");
            if (catalog is null)
            {
                throw new BridgeContractException("sleeping beds: no definition catalog was loaded");
            }
            bool humanlike;
            bool crib;
            double rest;
            try
            {
                humanlike = catalog.BedHumanlike(head.DefName);
                crib = catalog.BedCrib(head.DefName);
                rest = catalog.PlannerStatValue(head.DefName, row.Stuff ?? "", CatalogStats.BedRestEffectiveness);
            }
            catch (DomainException e)
            {
                throw new BridgeContractException($"sleeping bed {b.Id}: {e.Message}");
            }
            beds.Add(new SleepingBed
            {
                Native = b,
                Building = row,
                Humanlike = humanlike,
                Crib = crib,
                RestEffectiveness = rest,
            });
        }
        return r with { Beds = EquatableArray.From(beds) };
    }

    /// <summary>
    /// The room-quality view of the frame's rooms census: every room holding a colonist bed (humanlike, not
    /// medical, not for prisoners) or carrying a common role (dining, rec room) and not fogged, with its native
    /// stats.
    /// </summary>
    internal static EquatableArray<UpkeepRoom> UpkeepRooms(RimGovernor.Brain.GameFacts.Observations.RoomsSnapshot census, EquatableArray<SleepingBed> beds)
    {
        var owned = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var b in beds)
        {
            if (!b.Humanlike || b.Medical || b.Prisoners)
            {
                continue;
            }
            if (!owned.TryGetValue(b.Room, out var list))
            {
                owned[b.Room] = list = [];
            }
            list.Add(b.Id);
        }
        var rows = new List<UpkeepRoom>();
        foreach (var r in census.Rooms)
        {
            var held = owned.GetValueOrDefault(r.Id) ?? [];
            if (held.Count == 0 && (r.Fogged || (r.Role != RoomRole.DiningRoom && r.Role != RoomRole.RecRoom)))
            {
                continue;
            }
            var room = new UpkeepRoom { Native = r, StatKeys = QualityStatKeys, Beds = EquatableArray.From(held) };
            // Required native quality evidence fails the frame read, before any policy uses it.
            try { _ = room.Quality; }
            catch (DomainException e) { throw new ObservationContractException(e.Message); }
            rows.Add(room);
        }
        return EquatableArray.From(rows);
    }

}
