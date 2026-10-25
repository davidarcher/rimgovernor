

namespace RimGovernor.Brain.Observation;

public static class QuestReads
{

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


    /// <summary>Every caravan on the world map and every caravan being formed on the home map.</summary>
    internal static EquatableArray<ExpeditionTrip> FrameExpeditionTrips(WorldProgressionRead read, MapId home)
    {
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
        return EquatableArray.From(rows);
    }


    /// <summary>Derives the open meeting's protection on each read; rescue and ordinary tending stay available when operations are excluded.</summary>
    internal static RoundsFacts StampQuestGuestProtection(RoundsFacts f)
    {
        var ids = QuestHospitality.ProtectedQuestGuestIds(f);
        var custody = new List<CustodyFacts>(f.Population.Custody.Count);
        foreach (var row in f.Population.Custody)
        {
            custody.Add(row with { QuestProtected = ids.Contains(row.Pawn) });
        }
        f = f with { Population = f.Population with { Custody = EquatableArray.From(custody) } };
        {
            var medical = f.MedicalPawns;
            var rows = new List<CarePawn>(medical.Count);
            foreach (var row in medical)
            {
                rows.Add(row with { QuestProtected = ids.Contains(row.Id) });
            }
            f = f with { MedicalPawns = EquatableArray.From(rows) };
        }
        return f;
    }


    internal static RoundsFacts StampQuestRefugees(RoundsFacts f)
    {
        var refugees = QuestRefugees.QuestRefugeeIds(f);
        var custody = new List<CustodyFacts>(f.Population.Custody.Count);
        foreach (var row in f.Population.Custody)
        {
            custody.Add(row with { QuestRefugee = refugees.TryGetValue(row.Pawn, out var refugeeQuest) && refugeeQuest.Value != "" });
        }
        return f with { Population = f.Population with { Custody = EquatableArray.From(custody) } };
    }


    /// <summary>
    /// The current map's quest workers and the pawn census built on them. A departure row's defense points are the pawn's melee
    /// power plus ranged DPS in raid points from the defender roster, 0 for a pawn that cannot fight. The census is unseen when
    /// the map is not the home map, the emergency census is incomplete, the work pawns are unread, or the defender roster
    /// misses a fighter.
    /// </summary>
    internal static (EquatableArray<QuestWorkCapacity> Capacity, QuestPawns Pawns) FrameQuestPawns(WorldProgressionRead read, MapId current, EmergencyFacts emergency, Fact<EquatableArray<DefenderStrength>> defenders, WorkPawnsRead workPawns)
    {
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
            var seen = byId is not null && emergency.AllSeen && m.Home;
            foreach (var p in m.QuestWorkers)
            {
                capacity.Add(new QuestWorkCapacity { Pawn = p.PawnId, HealthyAdult = p.HealthyAdult, Rates = p.Rates.ToDictionary(r => r.Stat, r => r.Rate, StringComparer.Ordinal) });
                var points = 0.0;
                if (p.CanFight)
                {
                    if (byId is null || !byId.TryGetValue(new PawnId(p.PawnId.Value), out var pawn))
                    {
                        seen = false;
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
            if (!seen)
            {
                return (EquatableArray.From(capacity), new QuestPawns.Unseen());
            }
            var workers = workPawns.Pawns;
            return (EquatableArray.From(capacity), new QuestPawns.Seen { AtHome = ColonistsAtHome(read, m, emergency), Departures = EquatableArray.From(departures), Workers = workers });
        }
        return ([], new QuestPawns.Unseen());
    }

    /// <summary>The living colonists on the home map who are not away with a caravan.</summary>
    private static int ColonistsAtHome(WorldProgressionRead read, O.WorldMap row, EmergencyFacts emergency)
    {
        var away = new HashSet<PawnId>(read.Caravans.SelectMany(caravan => caravan.Crew()));
        var present = new HashSet<PawnId>(row.Colonists());
        var n = 0;
        foreach (var pawn in emergency.Colonists)
        {
            if (present.Contains(pawn.Id) && !away.Contains(pawn.Id) && !pawn.Dead)
            {
                n++;
            }
        }
        return n;
    }
}
