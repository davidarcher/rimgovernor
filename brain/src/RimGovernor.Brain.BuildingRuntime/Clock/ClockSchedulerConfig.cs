namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// What a scheduler needs: the rounder, the planners it queues and the step bounds. The native clock stays in the
/// mod, so the profile, window-start parameters, pace horizon, player-speed following and player-quiet bounds of
/// the Go configuration are not carried.
/// </summary>
public sealed class ClockSchedulerConfig
{
    public RoundsBuildingPlanner? Butcher { get; init; }

    public RoundsFieldPlanner? Fields { get; init; }

    /// <summary>The longest window the step may lend.</summary>
    public uint MaxTicks { get; init; }

    /// <summary>The oldest a read may be when it is used.</summary>
    public TimeSpan MaxAge { get; init; }

    /// <summary>Whether the Worker dispatches orders; gates the rules window and the dispatch turn.</summary>
    public bool Worker { get; init; }

    public uint CombatMaxTicks { get; init; }

    /// <summary>How long a timer step may skip the planners before the safety net promotes one to a full step; zero means <see cref="ClockScheduler.DefaultFullStepEvery"/>.</summary>
    public TimeSpan FullStepEvery { get; init; }

    public StepBudget Budget { get; init; } = new();

    public Faults Faults { get; init; } = new();

    public FactStore? Store { get; init; }

    public Rounder? Rounds { get; init; }

    public RoundsAcquisitionPlanner? FoodAcquisition { get; init; }

    public RoundsAcquisitionPlanner? ResourceAcquisition { get; init; }

    public RoundsAcquisitionPlanner? PestAcquisition { get; init; }

    public RoundsWorkPlanner? Work { get; init; }

    public RoundsSupplyPlanner? Supplies { get; init; }

    public RoundsBlightPlanner? Blight { get; init; }

    public RoundsPollutionPlanner? Pollution { get; init; }

    public RoundsMechChargerPlanner? MechCharger { get; init; }

    public RoundsGeneBankPlanner? GeneBank { get; init; }

    public RoundsLedgerPlanner? Ledger { get; init; }

    public RoundsArmoryPlanner? Armory { get; init; }

    public RoundsClearancePlanner? Clearance { get; init; }

    public RoundsShrinePlanner? Shrine { get; init; }

    public RoundsBuildingPlanner? Sleeping { get; init; }

    public RoundsBuildingPlanner? Cooking { get; init; }

    public RoundsBuildingPlanner? Comfort { get; init; }

    public RoundsBuildingPlanner? BasicComfort { get; init; }

    public RoundsBuildingPlanner? Workshop { get; init; }

    public RoundsHospitalPlanner? Hospital { get; init; }

    public RoundsSleepingUpkeepPlanner? SleepingUpkeep { get; init; }

    public RoundsBuildingPlanner? Expansion { get; init; }

    public RoundsBuildingPlanner? Power { get; init; }

    public RoundsBuildingPlanner? Temperature { get; init; }

    public RoundsBuildingPlanner? Refrigeration { get; init; }

    public RoundsBuildingPlanner? Lighting { get; init; }

    public RoundsBuildingPlanner? Flooring { get; init; }

    public RoundsBuildingPlanner? Routes { get; init; }

    public RoundsDefensePlanner? Defense { get; init; }

    public RoundsTendPlanner? Tend { get; init; }

    public RoundsRescuePlanner? Rescue { get; init; }

    public RoundsEquipPlanner? Equip { get; init; }

    public RoundsRepairPlanner? Repair { get; init; }

    public RoundsFireSafetyPlanner? FireSafety { get; init; }

    public RoundsGearPlanner? Gear { get; init; }

    public RoundsMedicalPlanner? Medical { get; init; }

    public RoundsSurgeryPlanner? Surgery { get; init; }

    public RoundsFoodStorageUpkeepPlanner? FoodStorageUpkeep { get; init; }

    public RoundsAnimalContainmentPlanner? AnimalContainment { get; init; }

    public RoundsRecoveryPlanner? Recovery { get; init; }

    public RoundsHusbandryPlanner? Husbandry { get; init; }

    public RoundsRulesPlanner? Rules { get; init; }

    public RoundsPrisonerInteractionPlanner? PrisonerInteraction { get; init; }

    public RoundsPopulationCustodyPlanner? PopulationCustody { get; init; }

    public RoundsPopulationJoinerPlanner? PopulationJoiner { get; init; }

    public RoundsResearchPlanner? Research { get; init; }

    public RoundsResourcePlanner? Resource { get; init; }

    public RoundsHomeCoveragePlanner? HomeCoverage { get; init; }

    public MaintainShelterPlanner? MaintainShelter { get; init; }

    public RoundsFirebreakPlanner? Firebreak { get; init; }

    public RoundsPsylinkPlanner? Psylink { get; init; }

    public RoundsCreepJoinerPlanner? CreepJoiners { get; init; }

    public RoundsPermitsPlanner? Permits { get; init; }

    public RoundsIdeoRolesPlanner? IdeoRoles { get; init; }

    public RoundsIdeoligionPlanner? Ideoligion { get; init; }

    public RoundsRitualsPlanner? Rituals { get; init; }

    public RoundsGatheringPlanner? Gathering { get; init; }

    public RoundsStoneShellPlanner? StoneShell { get; init; }

    public RoundsStockpilePlanner? Stockpiles { get; init; }

    public RoundsDefenseLayoutPlanner? DefenseLayout { get; init; }

    public RoundsBurialPlanner? Burial { get; init; }

    public RoundsTrainingPlanner? Training { get; init; }

    public RoundsEnvelopePlanner? Envelope { get; init; }

    public RoundsIncinerationPlanner? Incineration { get; init; }

    public RoundsMoodReliefPlanner? MoodRelief { get; init; }

    public RoundsDialogPlanner? Dialog { get; init; }

    public RoundsTradePlanner? Trade { get; init; }

    /// <summary>Whether the rounds methods run (the lend of a native-work window needs them).</summary>
    public bool RoundsMethods { get; init; }

    /// <summary>Runs after a step reads the world: true when the world was rebuilt since the last, so the next step reviews in full.</summary>
    public Func<ObservationContext, CancellationToken, Task<bool>>? WorldReady { get; init; }
}
