

namespace RimGovernor.Brain.Observation;

public static class QuestReads
{

    internal static Fact<int> FrameQuestColonistsAtHome(WorldProgressionRead? read, MapId home, EmergencyFacts emergency)
    {
        if (read is null || !emergency.AllSeen)
        {
            return Unknown<int>();
        }
        var away = new HashSet<PawnId>(read.Caravans.SelectMany(caravan => caravan.Crew()));
        foreach (var row in read.Maps)
        {
            if (new MapId(row.Id) != home || !row.Home)
            {
                continue;
            }
            var present = new HashSet<PawnId>(row.Colonists());
            var n = 0;
            foreach (var pawn in emergency.Colonists)
            {
                if (!present.Contains(pawn.Id) || away.Contains(pawn.Id))
                {
                    continue;
                }
                if (!pawn.Dead)
                {
                    n++;
                }
            }
            return Known(n);
        }
        return Unknown<int>();
    }


    /// <summary>Preserves the native exact exit cells from the same combat frame. No remote map selection or additional observation is performed.</summary>
    /// <summary>The loaded quest site's exit on this map: its crew and exit cells, empty without a crew extraction; none when the map is no loaded site.</summary>
    public static CombatSiteExit? CombatSiteExitOf(WorldProgressionRead? read, MapId mapId)
    {
        foreach (var site in read?.Sites ?? [])
        {
            if (site.Place is O.SitePlace.Loaded loaded && loaded.Map.Id == mapId)
            {
                return loaded.Map.Extraction is O.QuestSiteExtraction.Crew crew
                    ? new CombatSiteExit { Crew = crew.Value.Crew, Cells = crew.Value.ExitCells }
                    : new CombatSiteExit { Crew = [], Cells = [] };
            }
        }
        return null;
    }


    internal static Fact<EquatableArray<ExpeditionTrip>> FrameExpeditionTrips(WorldProgressionRead? read, MapId home)
    {
        if (read is null)
        {
            return Unknown<EquatableArray<ExpeditionTrip>>();
        }
        var rows = new List<ExpeditionTrip>();
        foreach (var caravan in read.Caravans)
        {
            rows.Add(new ExpeditionTrip.OnWorld { PawnIds = caravan.Crew(), Tile = caravan.Tile, Course = caravan.Course, HomeRoutes = [.. caravan.HomeRoutes] });
        }
        foreach (var assembly in read.Assemblies)
        {
            if (new MapId(assembly.MapId) != home)
            {
                continue;
            }
            rows.Add(new ExpeditionTrip.Forming { PawnIds = [.. assembly.Pawns] });
        }
        return Known(EquatableArray.From(rows));
    }


    /// <summary>Derives the open meeting's protection on each read; rescue and ordinary tending stay available when operations are excluded.</summary>
    internal static RoundsFacts StampQuestGuestProtection(RoundsFacts f)
    {
        var ids = QuestHospitality.ProtectedQuestGuestIds(f);
        if (f.Population.TryGetValue(out var census))
        {
            var rows = new List<CustodyFacts>(census.Custody.Count);
            foreach (var row in census.Custody)
            {
                rows.Add(row with { QuestProtected = ids.Contains(row.Pawn) });
            }
            f = f with { Population = Known(census with { Custody = EquatableArray.From(rows) }) };
        }
        if (f.MedicalPawns.TryGetValue(out var medical))
        {
            var rows = new List<CarePawn>(medical.Count);
            foreach (var row in medical)
            {
                rows.Add(row with { QuestProtected = ids.Contains(row.Id) });
            }
            f = f with { MedicalPawns = Known(EquatableArray.From(rows)) };
        }
        return f;
    }


    internal static RoundsFacts StampQuestRefugees(RoundsFacts f)
    {
        var refugees = QuestRefugees.QuestRefugeeIds(f);
        if (f.Population.TryGetValue(out var census))
        {
            var rows = new List<CustodyFacts>(census.Custody.Count);
            foreach (var row in census.Custody)
            {
                rows.Add(row with { QuestRefugee = refugees.TryGetValue(row.Pawn, out var refugeeQuest) && refugeeQuest.Value != "" });
            }
            f = f with { Population = Known(census with { Custody = EquatableArray.From(rows) }) };
        }
        return f;
    }


    /// <summary>
    /// The current map's quest workers and departure rows. A departure row's defense points are the pawn's melee
    /// power plus ranged DPS in raid points from the defender roster, 0 for a pawn that cannot fight; an unread roster,
    /// or a fighter it does not list, leaves the departures unread.
    /// </summary>
    internal static (Fact<EquatableArray<QuestWorkCapacity>> Capacity, Fact<EquatableArray<QuestDeparturePawn>> Departures) FrameQuestWorkerCapacity(WorldProgressionRead? read, MapId current, Fact<EquatableArray<DefenderStrength>> defenders)
    {
        var unknown = (Unknown<EquatableArray<QuestWorkCapacity>>(), Unknown<EquatableArray<QuestDeparturePawn>>());
        if (read is null)
        {
            return unknown;
        }
        foreach (var m in read.Maps)
        {
            if (new MapId(m.Id) != current)
            {
                continue;
            }
            // Native reads the map's pawns and its quest workers from the same FreeColonistsSpawned list.
            var capacity = new List<QuestWorkCapacity>();
            var departures = new List<QuestDeparturePawn>();
            var byId = defenders.TryGetValue(out var fighters) ? fighters.ToDictionary(f => f.Id) : null;
            var departuresRead = byId is not null;
            foreach (var p in m.QuestWorkers)
            {
                capacity.Add(new QuestWorkCapacity { Pawn = p.PawnId, HealthyAdult = p.HealthyAdult, Rates = p.Rates.ToDictionary(r => r.Stat, r => r.Rate, StringComparer.Ordinal) });
                var points = 0.0;
                if (p.CanFight)
                {
                    if (byId is null || !byId.TryGetValue(new PawnId(p.PawnId.Value), out var pawn))
                    {
                        departuresRead = false;
                        continue;
                    }
                    points = (pawn.MeleePower + pawn.RangedDps) * DefenseCapacityPolicy.DefensePointsPerDps;
                }
                departures.Add(new QuestDeparturePawn
                {
                    Id = p.PawnId,
                    HealthyAdult = p.HealthyAdult,
                    CanFight = p.CanFight,
                    DefensePoints = points,
                    CarryCapacity = p.CarryCapacity,
                    CarriedMass = p.CarriedMass,
                    FactionLeader = p.FactionLeader,
                    Negotiation = p.Negotiation,
                });
            }
            return (Known(EquatableArray.From(capacity)), departuresRead ? Known(EquatableArray.From(departures)) : unknown.Item2);
        }
        return unknown;
    }
}
