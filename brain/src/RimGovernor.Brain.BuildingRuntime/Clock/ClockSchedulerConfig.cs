namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// What a scheduler needs: the rounder, the planners it queues and the step bounds. The native clock stays in the
/// mod, so the profile, run-start parameters, pace horizon, player-speed following and player-quiet bounds of
/// the Go configuration are not carried.
/// </summary>
public sealed class ClockSchedulerConfig
{
    /// <summary>The oldest a read may be when it is used.</summary>
    public TimeSpan MaxAge { get; init; }

    /// <summary>Whether the Worker dispatches orders; gates the rules window and the dispatch turn.</summary>
    public bool Worker { get; init; }

    /// <summary>How long a timer step may skip the planners before the safety net promotes one to a full step; zero means <see cref="ClockScheduler.DefaultFullStepEvery"/>.</summary>
    public TimeSpan FullStepEvery { get; init; }

    public StepBudget Budget { get; init; } = new();

    public Faults Faults { get; init; } = new();

    public FactStore? Store { get; init; }

    public Rounder? Rounds { get; init; }

    public RoundsWorkPlanner? Work { get; init; }

    public RoundsPollutionPlanner? Pollution { get; init; }

    public RoundsMechChargerPlanner? MechCharger { get; init; }

    public RoundsGeneBankPlanner? GeneBank { get; init; }


    public RoundsBuildingPlanner? Sleeping { get; init; }

    public RoundsHospitalPlanner? Hospital { get; init; }

    public RoundsSleepingUpkeepPlanner? SleepingUpkeep { get; init; }

    public RoundsBuildingPlanner? Expansion { get; init; }

    public RoundsBuildingPlanner? Power { get; init; }

    public RoundsBuildingPlanner? Temperature { get; init; }

    public RoundsBuildingPlanner? Routes { get; init; }

    public RoundsDefensePlanner? Defense { get; init; }

    public RoundsTendPlanner? Tend { get; init; }

    public RoundsRescuePlanner? Rescue { get; init; }

    public RoundsMedicalPlanner? Medical { get; init; }

    public RoundsSurgeryPlanner? Surgery { get; init; }

    public RoundsRecoveryPlanner? Recovery { get; init; }

    public MaintainShelterPlanner? MaintainShelter { get; init; }

    public RoundsPsylinkPlanner? Psylink { get; init; }

    public RoundsPermitsPlanner? Permits { get; init; }


    public RoundsStockpilePlanner? Stockpiles { get; init; }

    public RoundsDefenseLayoutPlanner? DefenseLayout { get; init; }

    public RoundsMoodReliefPlanner? MoodRelief { get; init; }

    public RoundsDialogPlanner? Dialog { get; init; }

    public RoundsTradePlanner? Trade { get; init; }

    /// <summary>The families (#3250), each one due-queue entry after the catalog planners; none is wired yet.</summary>
    public IReadOnlyList<FamilyRunner> Families { get; init; } = [];

    /// <summary>Whether the rounds methods run.</summary>
    public bool RoundsMethods { get; init; }

    /// <summary>Runs after a step reads the world: true when the world was rebuilt since the last, so the next step reviews in full.</summary>
    public Func<C.ObservationContext, CancellationToken, Task<bool>>? WorldReady { get; init; }
}
