namespace RimGovernor.Brain.Policy;

// Fuel runway. A refuelable building (a generator, a turret barrel) holds fuel units and burns them at its def's
// rate; the runway is the fuel it holds and the stock behind it over that burn, against ProjectionHorizonDays.
// The shortfall is a forward-projector domain and the items to stock ahead of it are a MaintainResource level in
// the Rounder's construction memory, like ClothingRunway.

/// <summary>
/// The burn a refuelable def states (CompProperties_Refuelable), read from the catalog and never guessed. PerDay
/// is fuelConsumptionRate, the fuel units a day the comp burns while it ticks (native divides it by 60000 per
/// tick) and UnitsPerItem the fuel units one item loads (fuelMultiplier). UseDriven (consumeFuelOnlyWhenUsed or
/// externalTicking) is a comp that burns per use, a turret's per shot: it has no daily rate. InRain is a comp
/// that also burns in rain. WhenPowered and Flickable gate the continuous burn: native ticks it only while the
/// building is powered (WhenPowered) and switched on (Flickable). Difficulty scales UnitsPerItem for a comp with
/// factorByDifficulty (the storyteller's maintenance cost factor, which no observation carries): UnitsPerItem is
/// then the def's own multiplier. (Go <c>FuelBurn</c>.)
/// </summary>
public sealed record FuelBurn
{
    public double PerDay { get; init; }

    public double UnitsPerItem { get; init; }

    public bool UseDriven { get; init; }

    public bool InRain { get; init; }

    public bool WhenPowered { get; init; }

    public bool Flickable { get; init; }

    public bool Difficulty { get; init; }
}

/// <summary>
/// One refuelable building the power census shows: the fuel units it holds against its target, the facts its
/// def's burn is gated on and the fuel definitions it accepts, in the census's order. (Go <c>FuelConsumer</c>.)
/// </summary>
public sealed record FuelConsumer
{
    public string Id { get; init; } = "";

    public string Definition { get; init; } = "";

    public Fact<double> Fuel { get; init; }

    public Fact<double> Target { get; init; }

    public Fact<bool> OutOfFuel { get; init; }

    public Fact<bool> SwitchedOn { get; init; }

    public Fact<bool> Powered { get; init; }

    public Fact<FuelBurn> Burn { get; init; }

    public IReadOnlyList<Resource> Fuels { get; init; } = [];

    /// <summary>
    /// Go <c>FuelConsumer.perDay</c>: the fuel units a day the building burns now, unknown for a comp that burns
    /// per use or in rain, and when a fact its gate reads is unknown.
    /// </summary>
    internal Fact<double> PerDay()
    {
        if (!Burn.TryGetValue(out var burn) || burn.UseDriven || burn.InRain || !PolicyFunctions.Finite(burn.PerDay) || burn.PerDay < 0)
        {
            return Unknown<double>();
        }
        if (burn.Flickable)
        {
            if (!SwitchedOn.TryGetValue(out var on))
            {
                return Unknown<double>();
            }
            if (!on)
            {
                return Known(0.0);
            }
        }
        if (burn.WhenPowered)
        {
            if (!Powered.TryGetValue(out var powered))
            {
                return Unknown<double>();
            }
            if (!powered)
            {
                return Known(0.0);
            }
        }
        return Known(burn.PerDay);
    }

    /// <summary>Go <c>FuelConsumer.fuelItem</c>: the fuel definition the consumer's demand is named for: the first it accepts that is in stock, else its first (the rearm's choice).</summary>
    internal (Resource Item, bool Ok) FuelItem(StockReader stock)
    {
        foreach (var r in Fuels)
        {
            if (stock.Count(r).TryGetValue(out var n) && n > 0)
            {
                return (r, true);
            }
        }
        return Fuels.Count == 0 ? (default, false) : (Fuels[0], true);
    }
}

/// <summary>What the fuel runway is computed from. (Go <c>FuelInputs</c>.)</summary>
public sealed record FuelInputs
{
    public Fact<EquatableArray<FuelConsumer>> Consumers { get; init; }

    public StockReader Stock { get; init; }
}

/// <summary>
/// One fuel item's runway. Burn is the fuel units a day the continuous consumers burn, Need the items to hold
/// ahead of them (the horizon's burn less what their tanks hold, plus the refill of the barrels already empty)
/// and MissingItems the items missing from stock. (Go <c>FuelResourceRunway</c>.)
/// </summary>
public sealed record FuelResourceRunway
{
    public Resource Resource { get; init; }

    public double Burn { get; init; }

    public long Need { get; init; }

    public long Stock { get; init; }

    public long MissingItems { get; init; }

    public double ShortfallDays { get; init; }
}

/// <summary>The fuel domain of the forward projection. ShortfallDays is the largest per-resource shortfall. (Go <c>FuelProjection</c>.)</summary>
public sealed record FuelProjection
{
    public IReadOnlyList<FuelResourceRunway> Resources { get; init; } = [];

    public double ShortfallDays { get; init; }

    /// <summary>The consumers that burn per use and so have no runway until their barrel is empty.</summary>
    public IReadOnlyList<string> Gaps { get; init; } = [];
}

/// <summary>The review's fuel runway: Needs the stock levels MaintainResource must reach, the projection beside them. (Go <c>FuelRunway</c>.)</summary>
public sealed record FuelRunway
{
    public IReadOnlyDictionary<Resource, long> Needs { get; init; } = new Dictionary<Resource, long>();

    public Fact<FuelProjection> Projection { get; init; }
}

public static class RoundsFactsFuelExtensions
{
    /// <summary>Go <c>RoundsFacts.FuelRunway</c>: <see cref="PolicyFunctions.PlanFuelRunway"/> of the review's facts.</summary>
    public static FuelRunway FuelRunway(this RoundsFacts f) =>
        PolicyFunctions.PlanFuelRunway(new FuelInputs { Consumers = f.Fuel, Stock = new StockReader { Resources = f.Resources, Wood = f.Wood } });
}

public static partial class PolicyFunctions
{
    private sealed class FuelAccount
    {
        public double Burn;
        public double Tanks;

        /// <summary>The items the two kinds of consumer need.</summary>
        public double Continuous;
        public double Empty;
    }

    /// <summary>
    /// Go <c>PlanFuelRunway</c>: rolls every refuelable building forward over ProjectionHorizonDays. A building
    /// that burns at a known daily rate needs the horizon's burn less its tank; one already empty needs its refill
    /// (runway zero, whatever its rate). A building that burns per use and still holds fuel has no runway and is
    /// listed in Gaps. An unread census or stock, or a building whose fuel, rate gate, fuel item or unit
    /// conversion is unknown, makes the projection unknown: its demand is left out, never defaulted. The demand
    /// is an absolute stock level, counted only where the stock falls short of it.
    /// </summary>
    public static FuelRunway PlanFuelRunway(FuelInputs input)
    {
        if (!input.Consumers.TryGetValue(out var consumers))
        {
            return new FuelRunway { Projection = Unknown<FuelProjection>() };
        }
        var accounts = new Dictionary<Resource, FuelAccount>();
        var units = new Dictionary<Resource, double>();
        var complete = true;
        var gaps = new List<string>();
        foreach (var c in consumers)
        {
            var bk = c.Burn.TryGetValue(out var burn);
            var fk = c.Fuel.TryGetValue(out var fuel);
            var (item, ik) = c.FuelItem(input.Stock);
            if (!bk || !fk || !ik || !Finite(fuel) || !(burn!.UnitsPerItem > 0))
            {
                complete = false;
                continue;
            }
            var ek = c.OutOfFuel.TryGetValue(out var empty);
            var rk = c.PerDay().TryGetValue(out var rate);
            double need;
            if (rk)
            {
                need = Math.Max(0, (rate * ProjectionHorizonDays) - fuel);
            }
            else if (ek && empty)
            {
                var tk = c.Target.TryGetValue(out var target);
                if (!tk || !Finite(target))
                {
                    complete = false;
                    continue;
                }
                need = Math.Max(0, target - fuel);
            }
            else if (burn.UseDriven)
            {
                gaps.Add(c.Id);
                continue;
            }
            else
            {
                complete = false;
                continue;
            }
            if (!accounts.TryGetValue(item, out var a))
            {
                a = new FuelAccount();
                accounts[item] = a;
            }
            units[item] = burn.UnitsPerItem;
            if (rk)
            {
                a.Burn += rate;
                a.Tanks += fuel;
                a.Continuous += need / burn.UnitsPerItem;
            }
            else
            {
                a.Empty += need / burn.UnitsPerItem;
            }
        }
        gaps.Sort(StringComparer.Ordinal);
        var stockKnown = true;
        var needs = new Dictionary<Resource, long>();
        var rows = new List<FuelResourceRunway>();
        var shortfallDays = 0.0;
        foreach (var (item, a) in accounts)
        {
            if (!input.Stock.Count(item).TryGetValue(out var have))
            {
                stockKnown = false;
                continue;
            }
            var need = (long)Math.Ceiling(a.Continuous + a.Empty);
            var row = new FuelResourceRunway { Resource = item, Burn = a.Burn, Need = need, Stock = have };
            if (need > have)
            {
                var shortItems = need - have;
                var days = ProjectionHorizonDays;
                if (a.Empty == 0 && a.Burn > 0)
                {
                    days = Math.Min(ProjectionHorizonDays, shortItems * units[item] / a.Burn);
                }
                row = row with { MissingItems = shortItems, ShortfallDays = days };
                needs[item] = row.Need;
            }
            shortfallDays = Math.Max(shortfallDays, row.ShortfallDays);
            rows.Add(row);
        }
        rows.Sort((x, y) => x.Resource.CompareTo(y.Resource));
        if (!complete || !stockKnown)
        {
            return new FuelRunway { Needs = needs, Projection = Unknown<FuelProjection>() };
        }
        return new FuelRunway
        {
            Needs = needs,
            Projection = Known(new FuelProjection { Resources = rows, ShortfallDays = shortfallDays, Gaps = gaps }),
        };
    }
}
