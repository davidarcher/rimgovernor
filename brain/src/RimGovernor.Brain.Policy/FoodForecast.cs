using System.Text;

namespace RimGovernor.Brain.Policy;

/// <summary>Go <c>ErrFoodFacts</c>: the food forecast inputs are unavailable or invalid.</summary>
public sealed class FoodFactsException() : DomainException("food forecast inputs unavailable or invalid");

public sealed record FoodConsumer
{
    public PawnId Id { get; init; }

    public Fact<double> NutritionPerDay { get; init; }

    /// <summary>Includes animals whose native diet accepts it. Unknown is not consent.</summary>
    public Fact<bool> HumanMeatAcceptable { get; init; }
}

public sealed record FoodStock
{
    public string Id { get; init; } = "";

    /// <summary>Includes meals containing humanlike meat, not just raw meat.</summary>
    public bool IsHumanMeat { get; init; }

    public bool RawMeat { get; init; }

    public bool IsHumanlike { get; init; }

    public bool Vegetable { get; init; }

    public Fact<FoodIngredientClass> RawClass { get; init; }

    /// <summary>Forbidden pemmican or survival meals; eligibility is after release.</summary>
    public bool Reserve { get; init; }

    /// <summary>Known empty holder means shared stock. Unknown ownership is not shared.</summary>
    public Fact<PawnId> Holder { get; init; }

    public Fact<double> Nutrition { get; init; }

    public IReadOnlyList<PawnId> Eaters { get; init; } = [];

    public Fact<bool> Perishable { get; init; }

    public Fact<long> RotTicks { get; init; }

    /// <summary>
    /// DefName and Count are only needed to select a specific feed resource (the animal feed runway's item
    /// choice); ForecastFood itself never reads them, so unknown/zero values never affect forecast validity.
    /// </summary>
    public Resource DefName { get; init; }

    public Fact<long> Count { get; init; }

    /// <summary>The catalog mass used only for caravan packing.</summary>
    public Fact<double> UnitMass { get; init; }

    /// <summary>
    /// Roofed, TemperatureC and Room describe where the stock sits at observation time; ForecastFood ignores
    /// them, storage upkeep and refrigeration policy read them.
    /// </summary>
    public Fact<bool> Roofed { get; init; }

    public Fact<double> TemperatureC { get; init; }

    public Fact<string> Room { get; init; }

    /// <summary>Corpse nutrition is its native meat yield. Forbidden corpses remain observable reserves but do not extend the food runway.</summary>
    public bool Corpse { get; init; }

    public Fact<bool> Forbidden { get; init; }

    public Fact<double> MeatAmount { get; init; }

    public Fact<double> BodySize { get; init; }

    public Fact<long> TileFootprint { get; init; }
}

public sealed record FoodSupply
{
    public Fact<bool> Complete { get; init; }

    public IReadOnlyList<FoodConsumer> Consumers { get; init; } = [];

    public IReadOnlyList<FoodStock> Stocks { get; init; } = [];

    /// <summary>Forbidden ordinary food stacks: census rows for the storage review only, never forecast or reserve input.</summary>
    public IReadOnlyList<FoodStock> Barred { get; init; } = [];

    public Fact<FoodLarder> Larder { get; init; }
}

public sealed record ConsumerFoodForecast
{
    public PawnId Id { get; init; }

    public double RunwayDays { get; init; }

    public double UsableNutrition { get; init; }

    public double AllocatedNutrition { get; init; }

    public double NutritionPerDay { get; init; }
}

public sealed record FoodForecast
{
    public Fact<double> RunwayDays { get; init; }

    public double UsableNutrition { get; init; }

    public double AtRiskNutrition { get; init; }

    public double InventoryNutrition { get; init; }

    public IReadOnlyList<ConsumerFoodForecast> Consumers { get; init; } = [];

    /// <summary>Non-colonist rows below the minimum runway once GateOnColonists has taken the colony runway over colonists only.</summary>
    public IReadOnlyList<ConsumerFoodForecast> PetShortfalls { get; init; } = [];

    /// <summary>
    /// Go <c>FoodForecast.GateOnColonists</c>: returns the forecast with RunwayDays taken over the colonists'
    /// rows only: one unfed pet must not read the colony as starving. Every other row whose runway falls
    /// below minDays is reported in PetShortfalls as its own need. The allocation itself is unchanged, so pets
    /// still compete for shared stock. An empty census keeps the all-consumer minimum, and a census with no
    /// fed rows leaves the runway unknown.
    /// </summary>
    public FoodForecast GateOnColonists(IReadOnlyList<PawnId> colonists, double minDays)
    {
        if (colonists.Count == 0)
        {
            return this;
        }
        var human = new HashSet<PawnId>(colonists);
        var runway = Unknown<double>();
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
            if (!runway.TryGetValue(out var minimum) || row.RunwayDays < minimum)
            {
                runway = Known(row.RunwayDays);
            }
        }
        return this with { RunwayDays = runway, PetShortfalls = pets };
    }
}

public static partial class PolicyFunctions
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
    /// Go <c>ForecastFood</c>: allocates each stock among its observed eligible consumers by demand, then
    /// consumes allocations in expiry order. Private inventory belongs only to its holder. A selected human
    /// census still competes with animal demand. This is feasible nutrition under fixed access/temperature, not
    /// predicted jobs. Null selected means all consumers; an empty selection has unknown runway. Throws
    /// <see cref="FoodFactsException"/> where Go returned ErrFoodFacts.
    /// </summary>
    public static FoodForecast ForecastFood(FoodSupply supply, IReadOnlyList<PawnId>? selected)
    {
        if (!supply.Complete.TryGetValue(out var complete) || !complete)
        {
            throw new FoodFactsException();
        }
        var demand = new Dictionary<PawnId, double>();
        var humanMeat = new Dictionary<PawnId, bool>();
        var ids = new List<PawnId>(supply.Consumers.Count);
        foreach (var consumer in supply.Consumers)
        {
            var known = consumer.NutritionPerDay.TryGetValue(out var rate);
            if (demand.ContainsKey(consumer.Id) || !FoodId(consumer.Id.Value) || !known || !FoodNumber(rate))
            {
                throw new FoodFactsException();
            }
            demand[consumer.Id] = rate;
            humanMeat[consumer.Id] = consumer.HumanMeatAcceptable.ValueOrDefault;
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
            var ak = input.Nutrition.TryGetValue(out var amount);
            var hk = input.Holder.TryGetValue(out var holder);
            var pk = input.Perishable.TryGetValue(out var perishable);
            if (!FoodId(input.Id) || seen.Contains(input.Id) || !ak || !FoodNumber(amount) || !hk || !pk
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
            if (perishable)
            {
                var tk = input.RotTicks.TryGetValue(out var ticks);
                if (!tk || ticks < 0)
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
            if (input.Corpse)
            {
                if (!input.Forbidden.TryGetValue(out var forbidden))
                {
                    throw new FoodFactsException();
                }
                if (forbidden)
                {
                    continue;
                }
            }
            if (!input.Reserve)
            {
                stocks.Add(entry);
            }
        }
        stocks = [.. stocks.OrderBy(s => s.Expiry).ThenBy(s => s.Id, StringComparer.Ordinal)];
        var runwayDays = Unknown<double>();
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
            if (!runwayDays.TryGetValue(out var minimum) || runway < minimum)
            {
                runwayDays = Known(runway);
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
