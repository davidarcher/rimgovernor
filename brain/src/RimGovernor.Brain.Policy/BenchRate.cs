namespace RimGovernor.Brain.Policy;

// Go: bench_rate.go

/// <summary>
/// Groups resources by how fast a shortfall must refill. The refill window is the horizon over which a deficit
/// is to be made good, so the demand rate is deficit / window.
/// </summary>
public enum ResourceClass
{
    None,

    /// <summary>Spoils and is eaten daily: refill within a day.</summary>
    Food,

    /// <summary>Durable stock (steel, components, cloth, wood): refill within a few days.</summary>
    Material,
}

/// <summary>
/// One pawn assigned to a bench: the game hours per day it works and its work speed stat for the recipe
/// (1 is a baseline pawn).
/// </summary>
public sealed record BenchWorker
{
    public Fact<double> WorkHoursPerDay { get; init; }

    public Fact<double> WorkSpeed { get; init; }
}

/// <summary>
/// The recipe and bench side of the capacity model: the work amount one unit of product takes (RimWorld work
/// ticks at speed 1) and the bench's effective work speed factor.
/// </summary>
public sealed record BenchRecipe
{
    public Fact<double> WorkPerUnit { get; init; }

    public Fact<double> BenchWorkSpeed { get; init; }
}

public static partial class PolicyFunctions
{
    // Default refill windows, in game days.
    public const double FoodRefillWindowDays = 1.0;
    public const double MaterialRefillWindowDays = 3.0;

    /// <summary>The class's default refill window; unknown for a class the model does not name.</summary>
    public static Fact<double> RefillWindowDays(ResourceClass c) =>
        c switch
        {
            ResourceClass.Food => Known(FoodRefillWindowDays),
            ResourceClass.Material => Known(MaterialRefillWindowDays),
            _ => Unknown<double>(),
        };

    /// <summary>
    /// Units per day needed to clear <paramref name="deficit"/> within the class's refill window. A
    /// non-positive deficit needs nothing. Unknown when the deficit or the class window is unknown.
    /// </summary>
    public static Fact<double> DemandRate(Fact<double> deficit, ResourceClass resourceClass)
    {
        if (!deficit.TryGetValue(out var d) || double.IsNaN(d) || double.IsInfinity(d))
        {
            return Unknown<double>();
        }

        if (!RefillWindowDays(resourceClass).TryGetValue(out var w))
        {
            return Unknown<double>();
        }

        return Known(Math.Max(d, 0) / w);
    }

    /// <summary>
    /// The units per day one bench produces with the given workers at the table: sum over workers of hours x
    /// ticks per hour x worker speed, times bench speed, divided by the recipe's work per unit. Unknown when
    /// any input is unknown, the recipe work or bench speed is not positive, or a worker's hours or speed is
    /// negative or not finite. No workers is a known zero capacity.
    /// </summary>
    public static Fact<double> BenchCapacity(BenchRecipe r, IEnumerable<BenchWorker> workers)
    {
        if (!r.WorkPerUnit.TryGetValue(out var work) || !PositiveFinite(work))
        {
            return Unknown<double>();
        }

        if (!r.BenchWorkSpeed.TryGetValue(out var bench) || !PositiveFinite(bench))
        {
            return Unknown<double>();
        }

        var ticks = 0.0;
        foreach (var w in workers)
        {
            if (!w.WorkHoursPerDay.TryGetValue(out var h) || !NonNegativeFinite(h))
            {
                return Unknown<double>();
            }

            if (!w.WorkSpeed.TryGetValue(out var s) || !NonNegativeFinite(s))
            {
                return Unknown<double>();
            }

            ticks += h * GameTime.TicksPerHour * s;
        }

        return Known(ticks * bench / work);
    }

    /// <summary>
    /// ceil(demand / capacity) for demand and per-bench capacity in units per day. Zero demand wants no
    /// benches whatever the capacity; positive demand against zero capacity is unknown, not infinite.
    /// </summary>
    public static Fact<int> BenchesWanted(Fact<double> demand, Fact<double> capacity)
    {
        if (!demand.TryGetValue(out var d) || !NonNegativeFinite(d))
        {
            return Unknown<int>();
        }

        if (!capacity.TryGetValue(out var c) || !NonNegativeFinite(c))
        {
            return Unknown<int>();
        }

        if (d == 0)
        {
            return Known(0);
        }

        if (c == 0)
        {
            return Unknown<int>();
        }

        var n = Math.Ceiling((d / c) - 1e-9);
        return n > int.MaxValue ? Unknown<int>() : Known((int)n);
    }

    internal static bool PositiveFinite(double v) => v > 0 && !double.IsInfinity(v) && !double.IsNaN(v);

    internal static bool NonNegativeFinite(double v) => v >= 0 && !double.IsInfinity(v) && !double.IsNaN(v);
}
