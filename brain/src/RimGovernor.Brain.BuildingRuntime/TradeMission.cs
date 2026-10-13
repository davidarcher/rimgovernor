namespace RimGovernor.Brain.BuildingRuntime;

public interface ITradeMissionPackSource
{
    Task<(ObservationsPb.TradeAcquisition Acquisition, BridgeResult Result)> ReadTradeAcquisitionAsync(C.Identity identity, OperationsPb.FormCaravanIntent? intent, CancellationToken ct);
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
    public static async Task<(TradeMission Mission, Pol.TradeAcquisitionOption Option)> PrepareTradeMissionAsync(ITradeMissionPackSource source, GenerationSnapshot world, int homeTile, SettlementFact settlement, Pol.RoundsFacts facts, Pol.RoundsPolicy thresholds, long silverBudget, IReadOnlyList<CargoItem> demand, IReadOnlyList<Pol.PawnId>? claimed, CancellationToken ct)
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
        var (reserve, rk) = Pol.PolicyFunctions.TradeSilverReserve(facts.Colonists);
        if (!sk || !rk || silverBudget <= 0 || silverBudget > int.MaxValue || silver < reserve || silverBudget > silver - reserve)
        {
            throw new DomainException("insufficient_silver");
        }
        var (crew, crewSkip) = Pol.PolicyFunctions.TradeMissionCrew(facts, claimed ?? []);
        if (!crewSkip.IsNone)
        {
            throw new DomainException(crewSkip.Value);
        }
        var domainCrew = crew.Select(p => new Domain.PawnId(p.Value)).ToList();
        var (capacity, capacityKnown) = Pol.PolicyFunctions.TradeMissionCarryCapacity(facts, crew);
        if (!capacityKnown)
        {
            throw new DomainException("mass_unknown");
        }
        // Native packing requires a day of food before it returns route estimates. Seed with that legal minimum;
        // native measures silver mass rather than Go inventing a unit weight for modded definitions.
        var (seedFood, seedSkip) = Pol.PolicyFunctions.TradeMissionFood(facts, crew, 1, capacity, thresholds);
        if (!seedSkip.IsNone)
        {
            throw new DomainException(seedSkip.Value);
        }
        var seedCargo = new List<CargoItem>(seedFood) { new("Silver", (ulong)silverBudget) };
        if (!CaravanDeparture.TryCreate(domainCrew, seedCargo, settlement.Tile, out var departure))
        {
            throw new DomainException("invalid_trade_pack");
        }
async 
        Task<ObservationsPb.TradePackEstimate> PreviewAsync(CaravanDeparture d)
        {
            var pack = new OperationsPb.FormCaravanIntent { DestinationTile = d.DestinationTile };
            foreach (var id in d.Crew)
            {
                pack = pack with { PawnIds = [.. pack.PawnIds, id.Value] };
            }
            foreach (var item in d.Cargo)
            {
                pack = pack with { Cargo = [.. pack.Cargo, new OperationsPb.DefCount { DefName = item.Definition, Count = (int)item.Count }] };
            }
            var (read, _) = await source.ReadTradeAcquisitionAsync(Boundary.Identity(world), pack, ct).ConfigureAwait(false);
            return read?.Pack ?? throw new DomainException("pack_unknown");
        }

        var seed = await PreviewAsync(departure).ConfigureAwait(false);
        var (days, daysKnown) = Pol.PolicyFunctions.TradeMissionRouteDays(seed, homeTile, settlement.Tile);
        if (!daysKnown || seed.MassUsage is null || seed.MassCapacity is null
            || double.IsNaN(seed.MassUsage.GetValueOrDefault()) || double.IsNaN(seed.MassCapacity.GetValueOrDefault())
            || double.IsInfinity(seed.MassUsage.GetValueOrDefault()) || double.IsInfinity(seed.MassCapacity.GetValueOrDefault()) || seed.MassUsage.GetValueOrDefault() < 0)
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
        var (food, foodSkip) = Pol.PolicyFunctions.TradeMissionFood(facts, crew, days, seed.MassCapacity.GetValueOrDefault() - seed.MassUsage.GetValueOrDefault() + seedFoodMass, thresholds);
        if (!foodSkip.IsNone)
        {
            throw new DomainException(foodSkip.Value);
        }
        var cargo = new List<CargoItem>(food) { new("Silver", (ulong)silverBudget) };
        if (!CaravanDeparture.TryCreate(domainCrew, cargo, settlement.Tile, out departure))
        {
            throw new DomainException("invalid_trade_pack");
        }
        var final = await PreviewAsync(departure).ConfigureAwait(false);
        if (!Pol.PolicyFunctions.SafeTradeMissionPack(final, homeTile, settlement.Tile))
        {
            throw new DomainException("unsafe_trade_pack");
        }
        // Demand is canonicalized through the same definition/count contract.
        if (!CaravanDeparture.TryCreate(domainCrew, demand, settlement.Tile, out var canonicalDemand))
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
            Negotiator = domainCrew[0],
            SilverBudget = silverBudget,
            Demand = canonicalDemand.Cargo,
            Pack = departure.Cargo,
            Phase = TradeMissionPhase.Planned,
            ReturnHome = true,
        };
        m.Validate();
        (days, _) = Pol.PolicyFunctions.TradeMissionRouteDays(final, homeTile, settlement.Tile);
        var option = new Pol.TradeAcquisitionOption
        {
            ID = "settlement:" + settlement.Id,
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
            FoodDays = Fact.Known(final.FoodDays.GetValueOrDefault()),
            RotDays = Fact.Known(final.FoodRotDays.GetValueOrDefault()),
            LaborTicks = Fact.Known(days * GameTime.TicksPerDay * crew.Count),
        };
        return (m, option);
    }

    /// <summary>
    /// Never authorizes a fresh formation after the planned phase. Lost receipts are resolved from exact native
    /// crew, and partial or conflicting crew evidence holds the mission instead of sending replacements.
    /// </summary>
    public static (TradeMissionPhase Phase, CaravanJourney? Journey, bool Observed) TradeMissionDepartureEvidence(TradeMission m, WorldProgressionRead? read)
    {
        if (!TradeMissionValid(m)
            || (m.Phase != TradeMissionPhase.Planned && m.Phase != TradeMissionPhase.Departing && m.Phase != TradeMissionPhase.Outbound)
            || read?.Context?.Identity is null
            || (read.Context.Identity.ColonyId ?? "") != m.HomeColony.Value)
        {
            return (m.Phase, null, false);
        }
        bool Exact(IReadOnlyList<string> ids) => ids.Count == m.Crew.Count && m.Crew.All(id => ids.Contains(id.Value));
        bool Overlaps(IReadOnlyList<string> ids) => m.Crew.Any(id => ids.Contains(id.Value));
        CaravanJourney? found = null;
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
    internal static bool TradeMissionCurrentRead(GenerationSnapshot world, WorldProgressionRead? read)
    {
        if (read?.Context?.Identity is not { } id)
        {
            return false;
        }
        return (id.ColonyId ?? "") == world.Colony.Value && id.MapId.GetValueOrDefault() == world.Map.Value && (id.LoadToken ?? "") == world.Load.Value
            && new NativeGeneration(read.Context.NativeGeneration.GetValueOrDefault()) == world.Native;
    }

    internal static bool TradeMissionCrewAtHome(TradeMission m, WorldProgressionRead read)
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
        ControlState state,
        Sto.StandardState project,
        WorldProgressionRead read,
        StepArbiter arbiter, CancellationToken call, CancellationToken epoch)
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
            Player.Journal.RecordStandard(project.Standard.Id, project.Revision, m, call);
            return default;
        }
        if (!TradeMissionCrewAtHome(m, read))
        {
            throw new ControlException("AdmitTradeMissionDeparture: crew not at home");
        }
        if (!arbiter.TryClaim([.. m.Crew.Select(id => new Pol.PawnId(id.Value))], "trade-mission:" + m.Settlement))
        {
            throw new InvalidOperationException("trade mission crew claimed");
        }
        var d = m.Departure();
        var id = PlanIds.MintPlanId();
        var a = DomainAction.NewCaravanDepartureAction(new ActionId(id.Value + "-0"), d);
        var plan = new PlanSpec(id, new PlanRevision(1), [a]);
        m = m with { Phase = TradeMissionPhase.Departing };
        Player.Journal.CommitMethodRecord(project.Standard.Id, project.Revision, new MethodId("trade-departure"), $"settlement:{m.Settlement}", plan, m, call);
        return id;
    }
}
