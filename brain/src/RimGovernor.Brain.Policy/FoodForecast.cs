using System.Text;

namespace RimGovernor.Brain.Policy;

/// <summary>The food forecast inputs are unavailable or invalid.</summary>
public sealed class FoodFactsException() : DomainException("food forecast inputs unavailable or invalid");

public sealed record FoodConsumer
{
    public PawnId Id { get; init; }

    public required double NutritionPerDay { get; init; }

    /// <summary>Includes animals whose native diet accepts it.</summary>
    public bool HumanMeatAcceptable { get; init; }
}

/// <summary>What a food stock is: an item stack, or a corpse whose food is its meat yield.</summary>
public abstract record FoodForm
{
    private protected FoodForm() { }

    public sealed record Item : FoodForm;

    /// <summary>A corpse: MeatAmount is its native meat yield before butcher efficiency, BodySize its inner pawn's body size.</summary>
    public sealed record Corpse(double MeatAmount, double BodySize) : FoodForm;
}

/// <summary>Where a food stock is: standing on the map (roofed or not, in Room, "" outside any room) or carried, unspawned.</summary>
public abstract record FoodPlace
{
    private protected FoodPlace() { }

    public sealed record Stands(bool Roofed, string Room) : FoodPlace;

    public sealed record Carried : FoodPlace;
}

/// <summary>How a food stock spoils: a thing rots exactly while it has a rot deadline.</summary>
public abstract record FoodRot
{
    private protected FoodRot() { }

    /// <summary>Never rots: a survival meal, pemmican, or a stock whose def has no rot clock.</summary>
    public sealed record Keeps : FoodRot;

    /// <summary>Rots in Ticks at its current temperature; zero or less has already rotted.</summary>
    public sealed record Rots(long Ticks) : FoodRot;
}

public sealed record FoodStock
{
    public string Id { get; init; } = "";

    /// <summary>Includes meals containing humanlike meat, not just raw meat.</summary>
    public bool IsHumanMeat { get; init; }

    public bool RawMeat { get; init; }

    public bool IsHumanlike { get; init; }

    public bool Vegetable { get; init; }

    public FoodIngredientClass RawClass { get; init; }

    /// <summary>Forbidden pemmican or survival meals; eligibility is after release.</summary>
    public bool Reserve { get; init; }

    /// <summary>The pawn whose inventory holds the stock; empty for a shared stock.</summary>
    public PawnId Holder { get; init; }

    /// <summary>The nutrition the stock gives its eaters (a corpse's: its meat yield's).</summary>
    public required double Nutrition { get; init; }

    public IReadOnlyList<PawnId> Eaters { get; init; } = [];

    /// <summary>Whether the stock keeps or rots, and in how many ticks at its current temperature.</summary>
    public required FoodRot Rot { get; init; }

    /// <summary>DefName and Count select a specific feed resource (the animal feed runway's item choice); ForecastFood never reads them.</summary>
    public Resource DefName { get; init; }

    public long Count { get; init; }

    /// <summary>The catalog mass used only for caravan packing.</summary>
    public double UnitMass { get; init; }

    /// <summary>Where the stock is at observation time; ForecastFood ignores it, storage upkeep and refrigeration policy read it.</summary>
    public required FoodPlace Place { get; init; }

    /// <summary>The ambient temperature where the stock is.</summary>
    public double TemperatureC { get; init; }

    /// <summary>An item stack or a corpse. Forbidden corpses remain observable reserves but do not extend the food runway.</summary>
    public required FoodForm Form { get; init; }

    public bool Corpse => Form is FoodForm.Corpse;

    public bool Forbidden { get; init; }
}

public sealed record FoodSupply
{
    public IReadOnlyList<FoodConsumer> Consumers { get; init; } = [];

    public IReadOnlyList<FoodStock> Stocks { get; init; } = [];

    /// <summary>Forbidden ordinary food stacks: census rows for the storage review only, never forecast or reserve input.</summary>
    public IReadOnlyList<FoodStock> Barred { get; init; } = [];

    public required FoodLarder Larder { get; init; }
}

public sealed record ConsumerFoodForecast
{
    public PawnId Id { get; init; }

    public double RunwayDays { get; init; }

    public double UsableNutrition { get; init; }

    public double AllocatedNutrition { get; init; }

    public double NutritionPerDay { get; init; }
}

/// <summary>The colony's food runway: the shortest runway of the selected consumers that eat, or none eats at all.</summary>
public abstract record FoodRunway
{
    private protected FoodRunway() { }

    /// <summary>No selected consumer eats: the selection is empty or every rate is zero.</summary>
    public sealed record NoEaters : FoodRunway;

    /// <summary>The shortest runway in days over the selected consumers that eat.</summary>
    public sealed record Days(double Value) : FoodRunway;

    /// <summary>Whether the runway lasts at least <paramref name="days"/>: nobody eating lasts forever.</summary>
    public bool Reaches(double days) => this is not Days runway || runway.Value >= days;
}

public sealed record FoodForecast
{
    /// <summary>The shortest consumer runway; an empty forecast has no eaters.</summary>
    public FoodRunway RunwayDays { get; init; } = new FoodRunway.NoEaters();

    public double UsableNutrition { get; init; }

    public double AtRiskNutrition { get; init; }

    public double InventoryNutrition { get; init; }

    public IReadOnlyList<ConsumerFoodForecast> Consumers { get; init; } = [];

    /// <summary>Non-colonist rows below the minimum runway once GateOnColonists has taken the colony runway over colonists only.</summary>
    public IReadOnlyList<ConsumerFoodForecast> PetShortfalls { get; init; } = [];

    /// <summary>
    /// Returns the forecast with RunwayDays taken over the colonists'
    /// rows only: one unfed pet must not read the colony as starving. Every other row whose runway falls
    /// below minDays is reported in PetShortfalls as its own need. The allocation itself is unchanged, so pets
    /// still compete for shared stock. An empty census keeps the all-consumer minimum, and a census with no
    /// fed rows has no eaters.
    /// </summary>
    public FoodForecast GateOnColonists(IReadOnlyList<PawnId> colonists, double minDays)
    {
        if (colonists.Count == 0)
        {
            return this;
        }
        var human = new HashSet<PawnId>(colonists);
        FoodRunway runway = new FoodRunway.NoEaters();
        var pets = new List<ConsumerFoodForecast>();
        foreach (var row in Consumers)
        {
            if (!human.Contains(row.Id))
            {
                if (row.RunwayDays < minDays)
                {
                    pets.Add(row);
                }
                continue;
            }
            if (runway is not FoodRunway.Days { Value: var minimum } || row.RunwayDays < minimum)
            {
                runway = new FoodRunway.Days(row.RunwayDays);
            }
        }
        return this with { RunwayDays = runway, PetShortfalls = pets };
    }
}

public static class FoodForecastRules
{
    internal static bool FoodNumber(double n) => !double.IsNaN(n) && !double.IsInfinity(n) && n >= 0;

    internal static bool FoodId(string id) =>
        id.Length > 0 && id.Length <= 256 && Encoding.UTF8.GetByteCount(id) <= 256 && IsValidUtf16(id) && id.AsSpan().Trim().Length != 0 && !id.Contains('\0');

    private static bool IsValidUtf16(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(s[i]))
            {
                return false;
            }
        }
        return true;
    }

    private sealed class ForecastStock
    {
        public string Id = "";
        public PawnId Holder;
        public double Amount;
        public double Expiry;
        public double EligibleDemand;
        public HashSet<PawnId> Eaters = [];
    }

    /// <summary>
    /// Allocates each stock among its observed eligible consumers by demand, then
    /// consumes allocations in expiry order. Private inventory belongs only to its holder. A selected human
    /// census still competes with animal demand. This is feasible nutrition under fixed access/temperature, not
    /// predicted jobs. Null selected means all consumers; an empty selection has no eaters. Throws
    /// <see cref="FoodFactsException"/>.
    /// </summary>
    public static FoodForecast ForecastFood(FoodSupply supply, IReadOnlyList<PawnId>? selected)
    {
        var demand = new Dictionary<PawnId, double>();
        var humanMeat = new Dictionary<PawnId, bool>();
        var ids = new List<PawnId>(supply.Consumers.Count);
        foreach (var consumer in supply.Consumers)
        {
            var rate = consumer.NutritionPerDay;
            if (demand.ContainsKey(consumer.Id) || !FoodId(consumer.Id.Value) || !FoodNumber(rate))
            {
                throw new FoodFactsException();
            }
            demand[consumer.Id] = rate;
            humanMeat[consumer.Id] = consumer.HumanMeatAcceptable;
            ids.Add(consumer.Id);
        }
        ids.Sort();
        double total = 0;
        foreach (var id in ids)
        {
            total += demand[id];
        }
        if (!FoodNumber(total) || total == 0)
        {
            throw new FoodFactsException();
        }
        var wanted = new HashSet<PawnId>();
        foreach (var id in selected ?? ids)
        {
            if (!demand.ContainsKey(id) || !wanted.Add(id))
            {
                throw new FoodFactsException();
            }
        }
        var stocks = new List<ForecastStock>(supply.Stocks.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var input in supply.Stocks)
        {
            var amount = input.Nutrition;
            var holder = input.Holder;
            if (!FoodId(input.Id) || seen.Contains(input.Id) || !FoodNumber(amount)
                || (input.Eaters.Count == 0 && !input.Corpse && !input.IsHumanMeat) || input.Eaters.Count > ids.Count)
            {
                throw new FoodFactsException();
            }
            seen.Add(input.Id);
            if (!holder.IsEmpty && (!demand.ContainsKey(holder) || input.Eaters.Count != 1 || input.Eaters[0] != holder))
            {
                throw new FoodFactsException();
            }
            var entry = new ForecastStock { Id = input.Id, Holder = holder, Amount = amount, Expiry = double.PositiveInfinity };
            if (input.Rot is FoodRot.Rots { Ticks: var ticks })
            {
                if (ticks < 0)
                {
                    throw new FoodFactsException();
                }
                entry.Expiry = (double)ticks / GameTime.TicksPerDay;
            }
            foreach (var id in input.Eaters)
            {
                if (!demand.ContainsKey(id) || !entry.Eaters.Add(id))
                {
                    throw new FoodFactsException();
                }
            }
            foreach (var id in ids)
            {
                if ((input.IsHumanMeat || input.IsHumanlike) && !humanMeat[id])
                {
                    entry.Eaters.Remove(id);
                }
                if (entry.Eaters.Contains(id))
                {
                    entry.EligibleDemand += demand[id];
                }
            }
            if (!FoodNumber(entry.EligibleDemand))
            {
                throw new FoodFactsException();
            }
            if (input.Corpse && input.Forbidden)
            {
                continue;
            }
            if (!input.Reserve)
            {
                stocks.Add(entry);
            }
        }
        stocks = [.. stocks.OrderBy(s => s.Expiry).ThenBy(s => s.Id, StringComparer.Ordinal)];
        FoodRunway runwayDays = new FoodRunway.NoEaters();
        double usableTotal = 0;
        double atRisk = 0;
        double inventory = 0;
        var rows = new List<ConsumerFoodForecast>();
        foreach (var entry in stocks)
        {
            if (!entry.Holder.IsEmpty && wanted.Contains(entry.Holder) && entry.Eaters.Contains(entry.Holder))
            {
                inventory += entry.Amount;
            }
        }
        foreach (var id in ids)
        {
            var rate = demand[id];
            if (rate == 0 || !wanted.Contains(id))
            {
                continue;
            }
            double runway = 0;
            double usableNutrition = 0;
            double allocated = 0;
            foreach (var entry in stocks)
            {
                if (!entry.Eaters.Contains(id))
                {
                    continue;
                }
                var share = entry.Amount * (rate / entry.EligibleDemand);
                if (entry.Holder == id)
                {
                    share = entry.Amount;
                }
                var usable = Math.Min(share, Math.Max(0, entry.Expiry - runway) * rate);
                allocated += share;
                usableNutrition += usable;
                runway += usable / rate;
            }
            if (!FoodNumber(runway) || !FoodNumber(usableNutrition) || !FoodNumber(allocated))
            {
                throw new FoodFactsException();
            }
            if (runwayDays is not FoodRunway.Days { Value: var minimum } || runway < minimum)
            {
                runwayDays = new FoodRunway.Days(runway);
            }
            usableTotal += usableNutrition;
            atRisk += Math.Max(0, allocated - usableNutrition);
            rows.Add(
                new ConsumerFoodForecast
                {
                    Id = id,
                    NutritionPerDay = rate,
                    RunwayDays = runway,
                    UsableNutrition = usableNutrition,
                    AllocatedNutrition = allocated,
                }
            );
        }
        if (!FoodNumber(usableTotal) || !FoodNumber(atRisk) || !FoodNumber(inventory))
        {
            throw new FoodFactsException();
        }
        return new FoodForecast
        {
            RunwayDays = runwayDays,
            UsableNutrition = usableTotal,
            AtRiskNutrition = atRisk,
            InventoryNutrition = inventory,
            Consumers = rows,
        };
    }
}
