using DomainPawnId = RimGovernor.Brain.Domain.PawnId;

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
            foreach (var id in caravan.PawnIDs)
            {
                away.Add(id);
            }
        }
        foreach (var row in read.Maps)
        {
            if (new MapId(row.ID) != home || !row.Home)
            {
                continue;
            }
            var present = new HashSet<string>(row.PawnIDs, StringComparer.Ordinal);
            var n = 0;
            foreach (var pawn in emergency.Colonists)
            {
                if (!present.Contains(pawn.ID.Value) || away.Contains(pawn.ID.Value))
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
            return Known(new CombatSiteExit { Crew = extraction.Crew, Cells = extraction.ExitCells });
        }
        return Unknown<CombatSiteExit>();
    }

    // quest_departure.go

    internal static Fact<IReadOnlyList<QuestDeparturePawn>> ProjectDepartureStrength(Fact<IReadOnlyList<QuestDeparturePawn>> candidates, Fact<IReadOnlyList<SquadDefenderFacts>> defenders)
    {
        if (!candidates.TryGetValue(out var candidateRows))
        {
            return Unknown<IReadOnlyList<QuestDeparturePawn>>();
        }
        var rows = new List<QuestDeparturePawn>(candidateRows);
        var byId = new Dictionary<DomainPawnId, SquadDefenderFacts>();
        if (defenders.TryGetValue(out var fighters))
        {
            foreach (var pawn in fighters)
            {
                byId[pawn.ID] = pawn;
            }
        }
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].CanFight.TryGetValue(out var fighting) && !fighting)
            {
                rows[i] = rows[i] with { DefensePoints = Known(0.0) };
                continue;
            }
            if (!byId.TryGetValue(new DomainPawnId(rows[i].ID.Value), out var pawn))
            {
                continue;
            }
            if (pawn.MeleePower.TryGetValue(out var melee) && pawn.RangedDPS.TryGetValue(out var ranged))
            {
                rows[i] = rows[i] with { DefensePoints = Known((melee + ranged) * PolicyFunctions.DefensePointsPerDPS) };
            }
        }
        return Known<IReadOnlyList<QuestDeparturePawn>>(rows);
    }

    // quest_expedition.go

    internal static Fact<IReadOnlyList<ExpeditionTrip>> FrameExpeditionTrips(WorldProgressionRead? read, MapId home)
    {
        if (read is null)
        {
            return Unknown<IReadOnlyList<ExpeditionTrip>>();
        }
        var rows = new List<ExpeditionTrip>();
        foreach (var caravan in read.Caravans)
        {
            var pawns = new List<DomainPawnId>();
            foreach (var id in caravan.PawnIDs)
            {
                pawns.Add(new DomainPawnId(id));
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
                Destination = Optional(caravan.HasDestination, caravan.Destination),
                PawnIDs = pawns,
                HomeRoutes = routes,
            });
        }
        foreach (var assembly in read.Assemblies)
        {
            if (new MapId(assembly.MapID) != home)
            {
                continue;
            }
            var pawns = new List<DomainPawnId>();
            foreach (var id in assembly.PawnIDs)
            {
                pawns.Add(new DomainPawnId(id));
            }
            rows.Add(new ExpeditionTrip { Forming = true, PawnIDs = pawns });
        }
        return Known<IReadOnlyList<ExpeditionTrip>>(rows);
    }

    // quest_guest_protection.go

    /// <summary>Derives the open meeting's protection on each read; rescue and ordinary tending stay available when operations are excluded.</summary>
    internal static RoundsFacts StampQuestGuestProtection(RoundsFacts f)
    {
        var ids = PolicyFunctions.ProtectedQuestGuestIDs(f.QuestOffers);
        if (f.Custody.TryGetValue(out var custody))
        {
            var rows = new List<CustodyPawn>(custody.Count);
            foreach (var row in custody)
            {
                rows.Add(row with { QuestProtected = ids.Contains(row.Pawn) });
            }
            f = f with { Custody = Known(EquatableArray.From(rows)) };
        }
        if (f.MedicalPawns.TryGetValue(out var medical))
        {
            var rows = new List<MedicalPawn>(medical.Count);
            foreach (var row in medical)
            {
                rows.Add(row with { QuestProtected = ids.Contains(new DomainPawnId(row.ID.Value)) });
            }
            f = f with { MedicalPawns = Known(EquatableArray.From(rows)) };
        }
        return f;
    }

    // quest_refugees.go

    internal static RoundsFacts StampQuestRefugees(RoundsFacts f)
    {
        var refugees = PolicyFunctions.QuestRefugeeIDs(f.QuestOffers);
        if (f.Custody.TryGetValue(out var custody))
        {
            var rows = new List<CustodyPawn>(custody.Count);
            foreach (var row in custody)
            {
                rows.Add(row with { QuestRefugee = refugees.GetValueOrDefault(row.Pawn, "") != "" });
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
            var eligible = new List<DomainPawnId>(t.EligiblePawnIds.Count);
            foreach (var id in t.EligiblePawnIds)
            {
                eligible.Add(new DomainPawnId(id));
            }
            targets.Add(new QuestHackTarget
            {
                Id = t.Id,
                Map = new MapId(t.MapId),
                Spawned = Optional(t.HasSpawned, t.Spawned),
                Hacked = Optional(t.Hackable.HasHacked, t.Hackable.Hacked),
                LockedOut = Optional(t.Hackable.HasLockedOut, t.Hackable.LockedOut),
                Autohack = Optional(t.Hackable.HasAutohack, t.Hackable.Autohack),
                ProgressPercent = Optional(t.Hackable.HasProgressPercent, t.Hackable.ProgressPercent),
                Defence = Optional(t.Hackable.HasDefence, t.Hackable.Defence),
                EligiblePawnIds = eligible,
                Satisfied = Optional(t.HasSatisfied, t.Satisfied),
            });
        }
        row = row with { HackTargets = [.. row.HackTargets, .. targets] };
        if (source.HackRisk is { } risk)
        {
            row = row with { HackRisk = Known(new QuestHackRisk { FactionId = risk.FactionId, Hostile = Optional(risk.HasHostile, risk.Hostile) }) };
        }
        if (source.Gift is { } g)
        {
            // A pending recipient has no dispatch map; retain an unknown request until arrival.
            if (!g.HasMapId)
            {
                return row;
            }
            var gift = new QuestGiftRequest
            {
                Recipient = new DomainPawnId(g.RecipientId),
                Def = g.Def,
                Remaining = Optional(g.HasRemaining, (long)g.Remaining),
                Map = new MapId(g.MapId),
                PawnIds = [.. g.PawnIds.Select(id => new DomainPawnId(id))],
                HaulingPawnIds = [.. g.HaulingPawnIds.Select(id => new DomainPawnId(id))],
                EligiblePawnIds = [.. g.EligiblePawnIds.Select(id => new DomainPawnId(id))],
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
                Def = p.DefName,
                Stuff = p.Stuff,
                Offset = CellOf(p.Offset),
                Rotation = (Rotation)p.Rotation,
                Built = Optional(p.HasBuilt, p.Built),
                Queued = Optional(p.HasQueued, p.Queued),
                Allowed = Optional(p.HasAllowed, p.Allowed),
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
                (allowed, options) = MonumentPieceOptions(catalog, p.DefName, p.Stuff);
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
                Id = r.Id,
                Def = r.DefName,
                Cell = CellOf(r.Cell),
                InStorage = Optional(r.HasInStorage, r.InStorage),
                Haulers = [.. r.EligibleHaulers.Select(pawn => new DomainPawnId(pawn))],
            });
        }
        var supplied = new List<Amount>(m.SuppliedResources.Count);
        foreach (var r in m.SuppliedResources)
        {
            supplied.Add(new Amount(new Resource(r.DefName), r.Units));
        }
        var available = new List<Amount>(m.AvailableResources.Count);
        foreach (var r in m.AvailableResources)
        {
            available.Add(new Amount(new Resource(r.DefName), r.Units));
        }
        return Known(new QuestMonument
        {
            Marker = m.MarkerId,
            Def = m.DefName,
            Map = new MapId(m.MapId),
            Packed = Optional(m.HasPacked, m.Packed),
            Installed = Optional(m.HasInstalled, m.Installed),
            Complete = Optional(m.HasComplete, m.Complete),
            AllDone = Optional(m.HasAllDone, m.AllDone),
            DisallowedBuilding = m.DisallowedBuildingId,
            DisallowedTicks = Optional(m.HasDisallowedTicks, (long)m.DisallowedTicks),
            Offered = Optional(m.HasOffered, m.Offered),
            ClearSite = Optional(m.HasClearSite, m.ClearSite),
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
        var thing = catalog.ThingDef(def);
        var terrain = catalog.DefRow<D.TerrainDef>(def);
        if ((thing is null) == (terrain is null))
        {
            throw new DomainException($"monument piece {def} is not exactly one of a thing def and a terrain def");
        }
        var madeFromStuff = thing is not null ? thing.StuffCategories.Count > 0 : terrain!.StuffCategories.Count > 0;
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
            IReadOnlyList<O.Quantity> costs;
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
                amounts.Add(new Amount(new Resource(cost.DefName), cost.Units));
            }
            options.Add(option with { Costs = amounts });
        }
        return (allowed, options);
    }

    // quest_work_capacity.go

    internal static (Fact<IReadOnlyList<QuestWorkCapacity>> Capacity, Fact<IReadOnlyList<QuestDeparturePawn>> Departures) FrameQuestWorkerCapacity(WorldProgressionRead? read, MapId current)
    {
        var unknown = (Unknown<IReadOnlyList<QuestWorkCapacity>>(), Unknown<IReadOnlyList<QuestDeparturePawn>>());
        if (read is null)
        {
            return unknown;
        }
        foreach (var m in read.Maps)
        {
            if (new MapId(m.ID) != current)
            {
                continue;
            }
            if (m.QuestWorkers.Count != m.PawnIDs.Count)
            {
                return unknown;
            }
            var capacity = new List<QuestWorkCapacity>();
            var departures = new List<QuestDeparturePawn>();
            var present = new HashSet<string>(m.PawnIDs, StringComparer.Ordinal);
            foreach (var p in m.QuestWorkers)
            {
                if (!present.Contains(p.PawnId))
                {
                    return unknown;
                }
                var rates = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var rate in p.Rates)
                {
                    rates[rate.Stat] = rate.Rate;
                }
                var row = new QuestWorkCapacity { Pawn = new PawnId(p.PawnId), HealthyAdult = Optional(p.HasHealthyAdult, p.HealthyAdult), Rates = rates };
                capacity.Add(row);
                departures.Add(new QuestDeparturePawn
                {
                    ID = row.Pawn,
                    HealthyAdult = row.HealthyAdult,
                    CanFight = Optional(p.HasCanFight, p.CanFight),
                    CarryCapacity = Optional(p.HasCarryCapacity, p.CarryCapacity),
                    CarriedMass = Optional(p.HasCarriedMass, p.CarriedMass),
                    SocialLevel = Optional(p.HasSocialLevel, p.SocialLevel),
                    FactionLeader = Optional(p.HasFactionLeader, p.FactionLeader),
                    NegotiationAbility = Optional(p.HasNegotiationAbility, p.NegotiationAbility),
                });
            }
            return (Known<IReadOnlyList<QuestWorkCapacity>>(capacity), Known<IReadOnlyList<QuestDeparturePawn>>(departures));
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
                if (!facts.BillWorkOf(w.Recipe, out work))
                {
                    return Unknown<QuestWorkload>();
                }
            }
            catch (DomainException)
            {
                return Unknown<QuestWorkload>();
            }
        }
        return Known(new QuestWorkload { Work = work, Stat = w.Stat, Amount = w.Work, RateFactor = w.RateFactor });
    }
}
