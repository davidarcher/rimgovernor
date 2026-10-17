namespace RimGovernor.Brain.Observation;

public static class SleepingUpkeepReads
{
    internal static Fact<SleepingObservation> ColonySleeping(O.ColonyFactsSnapshot v, Buildings buildings, DefinitionCatalog? catalog)
    {
        var u = v.Upkeep?.Outcome;
        if (u is null || ColonyDecodeReads.HasIssue(u.Issues, "people") || ColonyDecodeReads.HasIssue(u.Issues, "beds"))
        {
            return Unknown<SleepingObservation>();
        }
        EquatableArray<PawnId> Ids(IEnumerable<string> values)
        {
            var rows = new List<PawnId>();
            foreach (var value in values)
            {
                rows.Add(new PawnId(value));
            }
            return EquatableArray.From(rows);
        }
        EquatableArray<SleepingPerson> People(IEnumerable<O.UpkeepPerson> rows)
        {
            var output = new List<SleepingPerson>();
            foreach (var p in rows)
            {
                RoyalTitle? title = null;
                if (p.RoyalTitle is not null)
                {
                    title = ColonyDecodeReads.RequireCatalog(catalog).RoyalTitleOf((p.RoyalTitle ?? ""), p.Ascetic.GetValueOrDefault(), p.Precepts);
                }
                output.Add(new SleepingPerson
                {
                    Id = new PawnId(p.Pawn?.Id ?? ""),
                    OwnedBed = Known(p.OwnedBed?.Id ?? ""),
                    ComfortableMin = ColonyDecodeReads.Optional(p.ComfortableMinC is not null, p.ComfortableMinC.GetValueOrDefault()),
                    ComfortableMax = ColonyDecodeReads.Optional(p.ComfortableMaxC is not null, p.ComfortableMaxC.GetValueOrDefault()),
                    Partners = Ids(FactHelpers.RefIds(p.Partners)),
                    BedSharingAllowed = ColonyDecodeReads.Optional(p.BedSharingAllowed is not null, p.BedSharingAllowed.GetValueOrDefault()),
                    Title = title,
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
            var head = buildings.Entity(b.Bed);
            if (head is null)
            {
                return Unknown<SleepingObservation>();
            }
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
                throw new BridgeContractException($"sleeping bed {b.Bed.Id}: {e.Message}");
            }
            beds.Add(new SleepingBed
            {
                Id = b.Bed.Id ?? "",
                Definition = new Resource((head.DefName ?? "")),
                Humanlike = humanlike,
                Crib = crib,
                Medical = b.Medical,
                Prisoners = b.Prisoners,
                Slaves = b.ForSlaves,
                Roofed = b.Roofed,
                RestEffectiveness = (float)rest,
                Temperature = ColonyDecodeReads.Optional(b.TemperatureC is not null, b.TemperatureC.GetValueOrDefault()),
                Owners = Ids(FactHelpers.RefIds(b.Owners)),
                Users = Ids(FactHelpers.RefIds(b.Users)),
                AccessibleTo = Ids(FactHelpers.RefIds(b.AccessibleTo)),
                Room = ColonyDecodeReads.OptionalRef(b.Room),
                Quality = ColonyDecodeReads.Optional(b.Quality is not null, (int)b.Quality.GetValueOrDefault()),
                Stuff = ColonyDecodeReads.Optional(b.Stuff is not null, b.Stuff ?? ""),
                Cell = ColonyDecodeReads.CellOf(head.Position),
            });
        }
        return Known(r with { Beds = EquatableArray.From(beds) });
    }

    /// <summary>
    /// The room-quality view of the frame's rooms census: every room holding a colonist bed (humanlike, not
    /// medical, not for prisoners) or carrying a common role (dining, rec room) and not fogged, with its native
    /// stats. Unknown when a bed's room is unknown; a room missing any quality stat has unknown quality.
    /// </summary>
    internal static Fact<EquatableArray<UpkeepRoom>> UpkeepRooms(O.RoomsSnapshot? census, EquatableArray<SleepingBed> beds)
    {
        var owned = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var b in beds)
        {
            if (!b.Humanlike || b.Medical || b.Prisoners)
            {
                continue;
            }
            if (!b.Room.TryGetValue(out var room))
            {
                return Unknown<EquatableArray<UpkeepRoom>>();
            }
            if (!owned.TryGetValue(room, out var list))
            {
                owned[room] = list = [];
            }
            list.Add(b.Id);
        }
        var rows = new List<UpkeepRoom>();
        foreach (var r in census?.Rooms ?? [])
        {
            var role = (r.Role ?? "");
            var held = owned.GetValueOrDefault((r.Id ?? "")) ?? [];
            if (held.Count == 0 && (r.Fogged.GetValueOrDefault() || (role != "DiningRoom" && role != "RecRoom")))
            {
                continue;
            }
            var cells = r.CellCount is not null ? Known((int)r.CellCount.GetValueOrDefault()) : Unknown<int>();
            var quality = Unknown<RoomQuality>();
            var sk = TemperatureRoomsReads.RoomStat(r, "Space").TryGetValue(out var s);
            var bk = TemperatureRoomsReads.RoomStat(r, "Beauty").TryGetValue(out var b);
            var ck = TemperatureRoomsReads.RoomStat(r, "Cleanliness").TryGetValue(out var c);
            var wk = TemperatureRoomsReads.RoomStat(r, "Wealth").TryGetValue(out var w);
            var ik = TemperatureRoomsReads.RoomStat(r, "Impressiveness").TryGetValue(out var i);
            if (sk && bk && ck && wk && ik)
            {
                quality = Known(new RoomQuality(s, b, c, w, i));
            }
            rows.Add(new UpkeepRoom { Id = r.Id ?? "", Role = role, Quality = quality, Cells = cells, Beds = EquatableArray.From(new List<string>(held)) });
        }
        return Known(EquatableArray.From(rows));
    }
}
