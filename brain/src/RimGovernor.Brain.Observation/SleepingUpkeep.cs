namespace RimGovernor.Brain.Observation;

public static partial class ObservationPackage
{
    internal static Fact<SleepingObservation> ColonySleeping(O.ColonyFactsSnapshot v, Buildings buildings, DefinitionCatalog? catalog)
    {
        var u = v.Upkeep?.Outcome?.Observed;
        if (u is null || v.ColonistCount is null || HasIssue(u.Issues, "people") || HasIssue(u.Issues, "beds"))
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
                    title = RequireCatalog(catalog).RoyalTitleOf((p.RoyalTitle ?? ""), p.Ascetic.GetValueOrDefault(), p.Precepts);
                }
                output.Add(new SleepingPerson
                {
                    Id = new PawnId(p.Pawn?.Id ?? ""),
                    OwnedBed = Known(p.OwnedBed?.Id ?? ""),
                    ComfortableMin = Optional(p.ComfortableMinC is not null, p.ComfortableMinC.GetValueOrDefault()),
                    ComfortableMax = Optional(p.ComfortableMaxC is not null, p.ComfortableMaxC.GetValueOrDefault()),
                    Partners = Ids(RefIds(p.Partners)),
                    BedSharingAllowed = Optional(p.BedSharingAllowed is not null, p.BedSharingAllowed.GetValueOrDefault()),
                    Title = title,
                });
            }
            return EquatableArray.From(output);
        }
        var r = new SleepingObservation
        {
            Colonists = (int)v.ColonistCount.GetValueOrDefault(),
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
                rest = catalog.PlannerStatValue((head.DefName ?? ""), b.Stuff ?? "", StatBedRestEffectiveness);
            }
            catch (DomainException e)
            {
                throw new BridgeContractException($"sleeping bed {b.Bed?.Id}: {e.Message}");
            }
            beds.Add(new SleepingBed
            {
                Id = b.Bed?.Id ?? "",
                Definition = new Resource((head.DefName ?? "")),
                Humanlike = Known(humanlike),
                Crib = crib,
                Medical = Optional(b.Medical is not null, b.Medical.GetValueOrDefault()),
                Prisoners = Optional(b.Prisoners is not null, b.Prisoners.GetValueOrDefault()),
                Slaves = b.ForSlaves.GetValueOrDefault(),
                Roofed = Optional(b.Roofed is not null, b.Roofed.GetValueOrDefault()),
                RestEffectiveness = Known((double)(float)rest),
                Temperature = Optional(b.TemperatureC is not null, b.TemperatureC.GetValueOrDefault()),
                Owners = Ids(RefIds(b.Owners)),
                Users = Ids(RefIds(b.Users)),
                AccessibleTo = Ids(RefIds(b.AccessibleTo)),
                Room = OptionalRef(b.Room),
                Quality = Optional(b.Quality is not null, b.Quality ?? ""),
                Stuff = Optional(b.Stuff is not null, b.Stuff ?? ""),
                Cell = CellOf(head.Position),
            });
        }
        return Known(r with { Beds = EquatableArray.From(beds) });
    }

    /// <summary>
    /// The room-quality view of the frame's rooms census: every room holding a colonist bed (humanlike, not
    /// medical, not for prisoners) or carrying a common role (dining, rec room) and not fogged, with its native
    /// stats. Unknown when a bed's kind or room is unknown; a room missing any quality stat has unknown quality.
    /// </summary>
    internal static Fact<EquatableArray<UpkeepRoom>> UpkeepRooms(O.RoomsSnapshot? census, EquatableArray<SleepingBed> beds)
    {
        var owned = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var b in beds)
        {
            var hk = b.Humanlike.TryGetValue(out var human);
            var mk = b.Medical.TryGetValue(out var medical);
            var pk = b.Prisoners.TryGetValue(out var prisoners);
            if (!hk || !mk || !pk)
            {
                return Unknown<EquatableArray<UpkeepRoom>>();
            }
            if (!human || medical || prisoners)
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
            var sk = RoomStat(r, "Space").TryGetValue(out var s);
            var bk = RoomStat(r, "Beauty").TryGetValue(out var b);
            var ck = RoomStat(r, "Cleanliness").TryGetValue(out var c);
            var wk = RoomStat(r, "Wealth").TryGetValue(out var w);
            var ik = RoomStat(r, "Impressiveness").TryGetValue(out var i);
            if (sk && bk && ck && wk && ik)
            {
                quality = Known(new RoomQuality(s, b, c, w, i));
            }
            rows.Add(new UpkeepRoom { Id = r.Id ?? "", Role = role, Quality = quality, Cells = cells, Beds = EquatableArray.From(new List<string>(held)) });
        }
        return Known(EquatableArray.From(rows));
    }
}
