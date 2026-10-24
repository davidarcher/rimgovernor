

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
    public static Fact<CombatSiteExit> CombatSiteExitOf(WorldProgressionRead? read, MapId mapId)
    {
        foreach (var site in read?.Sites ?? [])
        {
            if (site.Place is O.SitePlace.Loaded loaded && loaded.Map.Id == mapId)
            {
                return Known(loaded.Map.Extraction is O.QuestSiteExtraction.Crew crew
                    ? new CombatSiteExit { Crew = crew.Value.Crew, Cells = crew.Value.ExitCells }
                    : new CombatSiteExit { Crew = [], Cells = [] });
            }
        }
        return Unknown<CombatSiteExit>();
    }


    internal static Fact<EquatableArray<QuestDeparturePawn>> ProjectDepartureStrength(Fact<EquatableArray<QuestDeparturePawn>> candidates, Fact<EquatableArray<SquadDefenderFacts>> defenders)
    {
        if (!candidates.TryGetValue(out var candidateRows))
        {
            return Unknown<EquatableArray<QuestDeparturePawn>>();
        }
        var rows = new List<QuestDeparturePawn>(candidateRows);
        var byId = new Dictionary<PawnId, SquadDefenderFacts>();
        if (defenders.TryGetValue(out var fighters))
        {
            foreach (var pawn in fighters)
            {
                byId[pawn.Id] = pawn;
            }
        }
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].CanFight.TryGetValue(out var fighting) && !fighting)
            {
                rows[i] = rows[i] with { DefensePoints = Known(0.0) };
                continue;
            }
            if (!byId.TryGetValue(new PawnId(rows[i].Id.Value), out var pawn))
            {
                continue;
            }
            if (pawn.MeleePower.TryGetValue(out var melee) && pawn.RangedDPS.TryGetValue(out var ranged))
            {
                rows[i] = rows[i] with { DefensePoints = Known((melee + ranged) * DefenseCapacityPolicy.DefensePointsPerDps) };
            }
        }
        return Known(EquatableArray.From(rows));
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
        var ids = QuestHospitality.ProtectedQuestGuestIds(f.QuestOffers);
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
        var refugees = QuestRefugees.QuestRefugeeIds(f.QuestOffers);
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


    internal static (Fact<EquatableArray<QuestWorkCapacity>> Capacity, Fact<EquatableArray<QuestDeparturePawn>> Departures) FrameQuestWorkerCapacity(WorldProgressionRead? read, MapId current)
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
            if (m.QuestWorkers.Count != m.Pawns.Count)
            {
                return unknown;
            }
            var capacity = new List<QuestWorkCapacity>();
            var departures = new List<QuestDeparturePawn>();
            var present = new HashSet<string>(m.Pawns.Select(pawn => pawn.Pawn.Id), StringComparer.Ordinal);
            foreach (var p in m.QuestWorkers)
            {
                if (!present.Contains((p.PawnId ?? "")))
                {
                    return unknown;
                }
                var rates = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var rate in p.Rates)
                {
                    rates[(rate.Stat ?? "")] = rate.Rate.GetValueOrDefault();
                }
                var row = new QuestWorkCapacity { Pawn = new PawnId((p.PawnId ?? "")), HealthyAdult = ColonyDecodeReads.Optional(p.HealthyAdult is not null, p.HealthyAdult.GetValueOrDefault()), Rates = rates };
                capacity.Add(row);
                departures.Add(new QuestDeparturePawn
                {
                    Id = row.Pawn,
                    HealthyAdult = row.HealthyAdult,
                    CanFight = ColonyDecodeReads.Optional(p.CanFight is not null, p.CanFight.GetValueOrDefault()),
                    CarryCapacity = ColonyDecodeReads.Optional(p.CarryCapacity is not null, p.CarryCapacity.GetValueOrDefault()),
                    CarriedMass = ColonyDecodeReads.Optional(p.CarriedMass is not null, p.CarriedMass.GetValueOrDefault()),
                    SocialLevel = ColonyDecodeReads.Optional(p.SocialLevel is not null, p.SocialLevel.GetValueOrDefault()),
                    FactionLeader = ColonyDecodeReads.Optional(p.FactionLeader is not null, p.FactionLeader.GetValueOrDefault()),
                    NegotiationAbility = ColonyDecodeReads.Optional(p.NegotiationAbility is not null, p.NegotiationAbility.GetValueOrDefault()),
                });
            }
            return (Known(EquatableArray.From(capacity)), Known(EquatableArray.From(departures)));
        }
        return unknown;
    }

    internal static Fact<QuestWorkload> QuestWorkloadOf(O.QuestWorkload? w, O.QuestObjectiveKind kind, DefinitionCatalog? catalog)
    {
        if (w is null)
        {
            return Unknown<QuestWorkload>();
        }
        var work = WorkType.PlantCutting;
        if (kind == O.QuestObjectiveKind.ProduceItem)
        {
            if (catalog is null)
            {
                return Unknown<QuestWorkload>();
            }
            try
            {
                var facts = catalog.RecipeFacts();
                var (billWork, billKnown) = facts.BillWorkOf((w.Recipe ?? ""));
                work = billWork;
                if (!billKnown)
                {
                    return Unknown<QuestWorkload>();
                }
            }
            catch (DomainException)
            {
                return Unknown<QuestWorkload>();
            }
        }
        return Known(new QuestWorkload { Work = work, Stat = w.Stat ?? "", Amount = w.Work.GetValueOrDefault(), RateFactor = w.RateFactor.GetValueOrDefault() });
    }
}
