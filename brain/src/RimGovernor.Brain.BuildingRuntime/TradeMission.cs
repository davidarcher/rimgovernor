namespace RimGovernor.Brain.BuildingRuntime;

partial class RoundsTradePlanner
{
    /// <summary>Whether <paramref name="m"/> passes its own validation.</summary>
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
partial class RoundsTradePlanner
{
    /// <summary>
    /// A read-only exact-pack calculation. The caller feeds its returned option to the existing acquisition
    /// planner. No planner registers automatic departure until the complete purchase/return driver is
    /// installed. Throws <see cref="DomainException"/> with the failure reason when the mission cannot be
    /// prepared.
    /// </summary>
    public static async Task<(TradeMission Mission, Pol.TradeAcquisitionOption Option)> PrepareTradeMissionAsync(FacadeNativeClient source, GenerationSnapshot world, int homeTile, O.Settlement settlement, Pol.RoundsFacts facts, Pol.RoundsPolicy thresholds, long silverBudget, IReadOnlyList<CargoItem> demand, IReadOnlyList<PawnId>? claimed, CancellationToken ct)
    {
        if (!world.IsValid || homeTile < 0 || settlement.Tile == homeTile || settlement.Relation == O.SettlementRelation.Player || settlement.TraderKind.Length == 0)
        {
            throw new DomainException("invalid_trade_destination");
        }
        if (!settlement.CanTrade)
        {
            throw new DomainException("settlement_unavailable");
        }
        var silver = facts.Silver();
        var (reserve, rk) = Pol.TradePolicyRules.TradeSilverReserve(facts.Colonists);
        if (!rk || silverBudget <= 0 || silverBudget > int.MaxValue || silver < reserve || silverBudget > silver - reserve)
        {
            throw new DomainException("insufficient_silver");
        }
        var (crew, crewSkip) = Pol.TradePolicyRules.TradeMissionCrew(facts, claimed ?? []);
        if (!crewSkip.IsNone)
        {
            throw new DomainException(crewSkip.Value);
        }
        var domainCrew = crew.Select(p => p).ToList();
        var (capacity, capacityRead) = Pol.TradePolicyRules.TradeMissionCarryCapacity(facts, crew);
        if (!capacityRead)
        {
            throw new DomainException("no_crew_carry_capacity");
        }
        // Native packing requires a day of food before it returns route estimates. Seed with that legal minimum;
        // native measures silver mass rather than the brain inventing a unit weight for modded definitions.
        var (seedFood, seedSkip) = Pol.TradePolicyRules.TradeMissionFood(facts, crew, 1, capacity, thresholds);
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
            var read = await source.ReadTradeAcquisitionAsync(Boundary.Identity(world), pack, ct).ConfigureAwait(false);
            return read.Pack ?? throw new BridgeContractException("trade acquisition read omitted the requested pack");
        }

        if (await PreviewAsync(departure).ConfigureAwait(false) is not ObservationsPb.TradePackEstimate.Packed seed || seed.MassUsage < 0)
        {
            throw new DomainException("cannot_pack");
        }
        var (days, reachable) = Pol.TradePolicyRules.TradeMissionRouteDays(seed);
        if (!reachable)
        {
            throw new DomainException("unreachable_route");
        }
        // Calculate the whole food pack under the original native capacity minus silver. Seed nutrition is not
        // removed from home reserves twice.
        var seedFoodMass = 0.0;
        var supply = facts.AnimalUpkeep.Food;
        foreach (var item in seedFood)
        {
            var mass = double.MaxValue;
            foreach (var stock in supply.Stocks)
            {
                if (stock.DefName.Value == item.Definition)
                {
                    mass = Math.Min(mass, stock.UnitMass);
                }
            }
            if (mass == double.MaxValue)
            {
                throw new DomainException("no_seed_food_mass");
            }
            seedFoodMass += mass * item.Count;
        }
        var (food, foodSkip) = Pol.TradePolicyRules.TradeMissionFood(facts, crew, days, seed.MassCapacity - seed.MassUsage + seedFoodMass, thresholds);
        if (!foodSkip.IsNone)
        {
            throw new DomainException(foodSkip.Value);
        }
        var cargo = new List<CargoItem>(food) { new("Silver", (ulong)silverBudget) };
        if (!CaravanDeparture.TryCreate(domainCrew, cargo, settlement.Tile, out departure))
        {
            throw new DomainException("invalid_trade_pack");
        }
        if (await PreviewAsync(departure).ConfigureAwait(false) is not ObservationsPb.TradePackEstimate.Packed final
            || !Pol.TradePolicyRules.SafeTradeMissionPack(final)
            || (final.Outbound, final.Home) is not (ObservationsPb.PackRoute.Reachable outbound, ObservationsPb.PackRoute.Reachable home))
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
        (days, _) = Pol.TradePolicyRules.TradeMissionRouteDays(final);
        var option = new Pol.TradeAcquisitionOption
        {
            ID = "settlement:" + settlement.Id,
            Kind = Pol.TradeAcquisitionKind.Settlement,
            Participant = new TradeParticipant(TradeParticipantKind.Settlement, settlement.Id),
            TraderKind = settlement.TraderKind,
            Negotiator = m.Negotiator.Value,
            Eligible = true,
            LeadDays = days,
            OutboundDays = (double)outbound.EstimatedTicks / GameTime.TicksPerDay,
            ReturnDays = (double)home.EstimatedTicks / GameTime.TicksPerDay,
            FoodDays = final.FoodDays,
            RotDays = final.FoodRotDays,
            LaborTicks = days * GameTime.TicksPerDay * crew.Count,
        };
        return (m, option);
    }

    /// <summary>
    /// Never authorizes a fresh formation after the planned phase. Lost receipts are resolved from exact native
    /// crew, and partial or conflicting crew evidence holds the mission instead of sending replacements.
    /// </summary>
    public static (TradeMissionPhase Phase, bool Observed) TradeMissionDepartureEvidence(TradeMission m, WorldProgressionRead? read)
    {
        if (!TradeMissionValid(m)
            || (m.Phase != TradeMissionPhase.Planned && m.Phase != TradeMissionPhase.Departing && m.Phase != TradeMissionPhase.Outbound)
            || read?.Context.Identity is null
            || (read.Context.Identity.ColonyId ?? "") != m.HomeColony.Value)
        {
            return (m.Phase, false);
        }
        bool Exact(IReadOnlyList<PawnId> ids) => ids.Count == m.Crew.Count && m.Crew.All(ids.Contains);
        bool Overlaps(IReadOnlyList<PawnId> ids) => m.Crew.Any(ids.Contains);
        PbObservations.CaravanState? found = null;
        foreach (var row in read.Caravans)
        {
            var crew = row.Crew();
            if (Overlaps(crew))
            {
                if (!Exact(crew) || found is not null)
                {
                    return (m.Phase, false);
                }
                found = row;
            }
        }
        if (found is not null)
        {
            if (found.Tile == m.SettlementTile && found.Course is PbObservations.CaravanCourse.Stopped)
            {
                return (TradeMissionPhase.Buying, true);
            }
            return (TradeMissionPhase.Outbound, true);
        }
        foreach (var assembly in read.Assemblies)
        {
            if (Overlaps(assembly.Pawns))
            {
                if (assembly.MapId != m.HomeMap.Value || !Exact(assembly.Pawns))
                {
                    return (m.Phase, false);
                }
                return (TradeMissionPhase.Departing, true);
            }
        }
        return (m.Phase, false);
    }
}

partial class RoundsTradePlanner
{
    internal static bool TradeMissionCurrentRead(GenerationSnapshot world, WorldProgressionRead? read)
    {
        if (read?.Context.Identity is not { } id)
        {
            return false;
        }
        return (id.ColonyId ?? "") == world.Colony.Value && id.MapId == world.Map.Value && (id.LoadToken ?? "") == world.Load.Value
            && read.Context.NativeGeneration.IsAt(world.Native.Value);
    }

    internal static bool TradeMissionCrewAtHome(TradeMission m, WorldProgressionRead read)
    {
        foreach (var caravan in read.Caravans)
        {
            if (m.Crew.Any(caravan.Crew().Contains))
            {
                return false;
            }
        }
        foreach (var assembly in read.Assemblies)
        {
            if (m.Crew.Any(assembly.Pawns.Contains))
            {
                return false;
            }
        }
        foreach (var home in read.Maps)
        {
            if (home.Home && new MapId(home.Id) == m.HomeMap && home.Tile == m.HomeTile)
            {
                return m.Crew.All(home.Colonists().Contains);
            }
        }
        return false;
    }
}

public sealed partial class Rounder
{
}
