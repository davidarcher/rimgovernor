namespace RimGovernor.Brain.Observation;

public static class SleepingUpkeepReads
{
    /// <summary>
    /// The sleeping census: the upkeep people, slaves, guests and beds, each bed joined to its building table row and
    /// its def's catalog rows. A bed the building table does not hold, or a def the catalog cannot answer for, throws.
    /// </summary>
    internal static Fact<SleepingObservation> ColonySleeping(O.ColonyFactsSnapshot v, Buildings buildings, DefinitionCatalog? catalog)
    {
        var u = v.Upkeep;
        EquatableArray<SleepingPerson> People(IEnumerable<O.UpkeepPerson> rows)
        {
            var output = new List<SleepingPerson>();
            foreach (var p in rows)
            {
                output.Add(new SleepingPerson
                {
                    Id = p.Pawn,
                    OwnedBed = Known(p.OwnedBed ?? ""),
                    ComfortableMin = p.ComfortableMinC,
                    ComfortableMax = p.ComfortableMaxC,
                    Partners = p.Partners,
                    BedSharingAllowed = p.BedSharingAllowed,
                    Title = p.Title is O.RoyalStanding.Titled t ? ColonyDecodeReads.RequireCatalog(catalog).RoyalTitleOf(t.Title, t.Ascetic, t.Precepts) : null,
                });
            }
            return EquatableArray.From(output);
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
            if (catalog is null)
            {
                throw new BridgeContractException("sleeping beds: no definition catalog was loaded");
            }
            bool humanlike;
            bool crib;
            double rest;
            try
            {
                humanlike = catalog.BedHumanlike((head.DefName ?? ""));
                crib = catalog.BedCrib((head.DefName ?? ""));
                rest = catalog.PlannerStatValue((head.DefName ?? ""), b.Stuff ?? "", CatalogStats.BedRestEffectiveness);
            }
            catch (DomainException e)
            {
                throw new BridgeContractException($"sleeping bed {b.Id}: {e.Message}");
            }
            beds.Add(new SleepingBed
            {
                Id = b.Id,
                Definition = new Resource((head.DefName ?? "")),
                Humanlike = humanlike,
                Crib = crib,
                Medical = b.Medical,
                Prisoners = b.Prisoners,
                Slaves = b.ForSlaves,
                Roofed = b.Roofed,
                RestEffectiveness = (float)rest,
                Temperature = b.TemperatureC,
                Owners = b.Owners,
                Users = b.Users,
                AccessibleTo = b.AccessibleTo,
                Room = b.Room,
                Quality = b.Quality,
                Stuff = b.Stuff,
                Cell = ColonyDecodeReads.MapCellOf(head),
            });
        }
        return Known(r with { Beds = EquatableArray.From(beds) });
    }

    /// <summary>
    /// The room-quality view of the frame's rooms census: every room holding a colonist bed (humanlike, not
    /// medical, not for prisoners) or carrying a common role (dining, rec room) and not fogged, with its native
    /// stats.
    /// </summary>
    internal static Fact<EquatableArray<UpkeepRoom>> UpkeepRooms(O.RoomsSnapshot census, EquatableArray<SleepingBed> beds)
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
            var quality = new RoomQuality(RoomStat(r, "Space"), RoomStat(r, "Beauty"), RoomStat(r, "Cleanliness"), RoomStat(r, "Wealth"), RoomStat(r, "Impressiveness"));
            rows.Add(new UpkeepRoom { Id = r.Id, Role = r.Role, Quality = quality, Cells = (int)r.CellCount, Beds = EquatableArray.From(held) });
        }
        return Known(EquatableArray.From(rows));
    }

    /// <summary>One named native room stat; the census carries every quality stat for every room.</summary>
    private static double RoomStat(O.RoomState room, string name) =>
        room.Stats.FirstOrDefault(stat => stat.DefName == name)?.Value
            ?? throw new ObservationContractException($"room {room.Id} carries no {name} stat");
}
