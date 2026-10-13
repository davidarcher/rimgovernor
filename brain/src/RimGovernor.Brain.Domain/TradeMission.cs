namespace RimGovernor.Brain.Domain;

/// <summary>The Concern ids this package names itself.</summary>
public static class Concerns
{
    public static readonly ConcernId TradeMission = new("trade-mission");
}

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
/// Intent carried by <see cref="Standard.Mission"/>. It holds no native inventory, caravan identity, position,
/// prices or routes. Go persisted it as JSON text; it is a typed value here.
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
                && Identity.IsValidText(Settlement)
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
}

public sealed partial record Standard
{
    /// <summary>A Once Standard whose Record is the mission (Go <c>NewTradeMissionStandard</c>).</summary>
    public static Standard NewTradeMissionStandard(StandardId id, int priority, GenerationSnapshot snapshot, TradeMission m)
    {
        DomainException.Require(m.Phase == TradeMissionPhase.Planned && m.HomeColony == snapshot.Colony && m.HomeMap == snapshot.Map, "trade mission scope mismatch");
        var g = new Standard
        {
            Id = id,
            Kind = Concerns.TradeMission,
            Priority = priority,
            Snapshot = snapshot,
            Status = StandardStatus.Open,
            Finding = Finding.Unclear,
            Once = true,
            Mission = m,
        };
        g.Validate();
        return g;
    }
}
