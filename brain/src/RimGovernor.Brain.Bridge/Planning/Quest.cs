using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Facts;
using RimGovernor.Brain.Bridge.Contracts;
using O = RimGovernor.Contracts.Wire.Observations;
using PawnId = RimGovernor.Brain.Domain.PawnId;


namespace RimGovernor.Brain.Bridge;

public static class QuestReads
{

    /// <summary>The actual loaded site's extraction crew on this map; none without an able crew or a loaded site.</summary>
    public static O.ExtractionCrew? CombatSiteExitOf(RimGovernor.Contracts.Wire.Observations.WorldProgressionSnapshot? read, MapId mapId)
    {
        foreach (var site in read?.Sites ?? [])
        {
            if (site.Place is O.SitePlace.Loaded loaded && loaded.Map.Id == mapId)
            {
                return loaded.Map.Extraction is O.QuestSiteExtraction.Crew crew
                    ? crew.Value
                    : null;
            }
        }
        return null;
    }


    /// <summary>Every caravan on the world map and every caravan being formed on the home map.</summary>
    internal static EquatableArray<ExpeditionTrip> FrameExpeditionTrips(RimGovernor.Contracts.Wire.Observations.WorldProgressionSnapshot read, MapId home)
    {
        var rows = new List<ExpeditionTrip>();
        foreach (var caravan in read.Caravans)
        {
            rows.Add(new ExpeditionTrip.OnWorld(caravan));
        }
        foreach (var assembly in read.Assemblies)
        {
            if (new MapId(assembly.MapId) != home)
            {
                continue;
            }
            rows.Add(new ExpeditionTrip.Forming(assembly));
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
    internal static QuestPawns FrameQuestPawns(RimGovernor.Contracts.Wire.Observations.WorldProgressionSnapshot read, MapId current, EmergencyFacts emergency, EquatableArray<DefenderStrength> defenders, WorkPawnsRead workPawns)
    {
        foreach (var m in read.Maps)
        {
            if (new MapId(m.Id) != current)
            {
                continue;
            }
            // Native reads the map's pawns and its quest workers from the same FreeColonistsSpawned list.
            var departures = new List<QuestDeparturePawn>();
            var byId = defenders.ToDictionary(f => f.Id);
            var seen = emergency.AllSeen && m.Home;
            foreach (var p in m.QuestWorkers)
            {
                var points = 0.0;
                if (p.CanFight)
                {
                    if (!byId.TryGetValue(new PawnId(p.PawnId.Value), out var pawn))
                    {
                        throw new ObservationContractException($"quest worker missing from defender census: {p.PawnId.Value}");
                    }
                    points = (pawn.MeleePower + pawn.RangedDps) * DefenseCapacityPolicy.DefensePointsPerDps;
                }
                departures.Add(new QuestDeparturePawn
                {
                    Observed = p,
                    DefensePoints = points,
                });
            }
            if (!seen)
            {
                return new QuestPawns.Unseen();
            }
            var workers = workPawns.Pawns;
            return new QuestPawns.Seen { AtHome = ColonistsAtHome(read, m, emergency), Departures = EquatableArray.From(departures), Workers = workers };
        }
        return new QuestPawns.Unseen();
    }

    /// <summary>The living colonists on the home map who are not away with a caravan.</summary>
    private static int ColonistsAtHome(RimGovernor.Contracts.Wire.Observations.WorldProgressionSnapshot read, O.WorldMap row, EmergencyFacts emergency)
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
