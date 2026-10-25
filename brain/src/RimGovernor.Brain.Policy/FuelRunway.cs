namespace RimGovernor.Brain.Policy;

// Fuel runway. A refuelable building (a generator, a turret barrel) holds fuel units and burns them at its def's
// rate; the runway is the fuel it holds and the stock behind it over that burn, against MaintenanceHorizonDays.
// The shortfall is a forward-projector domain and the items to stock ahead of it are an economy/resource level in
// the Rounder's construction memory, like ClothingRunway.

/// <summary>
/// The burn a refuelable def states (CompProperties_Refuelable), read from the catalog and never guessed. PerDay
/// is fuelConsumptionRate, the fuel units a day the comp burns while it ticks (native divides it by 60000 per
/// tick) and UnitsPerItem the fuel units one item loads (fuelMultiplier). UseDriven (consumeFuelOnlyWhenUsed or
/// externalTicking) is a comp that burns per use, a turret's per shot: it has no daily rate. InRain is a comp
/// that also burns in rain. WhenPowered and Flickable gate the continuous burn: native ticks it only while the
/// building is powered (WhenPowered) and switched on (Flickable). UnitsPerItem uses the game's current-difficulty
/// multiplier, including its maintenance cost factor.
/// </summary>
public sealed record FuelBurn
{
    public double PerDay { get; init; }

    public double UnitsPerItem { get; init; }

    public bool UseDriven { get; init; }

    public bool InRain { get; init; }

    public bool WhenPowered { get; init; }

    public bool Flickable { get; init; }
}

/// <summary>
/// One refuelable building the power census shows: the fuel units it holds against its target, the facts its
/// def's burn is gated on and the fuel definitions it accepts, in the census's order.
/// </summary>
public sealed record FuelConsumer
{
    public string Id { get; init; } = "";

    public string Definition { get; init; } = "";

    public double Fuel { get; init; }

    public double Target { get; init; }

    public bool OutOfFuel { get; init; }

    public bool SwitchedOn { get; init; }

    public bool Powered { get; init; }

    public required FuelBurn Burn { get; init; }

    public IReadOnlyList<Resource> Fuels { get; init; } = [];

    /// <summary>The fuel definition the consumer's demand is named for: the first it accepts that is in stock, else its first (the rearm's choice).</summary>
    internal (Resource Item, bool Ok) FuelItem(StockReader stock)
    {
        foreach (var r in Fuels)
        {
            if (stock.Count(r) > 0)
            {
                return (r, true);
            }
        }
        return Fuels.Count == 0 ? (default, false) : (Fuels[0], true);
    }
}

/// <summary>What the fuel runway is computed from.</summary>
public sealed record FuelInputs
{
    public EquatableArray<FuelConsumer> Consumers { get; init; }

    public StockReader Stock { get; init; }
}

/// <summary>The fuel domain of the forward projection.</summary>
public abstract record FuelProjection
{
    private protected FuelProjection() { }

    /// <summary>Continuous demand is projected; per-use consumers with fuel are explicitly listed as gaps.</summary>
    public sealed record Projected(IReadOnlyList<string> Gaps) : FuelProjection;

    /// <summary>The nonempty rain-driven consumers have no fixed daily burn to project.</summary>
    public sealed record RainDependent(IReadOnlyList<string> Consumers) : FuelProjection;
}

/// <summary>The review's fuel runway: Needs the stock levels the economy/resource Need must reach, the projection beside them.</summary>
public sealed record FuelRunway
{
    public IReadOnlyDictionary<Resource, long> Needs { get; init; } = new Dictionary<Resource, long>();

    public required FuelProjection Projection { get; init; }
}

public static class RoundsFactsFuelExtensions
{
    /// <summary><see cref="FuelRunwayRules.PlanFuelRunway"/> of the review's facts.</summary>
    public static FuelRunway FuelRunway(this RoundsFacts f) =>
        FuelRunwayRules.PlanFuelRunway(new FuelInputs { Consumers = f.Fuel, Stock = new StockReader(f.Resources, f.Wood) });
}

public static class FuelRunwayRules
{
    /// <summary>
    /// Rolls every refuelable building forward over MaintenanceHorizonDays. A building
    /// that burns at a known daily rate needs the horizon's burn less its tank; one already empty needs its refill
    /// (runway zero, whatever its rate). A building that burns per use and still holds fuel has no runway and is
    /// listed in Gaps. Rain-driven consumers with fuel name the weather dependence of the projection.
    /// A malformed consumer fails the calculation. The demand
    /// is an absolute stock level, counted only where the stock falls short of it.
    /// </summary>
    public static FuelRunway PlanFuelRunway(FuelInputs input)
    {
        var consumers = input.Consumers;
        var accounts = new Dictionary<Resource, double>();
        var rainDependent = new List<string>();
        var gaps = new List<string>();
        foreach (var c in consumers)
        {
            var burn = c.Burn;
            var fuel = c.Fuel;
            var (item, ik) = c.FuelItem(input.Stock);
            if (!ik || !double.IsFinite(fuel) || fuel < 0 || !double.IsFinite(c.Target) || c.Target < 0
                || !double.IsFinite(burn.UnitsPerItem) || burn.UnitsPerItem <= 0 || !double.IsFinite(burn.PerDay) || burn.PerDay < 0)
            {
                throw new DomainException($"invalid fuel consumer {c.Id}");
            }
            var continuous = !burn.UseDriven && !burn.InRain;
            var rate = (burn.Flickable && !c.SwitchedOn) || (burn.WhenPowered && !c.Powered) ? 0 : burn.PerDay;
            double need;
            if (continuous)
            {
                need = Math.Max(0, (rate * ColonyDiagnosticsRules.MaintenanceHorizonDays) - fuel);
            }
            else if (c.OutOfFuel)
            {
                need = Math.Max(0, c.Target - fuel);
            }
            else if (burn.UseDriven)
            {
                gaps.Add(c.Id);
                continue;
            }
            else
            {
                rainDependent.Add(c.Id);
                continue;
            }
            accounts[item] = accounts.GetValueOrDefault(item) + need / burn.UnitsPerItem;
        }
        gaps.Sort(StringComparer.Ordinal);
        var needs = new Dictionary<Resource, long>();
        foreach (var (item, amount) in accounts)
        {
            var need = (long)Math.Ceiling(amount);
            if (need > input.Stock.Count(item)) needs[item] = need;
        }
        if (rainDependent.Count > 0)
        {
            rainDependent.Sort(StringComparer.Ordinal);
            return new FuelRunway { Needs = needs, Projection = new FuelProjection.RainDependent(rainDependent) };
        }
        return new FuelRunway
        {
            Needs = needs,
            Projection = new FuelProjection.Projected(gaps),
        };
    }
}
