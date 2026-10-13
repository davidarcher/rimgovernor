

namespace RimGovernor.Brain.Observation;

public static partial class ObservationPackage
{
    // quest_capacity.go

    internal static Fact<int> FrameQuestColonistsAtHome(WorldProgressionRead? read, MapId home, EmergencyFacts emergency)
    {
        if (read is null || !emergency.ColonistsComplete.TryGetValue(out var complete) || !complete)
        {
            return Unknown<int>();
        }
        var away = new HashSet<string>(StringComparer.Ordinal);
        foreach (var caravan in read.Caravans)
        {
            foreach (var id in caravan.PawnIds)
            {
                away.Add(id);
            }
        }
        foreach (var row in read.Maps)
        {
            if (new MapId(row.Id) != home || !row.Home)
            {
                continue;
            }
            var present = new HashSet<string>(row.PawnIds, StringComparer.Ordinal);
            var n = 0;
            foreach (var pawn in emergency.Colonists)
            {
                if (!present.Contains(pawn.Id.Value) || away.Contains(pawn.Id.Value))
                {
                    continue;
                }
                if (!pawn.Dead.TryGetValue(out var dead))
                {
                    return Unknown<int>();
                }
                if (!dead)
                {
                    n++;
                }
            }
            return Known(n);
        }
        return Unknown<int>();
    }

    // quest_combat_exit.go

    /// <summary>Preserves the native exact exit cells from the same combat frame. No remote map selection or additional observation is performed.</summary>
    public static Fact<CombatSiteExit> CombatSiteExitOf(WorldProgressionRead? read, MapId mapId)
    {
        if (!FrameWorldSites(read).TryGetValue(out var sites))
        {
            return Unknown<CombatSiteExit>();
        }
        foreach (var site in sites)
        {
            if (!site.Map.TryGetValue(out var mapped) || mapped != mapId)
            {
                continue;
            }
            if (!site.Extraction.TryGetValue(out var extraction))
            {
                return Unknown<CombatSiteExit>();
            }
            return Known(new CombatSiteExit { Crew = EquatableArray.From(extraction.Crew), Cells = EquatableArray.From(extraction.ExitCells) });
        }
        return Unknown<CombatSiteExit>();
    }

    // quest_departure.go

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
                rows[i] = rows[i] with { DefensePoints = Known((melee + ranged) * PolicyFunctions.DefensePointsPerDps) };
            }
        }
        return Known(EquatableArray.From(rows));
    }

    // quest_expedition.go

    internal static Fact<EquatableArray<ExpeditionTrip>> FrameExpeditionTrips(WorldProgressionRead? read, MapId home)
    {
        if (read is null)
        {
            return Unknown<EquatableArray<ExpeditionTrip>>();
        }
        var rows = new List<ExpeditionTrip>();
        foreach (var caravan in read.Caravans)
        {
            var pawns = new List<PawnId>();
            foreach (var id in caravan.PawnIds)
            {
                pawns.Add(new PawnId(id));
            }
            var routes = new List<SiteHomeRoute>();
            foreach (var route in caravan.HomeRoutes)
            {
                routes.Add(new SiteHomeRoute
                {
                    Tile = route.DestinationTile,
                    Reachable = Known(route.Reachable),
                    TravelTicks = route.EstimatedTicksKnown ? Known((long)route.EstimatedTicks) : Unknown<long>(),
                });
            }
            rows.Add(new ExpeditionTrip
            {
                Tile = Known(caravan.Tile),
                Destination = caravan.Destination is { } destination ? Known(destination) : Unknown<int>(),
                PawnIds = pawns,
                HomeRoutes = routes,
            });
        }
        foreach (var assembly in read.Assemblies)
        {
            if (new MapId(assembly.MapId) != home)
            {
                continue;
            }
            var pawns = new List<PawnId>();
            foreach (var id in assembly.PawnIds)
            {
                pawns.Add(new PawnId(id));
            }
            rows.Add(new ExpeditionTrip { Forming = true, PawnIds = pawns });
        }
        return Known(EquatableArray.From(rows));
    }

    // quest_guest_protection.go

    /// <summary>Derives the open meeting's protection on each read; rescue and ordinary tending stay available when operations are excluded.</summary>
    internal static RoundsFacts StampQuestGuestProtection(RoundsFacts f)
    {
        var ids = PolicyFunctions.ProtectedQuestGuestIds(f.QuestOffers);
        if (f.Custody.TryGetValue(out var custody))
        {
            var rows = new List<CustodyFacts>(custody.Count);
            foreach (var row in custody)
            {
                rows.Add(row with { QuestProtected = ids.Contains(row.Pawn) });
            }
            f = f with { Custody = Known(EquatableArray.From(rows)) };
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

    // quest_refugees.go

    internal static RoundsFacts StampQuestRefugees(RoundsFacts f)
    {
        var refugees = PolicyFunctions.QuestRefugeeIds(f.QuestOffers);
        if (f.Custody.TryGetValue(out var custody))
        {
            var rows = new List<CustodyFacts>(custody.Count);
            foreach (var row in custody)
            {
                rows.Add(row with { QuestRefugee = refugees.TryGetValue(row.Pawn, out var refugeeQuest) && refugeeQuest.Value != "" });
            }
            f = f with { Custody = Known(EquatableArray.From(rows)) };
        }
        return f;
    }

    // quest_hack_gift.go

    internal static QuestObjective QuestHackGift(QuestObjectiveFact source, QuestObjective row)
    {
        var targets = new List<QuestHackTarget>();
        foreach (var t in source.HackTargets)
        {
            var eligible = new List<PawnId>(t.EligiblePawnIds.Count);
            foreach (var id in t.EligiblePawnIds)
            {
                eligible.Add(new PawnId(id));
            }
            targets.Add(new QuestHackTarget
            {
                Id = t.Id ?? "",
                Map = new MapId(t.MapId.GetValueOrDefault()),
                Spawned = Optional(t.Spawned is not null, t.Spawned.GetValueOrDefault()),
                Hacked = Optional(t.Hackable?.Hacked is not null, t.Hackable?.Hacked ?? default),
                LockedOut = Optional(t.Hackable?.LockedOut is not null, t.Hackable?.LockedOut ?? default),
                Autohack = Optional(t.Hackable?.Autohack is not null, t.Hackable?.Autohack ?? default),
                ProgressPercent = Optional(t.Hackable?.ProgressPercent is not null, t.Hackable?.ProgressPercent ?? default),
                Defence = Optional(t.Hackable?.Defence is not null, t.Hackable?.Defence ?? default),
                EligiblePawnIds = eligible,
                Satisfied = Optional(t.Satisfied is not null, t.Satisfied.GetValueOrDefault()),
            });
        }
        row = row with { HackTargets = [.. row.HackTargets, .. targets] };
        if (source.HackRisk is { } risk)
        {
            row = row with { HackRisk = Known(new QuestHackRisk { FactionId = risk.FactionId ?? "", Hostile = Optional(risk.Hostile is not null, risk.Hostile.GetValueOrDefault()) }) };
        }
        if (source.Gift is { } g)
        {
            // A pending recipient has no dispatch map; retain an unknown request until arrival.
            if (g.MapId is null)
            {
                return row;
            }
            var gift = new QuestGiftRequest
            {
                Recipient = new PawnId((g.RecipientId ?? "")),
                Def = g.Def ?? "",
                Remaining = Optional(g.Remaining is not null, (long)g.Remaining.GetValueOrDefault()),
                Map = new MapId(g.MapId.GetValueOrDefault()),
                PawnIds = [.. g.PawnIds.Select(id => new PawnId(id))],
                HaulingPawnIds = [.. g.HaulingPawnIds.Select(id => new PawnId(id))],
                EligiblePawnIds = [.. g.EligiblePawnIds.Select(id => new PawnId(id))],
            };
            row = row with { Gift = Known(gift) };
        }
        return row;
    }

    // quest_monument.go

    internal static Fact<QuestMonument> QuestMonumentOf(O.QuestMonument? m, DefinitionCatalog? catalog)
    {
        if (m is null)
        {
            return Unknown<QuestMonument>();
        }
        var pieces = new List<QuestMonumentPiece>(m.Pieces.Count);
        foreach (var p in m.Pieces)
        {
            pieces.Add(new QuestMonumentPiece
            {
                Def = p.DefName ?? "",
                Stuff = p.Stuff ?? "",
                Offset = CellOf(p.Offset),
                Rotation = (Rotation)p.Rotation.GetValueOrDefault(),
                Built = Optional(p.Built is not null, p.Built.GetValueOrDefault()),
                Queued = Optional(p.Queued is not null, p.Queued.GetValueOrDefault()),
                Allowed = Optional(p.Allowed is not null, p.Allowed.GetValueOrDefault()),
            });
        }
        for (var i = 0; i < m.Pieces.Count; i++)
        {
            var p = m.Pieces[i];
            // The stuffs and build options come from the catalog rows; a piece the catalog cannot answer for
            // leaves the whole monument unknown.
            if (catalog is null)
            {
                return Unknown<QuestMonument>();
            }
            List<string> allowed;
            List<QuestMonumentBuildOption> options;
            try
            {
                (allowed, options) = MonumentPieceOptions(catalog, (p.DefName ?? ""), (p.Stuff ?? ""));
            }
            catch (DomainException)
            {
                return Unknown<QuestMonument>();
            }
            pieces[i] = pieces[i] with { AllowedStuffs = allowed, BuildOptions = options, Footprint = CellsOf(p.Footprint) };
        }
        var resources = new List<QuestMonumentResource>(m.Resources.Count);
        foreach (var r in m.Resources)
        {
            resources.Add(new QuestMonumentResource
            {
                Id = r.Id ?? "",
                Def = r.DefName ?? "",
                Cell = CellOf(r.Cell),
                InStorage = Optional(r.InStorage is not null, r.InStorage.GetValueOrDefault()),
                Haulers = [.. r.EligibleHaulers.Select(pawn => new PawnId(pawn))],
            });
        }
        var supplied = new List<Amount>(m.SuppliedResources.Count);
        foreach (var r in m.SuppliedResources)
        {
            supplied.Add(new Amount(new Resource((r.DefName ?? "")), r.Units.GetValueOrDefault()));
        }
        var available = new List<Amount>(m.AvailableResources.Count);
        foreach (var r in m.AvailableResources)
        {
            available.Add(new Amount(new Resource((r.DefName ?? "")), r.Units.GetValueOrDefault()));
        }
        return Known(new QuestMonument
        {
            Marker = m.MarkerId ?? "",
            Def = m.DefName ?? "",
            Map = new MapId(m.MapId.GetValueOrDefault()),
            Packed = Optional(m.Packed is not null, m.Packed.GetValueOrDefault()),
            Installed = Optional(m.Installed is not null, m.Installed.GetValueOrDefault()),
            Complete = Optional(m.Complete is not null, m.Complete.GetValueOrDefault()),
            AllDone = Optional(m.AllDone is not null, m.AllDone.GetValueOrDefault()),
            DisallowedBuilding = m.DisallowedBuildingId ?? "",
            DisallowedTicks = Optional(m.DisallowedTicks is not null, (long)m.DisallowedTicks.GetValueOrDefault()),
            Offered = Optional(m.Offered is not null, m.Offered.GetValueOrDefault()),
            ClearSite = Optional(m.ClearSite is not null, m.ClearSite.GetValueOrDefault()),
            SuppliedResources = supplied,
            AvailableResources = available,
            Cell = m.Cell is null ? default : CellOf(m.Cell),
            InstallCells = CellsOf(m.InstallCells),
            Pieces = pieces,
            Resources = resources,
        });
    }

    // quest_monument_options.go

    /// <summary>
    /// Reads a monument piece's stuffs and build options from the catalog rows, the way
    /// MonumentMarker.AllowedStuffsFor and the sketch buildable do: a def made from stuff with none chosen offers
    /// every stuff that can make it, one chosen offers only it, and a def not made from stuff has the one option
    /// with no stuff. The piece names a thing or a terrain def; a name both tables hold, or neither does, is an
    /// error.
    /// </summary>
    private static (List<string> Allowed, List<QuestMonumentBuildOption> Options) MonumentPieceOptions(DefinitionCatalog catalog, string def, string stuff)
    {
        var thing = catalog.FindThing(def);
        var terrain = catalog.FindTerrain(def);
        if ((thing is null) == (terrain is null))
        {
            throw new DomainException($"monument piece {def} is not exactly one of a thing def and a terrain def");
        }
        var madeFromStuff = thing is not null ? (thing.stuffCategories?.Count ?? 0) > 0 : (terrain?.stuffCategories?.Count ?? 0) > 0;
        IReadOnlyList<string> stuffs = [""];
        var allowed = new List<string>();
        if (madeFromStuff)
        {
            stuffs = [stuff];
            if (stuff == "")
            {
                if (thing is null)
                {
                    throw new DomainException($"monument terrain piece {def} is made from stuff with none chosen");
                }
                stuffs = catalog.AllowedStuffs(def);
                allowed.AddRange(stuffs);
            }
        }
        var options = new List<QuestMonumentBuildOption>();
        foreach (var s in stuffs)
        {
            var option = new QuestMonumentBuildOption { Stuff = s };
            IReadOnlyList<StatCost> costs;
            if (thing is not null)
            {
                option = option with { Work = (float)catalog.StatValue(def, s, StatWorkToBuild) };
                costs = catalog.AdjustedCosts(def, s);
            }
            else
            {
                option = option with { Work = (float)catalog.TerrainWorkToBuild(def) };
                costs = catalog.TerrainAdjustedCosts(def);
            }
            var amounts = new List<Amount>(costs.Count);
            foreach (var cost in costs)
            {
                amounts.Add(new Amount(new Resource(cost.Def), cost.Units));
            }
            options.Add(option with { Costs = amounts });
        }
        return (allowed, options);
    }

    // quest_work_capacity.go

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
            if (m.QuestWorkers.Count != m.PawnIds.Count)
            {
                return unknown;
            }
            var capacity = new List<QuestWorkCapacity>();
            var departures = new List<QuestDeparturePawn>();
            var present = new HashSet<string>(m.PawnIds, StringComparer.Ordinal);
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
                var row = new QuestWorkCapacity { Pawn = new PawnId((p.PawnId ?? "")), HealthyAdult = Optional(p.HealthyAdult is not null, p.HealthyAdult.GetValueOrDefault()), Rates = rates };
                capacity.Add(row);
                departures.Add(new QuestDeparturePawn
                {
                    Id = row.Pawn,
                    HealthyAdult = row.HealthyAdult,
                    CanFight = Optional(p.CanFight is not null, p.CanFight.GetValueOrDefault()),
                    CarryCapacity = Optional(p.CarryCapacity is not null, p.CarryCapacity.GetValueOrDefault()),
                    CarriedMass = Optional(p.CarriedMass is not null, p.CarriedMass.GetValueOrDefault()),
                    SocialLevel = Optional(p.SocialLevel is not null, p.SocialLevel.GetValueOrDefault()),
                    FactionLeader = Optional(p.FactionLeader is not null, p.FactionLeader.GetValueOrDefault()),
                    NegotiationAbility = Optional(p.NegotiationAbility is not null, p.NegotiationAbility.GetValueOrDefault()),
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
