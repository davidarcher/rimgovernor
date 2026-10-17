namespace RimGovernor.Brain.Domain;

public enum TradeMissionPhase
{
    Planned,
    Departing,
    Outbound,
    Buying,
    Returning,
    Delivered,
    Ended,
}

/// <summary>
/// A settlement trade mission's intent, kept in the economy family's <c>family/economy/trade_missions</c> record
/// (#3316; a trade-mission Standard carried it before). It holds no native inventory, caravan identity, position,
/// prices or routes.
/// </summary>
public sealed record TradeMission
{
    public ColonyId HomeColony { get; init; }

    public MapId HomeMap { get; init; }

    public int HomeTile { get; init; }

    public string Settlement { get; init; } = "";

    public int SettlementTile { get; init; }

    public EquatableArray<PawnId> Crew { get; init; }

    public PawnId Negotiator { get; init; }

    public long SilverBudget { get; init; }

    public EquatableArray<CargoItem> Demand { get; init; }

    public EquatableArray<CargoItem> Pack { get; init; }

    public TradeMissionPhase Phase { get; init; }

    public bool ReturnHome { get; init; }

    /// <summary>Bounded authorized purchase intent, never observed stock.</summary>
    public EquatableArray<CargoItem> ReturnGoods { get; init; }

    public bool PurchaseCommitted { get; init; }

    public void Validate()
    {
        DomainException.Require(
            HomeColony.IsValid
                && HomeMap.Value >= 0
                && HomeTile >= 0
                && IdText.IsValidText(Settlement)
                && SettlementTile >= 0
                && SettlementTile != HomeTile
                && SilverBudget > 0
                && ReturnHome
                && Crew.Contains(Negotiator),
            "invalid trade mission identity or budget"
        );
        DomainException.Require(
            CaravanDeparture.TryCreate(Crew, Pack, SettlementTile, out var departure) && Crew == departure.Crew && Pack == departure.Cargo,
            "invalid trade mission crew or pack"
        );
        DomainException.Require(
            CaravanDeparture.TryCreate(Crew, Demand, SettlementTile, out var demand) && Demand.Count != 0 && Demand == demand.Cargo,
            "invalid trade mission demand"
        );
        ulong silver = 0;
        foreach (var item in Pack)
        {
            if (item.Definition == "Silver")
            {
                silver = item.Count;
            }
        }
        DomainException.Require(silver == (ulong)SilverBudget, "trade mission budget differs from packed silver");
        if (ReturnGoods.Count != 0)
        {
            DomainException.Require(
                CaravanDeparture.TryCreate(Crew, ReturnGoods, HomeTile, out var goods) && ReturnGoods == goods.Cargo && PurchaseCommitted,
                "invalid authorized return goods"
            );
            foreach (var item in ReturnGoods)
            {
                var demanded = Demand.Where(d => d.Definition == item.Definition).Select(d => (ulong?)d.Count).FirstOrDefault();
                DomainException.Require(demanded is not null && item.Count <= demanded, "return goods exceed mission demand");
            }
        }
        DomainException.Require(!(PurchaseCommitted && ReturnGoods.Count == 0), "purchase commitment needs authorized goods");
        DomainException.Require(Wire.IsDefined(Phase), "invalid trade mission phase");
    }

    /// <summary>The caravan departure this mission sends out.</summary>
    public CaravanDeparture Departure()
    {
        Validate();
        return new CaravanDeparture(Crew, Pack, SettlementTile);
    }

    /// <summary>Whether the mission is over: delivered, or ended without delivery.</summary>
    public bool Over => Phase is TradeMissionPhase.Delivered or TradeMissionPhase.Ended;
}
