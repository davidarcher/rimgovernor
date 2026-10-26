namespace RimGovernor.Brain.Policy;


/// <summary>
/// Groups resources by how fast a shortfall must refill. The refill window is the horizon over which a deficit
/// is to be made good, so the demand rate is deficit / window.
/// </summary>
public enum ResourceClass
{
    /// <summary>An order the rate model does not size (a bill read back from a bench, the beer reserve): it has no refill window.</summary>
    None,

    /// <summary>Spoils and is eaten daily: refill within a day.</summary>
    Food,

    /// <summary>Durable stock (steel, components, cloth, wood): refill within a few days.</summary>
    Material,
}

/// <summary>
/// One pawn's conditional potential at a bench: estimated hours per day and its work speed stat for the recipe
/// (1 is a baseline pawn).
/// </summary>
public sealed record BenchWorker
{
    public required double WorkHoursPerDay { get; init; }

    public required double WorkSpeed { get; init; }
}

/// <summary>
/// The recipe and bench side of the capacity model: the work amount one unit of product takes (RimWorld work
/// ticks at speed 1) and the bench's effective work speed factor. Both are def stats, always read.
/// </summary>
public sealed record BenchRecipe
{
    public required double WorkPerUnit { get; init; }

    public required double BenchWorkSpeed { get; init; }
}

/// <summary>How many benches an order wants: a count, or none can serve it because no bench makes anything.</summary>
public abstract record BenchesWanted
{
    private BenchesWanted()
    {
    }

    /// <summary>The benches that clear the demand at the capacity (zero for no demand).</summary>
    public sealed record Count(int Benches) : BenchesWanted;

    /// <summary>Positive demand against zero capacity: no number of benches meets it (no worker, or a bench that makes nothing).</summary>
    public sealed record NoCapacity : BenchesWanted;
}

public static class BenchRateRules
{
    // Default refill windows, in game days.
    public const double FoodRefillWindowDays = 1.0;
    public const double MaterialRefillWindowDays = 3.0;

    /// <summary>The class's default refill window. An unsized order has none: sizing one is a caller error.</summary>
    public static double RefillWindowDays(ResourceClass c)
    {
        switch (c)
        {
            case ResourceClass.Food:
                return FoodRefillWindowDays;
            case ResourceClass.Material:
                return MaterialRefillWindowDays;
            case ResourceClass.None:
                throw new DomainException("an order the rate model does not size has no refill window");
        }

        throw new DomainException($"undefined resource class {(int)c}");
    }

    /// <summary>
    /// Units per day needed to clear <paramref name="deficit"/> within the class's refill window. A non-positive deficit
    /// needs nothing. Throws on a deficit that is not finite.
    /// </summary>
    public static double DemandRate(double deficit, ResourceClass resourceClass)
    {
        if (!double.IsFinite(deficit))
        {
            throw new DomainException($"invalid deficit {deficit}");
        }

        return Math.Max(deficit, 0) / RefillWindowDays(resourceClass);
    }

    /// <summary>
    /// The units per day one bench produces with the given workers at the table: sum over workers of hours x ticks
    /// per hour x worker speed, times bench speed, divided by the recipe's work per unit. No workers is a zero
    /// capacity. Throws when the recipe work or bench speed is not positive and finite, or a worker's hours or speed
    /// is negative or not finite: each is a read the game always has.
    /// </summary>
    public static double BenchCapacity(BenchRecipe r, IEnumerable<BenchWorker> workers)
    {
        if (!PositiveFinite(r.WorkPerUnit) || !PositiveFinite(r.BenchWorkSpeed))
        {
            throw new DomainException($"invalid bench recipe work {r.WorkPerUnit} at speed {r.BenchWorkSpeed}");
        }

        var ticks = 0.0;
        foreach (var w in workers)
        {
            if (!NonNegativeFinite(w.WorkHoursPerDay) || !NonNegativeFinite(w.WorkSpeed))
            {
                throw new DomainException($"invalid bench worker {w.WorkHoursPerDay} h at speed {w.WorkSpeed}");
            }

            ticks += w.WorkHoursPerDay * GameTime.TicksPerHour * w.WorkSpeed;
        }

        return ticks * r.BenchWorkSpeed / r.WorkPerUnit;
    }

    /// <summary>
    /// ceil(demand / capacity) for demand and per-bench capacity in units per day. Zero demand wants no benches
    /// whatever the capacity; positive demand against zero capacity is <see cref="BenchesWanted.NoCapacity"/>. Throws
    /// on a negative or non-finite input.
    /// </summary>
    public static BenchesWanted Wanted(double demand, double capacity)
    {
        if (!NonNegativeFinite(demand) || !NonNegativeFinite(capacity))
        {
            throw new DomainException($"invalid bench demand {demand} at capacity {capacity}");
        }

        if (demand == 0)
        {
            return new BenchesWanted.Count(0);
        }

        if (capacity == 0)
        {
            return new BenchesWanted.NoCapacity();
        }

        var n = Math.Ceiling((demand / capacity) - 1e-9);
        return n > int.MaxValue ? new BenchesWanted.NoCapacity() : new BenchesWanted.Count((int)n);
    }

    internal static bool PositiveFinite(double v) => v > 0 && double.IsFinite(v);

    internal static bool NonNegativeFinite(double v) => v >= 0 && double.IsFinite(v);
}
