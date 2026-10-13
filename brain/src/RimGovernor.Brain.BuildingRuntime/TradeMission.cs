namespace RimGovernor.Brain.BuildingRuntime;

public interface ITradeMissionPackSource
{
    (ObservationsPb.TradeAcquisition Acquisition, BridgeResult Result) ReadTradeAcquisition(CancellationToken ct, CommonPb.Identity identity, OperationsPb.FormCaravanIntent? intent);
}

internal static partial class BuildingRuntimePackage
{
    /// <summary>Whether <paramref name="m"/> passes its own validation (Go: <c>m.Validate() == nil</c>).</summary>
    internal static bool TradeMissionValid(TradeMission m)
    {
        try
        {
            m.Validate();
            return true;
        }
        catch (DomainException)
        {
            return false;
        }
    }
}

/// <summary>The exported trade mission calculations of the package.</summary>
public static partial class TradeMissions
{
    /// <summary>
    /// A read-only exact-pack calculation. The caller feeds its returned option to the existing acquisition
    /// planner. No planner registers automatic departure until the complete purchase/return driver is
    /// installed. Throws <see cref="DomainException"/> with the Go failure reason when the mission cannot be
    /// prepared.
    /// </summary>
    public static (TradeMission Mission, Pol.TradeAcquisitionOption Option) PrepareTradeMission(
        CancellationToken ct, ITradeMissionPackSource source, GenerationSnapshot world, int homeTile, Bridge.SettlementFact settlement, Pol.RoundsFacts facts, Pol.RoundsPolicy thresholds, long silverBudget, IReadOnlyList<CargoItem> demand, IReadOnlyList<Pol.PawnId>? claimed)
    {
        if (!world.IsValid || homeTile < 0 || settlement.Tile == homeTile || settlement.Player || settlement.TraderKind.Length == 0)
        {
            throw new DomainException("invalid_trade_destination");
        }
        if (!settlement.CanTrade.TryGetValue(out var canTrade) || !canTrade)
        {
            throw new DomainException("settlement_unavailable");
        }
        var sk = facts.Silver().TryGetValue(out var silver);
        var rk = Pol.TradeMissionFacts.TradeSilverReserve(facts.Colonists, out var reserve);
        if (!sk || !rk || silverBudget <= 0 || silverBudget > int.MaxValue || silver < reserve || silverBudget > silver - reserve)
        {
            throw new DomainException("insufficient_silver");
        }
        var (crew, reason) = Pol.TradeMissionFacts.TradeMissionCrew(facts, claimed);
        if (reason.Length != 0)
        {
            throw new DomainException(reason);
        }
        if (!Pol.TradeMissionFacts.TradeMissionCarryCapacity(facts, crew, out var capacity))
        {
            throw new DomainException("mass_unknown");
        }
        // Native packing requires a day of food before it returns route estimates. Seed with that legal minimum;
        // native measures silver mass rather than Go inventing a unit weight for modded definitions.
        var (seedFood, seedReason) = Pol.TradeMissionFacts.TradeMissionFood(facts, crew, 1, capacity, thresholds);
        if (seedReason.Length != 0)
        {
            throw new DomainException(seedReason);
        }
        var seedCargo = new List<CargoItem>(seedFood) { new("Silver", (ulong)silverBudget) };
        if (!CaravanDeparture.TryCreate(crew, seedCargo, settlement.Tile, out var departure))
        {
            throw new DomainException("invalid_trade_pack");
        }

        ObservationsPb.TradePackEstimate Preview(CaravanDeparture d)
        {
            var pack = new OperationsPb.FormCaravanIntent { DestinationTile = d.DestinationTile };
            foreach (var id in d.Crew)
            {
                pack.PawnIds.Add(id.Value);
            }
            foreach (var item in d.Cargo)
            {
                pack.Cargo.Add(new OperationsPb.DefCount { DefName = item.Definition, Count = (int)item.Count });
            }
            var (read, _) = source.ReadTradeAcquisition(ct, BoundaryApi.Identity(world), pack);
            return read?.Pack ?? throw new DomainException("pack_unknown");
        }

        var seed = Preview(departure);
        var daysKnown = Pol.TradeMissionFacts.TradeMissionRouteDays(seed, homeTile, settlement.Tile, out var days);
        if (!daysKnown || !seed.HasMassUsage || !seed.HasMassCapacity
            || double.IsNaN(seed.MassUsage) || double.IsNaN(seed.MassCapacity)
            || double.IsInfinity(seed.MassUsage) || double.IsInfinity(seed.MassCapacity) || seed.MassUsage < 0)
        {
            throw new DomainException("unknown_trip_facts");
        }
        // Calculate the whole food pack under the original native capacity minus silver. Seed nutrition is not
        // removed from home reserves twice.
        var seedFoodMass = 0.0;
        facts.AnimalUpkeep.Food.TryGetValue(out var supply);
        foreach (var item in seedFood)
        {
            var mass = double.MaxValue;
            foreach (var stock in supply?.Stocks ?? [])
            {
                if (stock.DefName.Value == item.Definition && stock.UnitMass.TryGetValue(out var unit))
                {
                    mass = Math.Min(mass, unit);
                }
            }
            if (mass == double.MaxValue)
            {
                throw new DomainException("mass_unknown");
            }
            seedFoodMass += mass * item.Count;
        }
        var (food, foodReason) = Pol.TradeMissionFacts.TradeMissionFood(facts, crew, days, seed.MassCapacity - seed.MassUsage + seedFoodMass, thresholds);
        if (foodReason.Length != 0)
        {
            throw new DomainException(foodReason);
        }
        var cargo = new List<CargoItem>(food) { new("Silver", (ulong)silverBudget) };
        if (!CaravanDeparture.TryCreate(crew, cargo, settlement.Tile, out departure))
        {
            throw new DomainException("invalid_trade_pack");
        }
        var final = Preview(departure);
        if (!Pol.TradeMissionFacts.SafeTradeMissionPack(final, homeTile, settlement.Tile))
        {
            throw new DomainException("unsafe_trade_pack");
        }
        // Demand is canonicalized through the same definition/count contract.
        if (!CaravanDeparture.TryCreate(crew, demand, settlement.Tile, out var canonicalDemand))
        {
            throw new DomainException("invalid_trade_demand");
        }
        var m = new TradeMission
        {
            HomeColony = world.Colony,
            HomeMap = world.Map,
            HomeTile = homeTile,
            Settlement = settlement.Id,
            SettlementTile = settlement.Tile,
            Crew = departure.Crew,
            Negotiator = crew[0],
            SilverBudget = silverBudget,
            Demand = canonicalDemand.Cargo,
            Pack = departure.Cargo,
            Phase = TradeMissionPhase.Planned,
            ReturnHome = true,
        };
        m.Validate();
        Pol.TradeMissionFacts.TradeMissionRouteDays(final, homeTile, settlement.Tile, out days);
        var option = new Pol.TradeAcquisitionOption
        {
            Id = "settlement:" + settlement.Id,
            Kind = Pol.TradeAcquisitionKind.Settlement,
            Participant = new TradeParticipant(TradeParticipantKind.Settlement, settlement.Id),
            TraderKind = settlement.TraderKind,
            Negotiator = m.Negotiator.Value,
            Eligible = Fact.Known(true),
            SafeCrew = Fact.Known(true),
            Packable = Fact.Known(true),
            RoutesReachable = Fact.Known(true),
            LeadDays = Fact.Known(days),
            OutboundDays = Fact.Known((double)(final.Outbound?.EstimatedTicks ?? 0) / GameTime.TicksPerDay),
            ReturnDays = Fact.Known((double)(final.Home?.EstimatedTicks ?? 0) / GameTime.TicksPerDay),
            FoodDays = Fact.Known(final.FoodDays),
            RotDays = Fact.Known(final.FoodRotDays),
            LaborTicks = Fact.Known(days * GameTime.TicksPerDay * crew.Count),
        };
        return (m, option);
    }

    /// <summary>
    /// Never authorizes a fresh formation after the planned phase. Lost receipts are resolved from exact native
    /// crew, and partial or conflicting crew evidence holds the mission instead of sending replacements.
    /// </summary>
    public static (TradeMissionPhase Phase, Bridge.CaravanJourney? Journey, bool Observed) TradeMissionDepartureEvidence(TradeMission m, Bridge.WorldProgressionRead? read)
    {
        if (!TradeMissionValid(m)
            || (m.Phase != TradeMissionPhase.Planned && m.Phase != TradeMissionPhase.Departing && m.Phase != TradeMissionPhase.Outbound)
            || read?.Context?.Identity is null
            || read.Context.Identity.ColonyId != m.HomeColony.Value)
        {
            return (m.Phase, null, false);
        }
        bool Exact(IReadOnlyList<string> ids) => ids.Count == m.Crew.Count && m.Crew.All(id => ids.Contains(id.Value));
        bool Overlaps(IReadOnlyList<string> ids) => m.Crew.Any(id => ids.Contains(id.Value));
        Bridge.CaravanJourney? found = null;
        foreach (var row in read.Caravans)
        {
            if (Overlaps(row.PawnIds))
            {
                if (!Exact(row.PawnIds) || found is not null)
                {
                    return (m.Phase, null, false);
                }
                found = row;
            }
        }
        if (found is not null)
        {
            if (found.Tile == m.SettlementTile && !found.Moving)
            {
                return (TradeMissionPhase.Buying, found, true);
            }
            return (TradeMissionPhase.Outbound, found, true);
        }
        foreach (var assembly in read.Assemblies)
        {
            if (Overlaps(assembly.PawnIds))
            {
                if (assembly.MapId != m.HomeMap.Value || !Exact(assembly.PawnIds))
                {
                    return (m.Phase, null, false);
                }
                return (TradeMissionPhase.Departing, null, true);
            }
        }
        return (m.Phase, null, false);
    }
}

internal static partial class BuildingRuntimePackage
{
    internal static bool TradeMissionCurrentRead(GenerationSnapshot world, Bridge.WorldProgressionRead? read)
    {
        if (read?.Context?.Identity is not { } id)
        {
            return false;
        }
        return id.ColonyId == world.Colony.Value && id.MapId == world.Map.Value && id.LoadToken == world.Load.Value
            && new NativeGeneration(read.Context.NativeGeneration) == world.Native;
    }

    internal static bool TradeMissionCrewAtHome(TradeMission m, Bridge.WorldProgressionRead read)
    {
        foreach (var caravan in read.Caravans)
        {
            if (m.Crew.Any(id => caravan.PawnIds.Contains(id.Value)))
            {
                return false;
            }
        }
        foreach (var assembly in read.Assemblies)
        {
            if (m.Crew.Any(id => assembly.PawnIds.Contains(id.Value)))
            {
                return false;
            }
        }
        foreach (var home in read.Maps)
        {
            if (home.Home && new MapId(home.Id) == m.HomeMap && home.Tile == m.HomeTile)
            {
                return m.Crew.All(id => home.PawnIds.Contains(id.Value));
            }
        }
        return false;
    }
}

public sealed partial class Rounder
{
    /// <summary>
    /// Deliberately unregistered. The complete driver calls it under the player gate after persisting the
    /// planned Project. Returns the departure plan's id, empty when only the mission's phase was recorded.
    /// </summary>
    public PlanId AdmitTradeMissionDeparture(
        CancellationToken call,
        CancellationToken epoch,
        ControlState state,
        Sto.StandardState project,
        Bridge.WorldProgressionRead read,
        StepArbiter arbiter)
    {
        var m = project.Standard.Mission ?? throw new DomainException("standard carries no trade mission");
        if (!state.Enabled || !state.ObservationKnown || !project.Standard.Snapshot.SameWorld(state.Snapshot)
            || m.Phase != TradeMissionPhase.Planned || project.History.Count != 0)
        {
            throw new ControlException("AdmitTradeMissionDeparture: project not planned in an enabled, known world");
        }
        if (!TradeMissionCurrentRead(state.Snapshot, read))
        {
            throw new ControlException("AdmitTradeMissionDeparture: read is not current");
        }
        Player.Current(call, epoch);
        if (Player.Session.State() != state)
        {
            throw new ControlException("AdmitTradeMissionDeparture: session state changed");
        }
        var (phase, _, observed) = TradeMissions.TradeMissionDepartureEvidence(m, read);
        if (observed)
        {
            m = m with { Phase = phase };
            Player.Journal.RecordStandard(call, project.Standard.Id, project.Revision, m);
            return default;
        }
        if (!TradeMissionCrewAtHome(m, read))
        {
            throw new ControlException("AdmitTradeMissionDeparture: crew not at home");
        }
        if (!arbiter.TryClaim(m.Crew, "trade-mission:" + m.Settlement))
        {
            throw new InvalidOperationException("trade mission crew claimed");
        }
        var d = m.Departure();
        var id = PlanIds.MintPlanId();
        var a = DomainAction.NewCaravanDepartureAction(new ActionId(id.Value + "-0"), d);
        var plan = new PlanSpec(id, new PlanRevision(1), [a]);
        m = m with { Phase = TradeMissionPhase.Departing };
        Player.Journal.CommitMethodRecord(call, project.Standard.Id, project.Revision, new MethodId("trade-departure"), $"settlement:{m.Settlement}", plan, m);
        return id;
    }
}
