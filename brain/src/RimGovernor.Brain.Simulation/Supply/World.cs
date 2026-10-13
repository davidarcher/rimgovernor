using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Simulation.Supply;

// Port of Go internal/supplysim (world.go): a day-by-day stock-flow simulator for colony supply. It models
// flows and constraints, never pawn behaviour: goods have stock, consumers draw them down, sources deliver
// while opened and within a labor budget, and shocks change capacity or destroy stock. A planner opens and
// closes sources each day; World.Run returns a deterministic Report. Goods are plain strings so a resource
// world can register more.

/// <summary>Names a stocked good ("Nutrition", later "WoodLog", "Steel").</summary>
public readonly record struct Good(string Value)
{
    public static readonly Good Nutrition = new("Nutrition");

    /// <summary>The trade currency good.</summary>
    public static readonly Good Silver = new("Silver");

    // Resource goods, named as the policy resource definitions.
    public static readonly Good Wood = new("WoodLog");
    public static readonly Good StoneChunks = new("StoneChunks");
    public static readonly Good StoneBlocks = new("StoneBlocks");
    public static readonly Good Steel = new("Steel");
    public static readonly Good Plasteel = new("Plasteel");
    public static readonly Good Components = new("ComponentIndustrial");
    public static readonly Good Cotton = new("Cotton");
    public static readonly Good Leather = new("Leather");

    public override string ToString() => Value;
}

/// <summary>Planning constants of the simulator (Go package-level consts of supplysim).</summary>
public static class SupplyConstants
{
    /// <summary>The planning labor per worker per game day in pawn ticks, as in buildingruntime/rounds_food_plan.go (eight hours of a day).</summary>
    public const double LaborPerWorkerDay = 20000.0;

    // Runway forecast constants from policy/resource_runway.go.
    public const int ForecastHistoryDays = 15;
    public const double ForecastHorizonDays = 5.0;

    // Work estimates per unit in pawn ticks, from policy/food_plan.go.
    public const double ForageWorkTicks = 2500.0;
    public const double HuntWorkTicks = 7500.0;

    // Labor priors in pawn ticks per unit yielded, copied from policy.AcquisitionLaborPerUnit
    // (policy/acquisition_catalog.go). Trade is 0.
    public const double ChopWork = 15.0;
    public const double MineWork = 20.0;
    public const double SalvageWork = 25.0;
    public const double LootWork = 5.0;

    /// <summary>Per unit of product.</summary>
    public const double ProduceWork = 60.0;
    public const double DeepDrillWork = 100.0;

    // Floors copied from policy.RoundsPolicy defaults (policy/rounds.go:155) and the scenario's own steel
    // and component floors (the derived needs a live colony supplies). Use them in fixtures as Floor values.
    public const double WoodMin = 120.0;
    public const double WoodTarget = 350.0;
    public const double WoodMax = 500.0;
    public const double SteelFloor = 200.0;
    public const double ComponentFloor = 10.0;

    // Modelling priors. The repo observes a fresh census each tick and models none of these dynamics, so
    // each is a stated assumption: tests assert direction and ordering, never these values.

    /// <summary>
    /// The share of a cluster's maximum wood regrown per day (about a mature tree per 200 trees-days: slow,
    /// so over-chopping shows).
    /// </summary>
    public const double TreeRegrowFraction = 0.005;

    /// <summary>The Core stonecutting recipe yield.</summary>
    public const double StoneBlocksPerChunk = 20.0;

    // Per-unit constants for the food source constructors.
    public const double FishingRegenFraction = 0.025; // of Max per day
    public const double FishPerFisherDay = 3.0;
    public const double FishLaborPerFisher = 15000.0;
    public const double CropHarvestWork = 400.0; // pawn ticks per cell harvested
    public const double KillsPerHunterDay = 1.0;
    public const double ProductWork = 300.0; // pawn ticks per animal per day
}

/// <summary>A consumer draws PerDay of a good each day, growing linearly by GrowthPerDay.</summary>
public sealed record Consumer(string Name, Good Good, double PerDay)
{
    public double GrowthPerDay { get; init; }

    /// <summary>Multiplies demand while Window is active (winter wood); applies only with WindowFactor &gt; 0 and a Window.Period &gt; 0.</summary>
    public Window Window { get; init; }

    public double WindowFactor { get; init; }

    /// <summary>The consumer's draw on the given day.</summary>
    public double Demand(int day)
    {
        var d = PerDay + (GrowthPerDay * day);
        if (WindowFactor > 0 && Window.Period > 0 && Window.Active(day))
        {
            d *= WindowFactor;
        }
        return d;
    }
}

/// <summary>
/// A latched stock floor: it latches when stock falls below Min and asks for Target until stock exceeds
/// Target. Max is the ceiling above which the good is over-stocked.
/// </summary>
public readonly record struct Floor(Good Good, double Min, double Target, double Max);

/// <summary>
/// An open construction action: from Day it draws Costs from stock each day until paid in full, and is
/// visible to the planner while open.
/// </summary>
public sealed record Build(string Name, int Day, IReadOnlyList<Yield> Costs);

/// <summary>A floor's state at the start of a day.</summary>
public readonly record struct FloorView(Good Good, bool Latched, double Ask, bool Over, double Min, double Target, double Max);

/// <summary>An open build's remaining cost.</summary>
public sealed record BuildView(string Name, IReadOnlyDictionary<Good, double> Left);

/// <summary>The runway forecast for a good over the last ForecastHistoryDays of consumption, as policy.ForecastResourceRunway.</summary>
/// <param name="ConsumptionPerDay">The consumption rate.</param>
/// <param name="DaysLeft">Stock above the floor reserve plus unopened ore; -1 when no consumption.</param>
/// <param name="Deficit">Whether the good is short.</param>
public readonly record struct Forecast(double ConsumptionPerDay, double DaysLeft, bool Deficit);

/// <summary>What a planner asks of a source.</summary>
public enum CommandKind
{
    Open,
    Close,
}

/// <summary>Opens or closes one source. Opening restarts its lead.</summary>
public readonly record struct Command(CommandKind Kind, string Source);

/// <summary>What a planner sees of a source.</summary>
public sealed record SourceView
{
    public string Id { get; init; } = "";

    public bool Open { get; init; }

    /// <summary>Open, past its lead and not removed.</summary>
    public bool Delivering { get; init; }

    public bool Removed { get; init; }

    public int LeadLeft { get; init; }

    public double Stock { get; init; }

    public double Max { get; init; }

    /// <summary>Units delivered on the previous day.</summary>
    public double Last { get; init; }

    /// <summary>
    /// The units the source could deliver today at full labor (a crop: its harvest burst), after shocks; zero
    /// outside its window.
    /// </summary>
    public double Rate { get; init; }

    /// <summary>Marks a crop field.</summary>
    public bool Crop { get; init; }

    /// <summary>A crop's remaining growing days.</summary>
    public int GrowLeft { get; init; }
}

/// <summary>The planner's read of the world at the start of a day.</summary>
public sealed record WorldView
{
    public int Day { get; init; }

    public Tick Tick { get; init; }

    public IReadOnlyDictionary<Good, double> Stock { get; init; } = new Dictionary<Good, double>();

    /// <summary>Today's draw per good.</summary>
    public IReadOnlyDictionary<Good, double> Demand { get; init; } = new Dictionary<Good, double>();

    /// <summary>Days of stock at today's demand.</summary>
    public IReadOnlyDictionary<Good, double> Runway { get; init; } = new Dictionary<Good, double>();

    public double LaborBudget { get; init; }

    public IReadOnlyList<SourceView> Sources { get; init; } = [];

    public bool Threat { get; init; }

    public IReadOnlyList<FloorView> Floors { get; init; } = [];

    public IReadOnlyList<BuildView> Builds { get; init; } = [];

    /// <summary>Present for floored goods once a full day of history exists; empty before.</summary>
    public IReadOnlyDictionary<Good, Forecast> Forecast { get; init; } = new Dictionary<Good, Forecast>();
}

/// <summary>Decides which sources to open or close.</summary>
public interface IPlanner
{
    IReadOnlyList<Command> Plan(WorldView view);
}

/// <summary>Adapts a function to <see cref="IPlanner"/> (Go <c>PlannerFunc</c>).</summary>
public sealed class PlannerFunc(Func<WorldView, IReadOnlyList<Command>> plan) : IPlanner
{
    public IReadOnlyList<Command> Plan(WorldView view) => plan(view);
}

/// <summary>The simulator input. Run works on a copy; it never mutates it.</summary>
public sealed partial record World
{
    public ulong Seed { get; init; }

    public int Workers { get; init; }

    public IReadOnlyDictionary<Good, double> Stock { get; init; } = new Dictionary<Good, double>();

    public IReadOnlyList<Consumer> Consumers { get; init; } = [];

    public IReadOnlyList<Source> Sources { get; init; } = [];

    public IReadOnlyList<Shock> Shocks { get; init; } = [];

    /// <summary>The fraction of a good's stock lost per day.</summary>
    public IReadOnlyDictionary<Good, double> Spoilage { get; init; } = new Dictionary<Good, double>();

    /// <summary>The grid supply shared by sources with a PowerDraw.</summary>
    public double Power { get; init; }

    /// <summary>The stock ceiling per good; absent means unbounded.</summary>
    public IReadOnlyDictionary<Good, double> Capacity { get; init; } = new Dictionary<Good, double>();

    /// <summary>Latched stock floors a planner reads (MaintainResource).</summary>
    public IReadOnlyList<Floor> Floors { get; init; } = [];

    /// <summary>Construction shortfall edges.</summary>
    public IReadOnlyList<Build> Builds { get; init; } = [];

    /// <summary>The pawn ticks available per day.</summary>
    public double LaborBudget => Workers * SupplyConstants.LaborPerWorkerDay;
}
