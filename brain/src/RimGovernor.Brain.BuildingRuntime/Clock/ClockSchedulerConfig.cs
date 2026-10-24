namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// What a scheduler needs: the rounder, the planners it queues and the step bounds. The native clock stays in the
/// mod, so it carries no profile, run-start parameters, pace horizon, player-speed following or player-quiet bounds.
/// </summary>
public sealed class ClockSchedulerConfig
{
    /// <summary>The oldest a read may be when it is used.</summary>
    public TimeSpan MaxAge { get; init; }

    /// <summary>Whether the Worker dispatches orders; gates the rules window and the dispatch turn.</summary>
    public bool Worker { get; init; }

    /// <summary>How long review steps may run the due queue alone before the safety net promotes one to a full step; zero means <see cref="ClockScheduler.DefaultFullStepEvery"/>.</summary>
    public TimeSpan FullStepEvery { get; init; }

    /// <summary>
    /// The rounds cadence (#3556): how many game ticks a committed rounds review stands before a review step runs the
    /// rounder again. One in-game hour, the planners' own default cadence. Between two rounds reviews a review step
    /// runs its dirty and due planners against the last committed review, and a wake (an early review) runs every
    /// planner against it; a step that runs every planner for a full step or an authority change reviews first
    /// regardless. The rounds review is the step's cost (derive, acquisition, the coupled search), and its inputs
    /// rarely change in the 600 ticks between reviews.
    /// </summary>
    public const long DefaultRoundsEveryTicks = GameTime.TicksPerHour;

    /// <summary>The game ticks a rounds review stands before a review step reviews again; must be positive.</summary>
    public long RoundsEveryTicks { get; init; } = DefaultRoundsEveryTicks;

    public StepBudget Budget { get; init; } = new();

    public Faults Faults { get; init; } = new();

    public FactStore? Store { get; init; }

    public Rounder? Rounds { get; init; }

    public RoundsDefensePlanner? Defense { get; init; }

    /// <summary>The families (#3250), each one due-queue entry after the catalog planners; none is wired yet.</summary>
    public IReadOnlyList<FamilyRunner> Families { get; init; } = [];

    /// <summary>Whether the rounds methods run.</summary>
    public bool RoundsMethods { get; init; }

    /// <summary>Runs after a step reads the world: true when the world was rebuilt since the last, so the next step reviews in full.</summary>
    public Func<C.ObservationContext, CancellationToken, Task<bool>>? WorldReady { get; init; }
}
