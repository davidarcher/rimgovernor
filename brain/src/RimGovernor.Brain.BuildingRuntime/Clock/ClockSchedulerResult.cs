namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The window a step asks for, in ticks.</summary>
public readonly record struct ClockWindowSize(uint Ticks);

/// <summary>
/// What one scheduler step did: the review, each planner's result (null when it did not run), the proposals
/// the coordinator decided and the native-work window the step asks the host for. Go merged planner results by
/// reflection; <see cref="MergeFrom"/> copies each typed result explicitly. The Go attempt pointer is not carried:
/// the host starts windows.
/// </summary>
public sealed class ClockSchedulerResult
{
    public bool Immediate { get; set; }

    /// <summary>Whether a window was found running when the step read the clock (reported by the host).</summary>
    public bool Running { get; set; }

    public ClockWindowSize Window { get; set; }

    public RoundsResult? Rounds { get; set; }

    public RoundsBuildingResult? Butcher { get; set; }

    public RoundsFieldResult? Fields { get; set; }

    public RoundsAcquisitionResult? FoodAcquisition { get; set; }

    public RoundsRulesResult? Rules { get; set; }

    public RoundsAcquisitionResult? ResourceAcquisition { get; set; }

    public RoundsAcquisitionResult? PestAcquisition { get; set; }

    public RoundsWorkResult? Work { get; set; }

    public RoundsSupplyResult? Supplies { get; set; }

    public RoundsBlightResult? Blight { get; set; }

    public RoundsPollutionResult? Pollution { get; set; }

    public RoundsBuildingResult? MechCharger { get; set; }

    public RoundsBuildingResult? GeneBank { get; set; }

    public RoundsLedgerResult? Ledger { get; set; }

    public RoundsArmoryResult? Armory { get; set; }

    public RoundsClearanceResult? Clearance { get; set; }

    public RoundsShrineResult? Shrine { get; set; }

    public RoundsBuildingResult? Sleeping { get; set; }

    public RoundsBuildingResult? Cooking { get; set; }

    public RoundsBuildingResult? Comfort { get; set; }

    public RoundsBuildingResult? BasicComfort { get; set; }

    public RoundsBuildingResult? Workshop { get; set; }

    public RoundsBuildingResult? Hospital { get; set; }

    public RoundsBuildingResult? SleepingUpkeep { get; set; }

    public RoundsBuildingResult? Expansion { get; set; }

    public RoundsBuildingResult? Power { get; set; }

    public RoundsBuildingResult? Temperature { get; set; }

    public RoundsBuildingResult? Refrigeration { get; set; }

    public RoundsBuildingResult? Lighting { get; set; }

    public RoundsBuildingResult? Flooring { get; set; }

    public RoundsBuildingResult? Routes { get; set; }

    public RoundsDefenseResult? Defense { get; set; }

    public RoundsTendResult? Tend { get; set; }

    public RoundsRescueResult? Rescue { get; set; }

    public RoundsEquipResult? Equip { get; set; }

    public RoundsRepairResult? Repair { get; set; }

    public RoundsFireSafetyResult? FireSafety { get; set; }

    public RoundsGearResult? Gear { get; set; }

    public RoundsMedicalResult? Medical { get; set; }

    public RoundsSurgeryResult? Surgery { get; set; }

    public RoundsFoodStorageUpkeepResult? FoodStorageUpkeep { get; set; }

    public RoundsAnimalContainmentResult? AnimalContainment { get; set; }

    public RoundsRecoveryResult? Recovery { get; set; }

    public RoundsHusbandryResult? Husbandry { get; set; }

    public RoundsPrisonerInteractionResult? PrisonerInteraction { get; set; }

    public RoundsPopulationCustodyResult? PopulationCustody { get; set; }

    public RoundsPopulationJoinerResult? PopulationJoiner { get; set; }

    public RoundsResearchResult? Research { get; set; }

    public RoundsResourceResult? Resource { get; set; }

    public RoundsHomeCoverageResult? HomeCoverage { get; set; }

    public MaintainShelterResult? MaintainShelter { get; set; }

    public RoundsFirebreakResult? Firebreak { get; set; }

    public RoundsPsylinkResult? Psylink { get; set; }

    public RoundsCreepJoinerResult? CreepJoiners { get; set; }

    public RoundsPermitsResult? Permits { get; set; }

    public RoundsIdeoRolesResult? IdeoRoles { get; set; }

    public RoundsIdeoligionResult? Ideoligion { get; set; }

    public RoundsRitualsResult? Rituals { get; set; }

    public RoundsGatheringResult? Gathering { get; set; }

    public RoundsStoneShellResult? StoneShell { get; set; }

    public RoundsStockpileResult? Stockpiles { get; set; }

    public RoundsDefenseLayoutResult? DefenseLayout { get; set; }

    public RoundsBurialResult? Burial { get; set; }

    public RoundsTrainingResult? Training { get; set; }

    public RoundsEnvelopeResult? Envelope { get; set; }

    public RoundsIncinerationResult? Incineration { get; set; }

    public RoundsMoodReliefResult? MoodRelief { get; set; }

    public RoundsDialogResult? Dialog { get; set; }

    public RoundsTradeResult? Trade { get; set; }

    /// <summary>Whether the step deferred its window to the Worker's turn.</summary>
    public bool Deferred { get; set; }

    public TimeSpan Journal { get; set; }

    public List<Exception> PlannerFailures { get; } = [];

    public List<string> Planners { get; set; } = [];

    public List<string> Waiting { get; set; } = [];

    public List<ProposalOutcome> Proposals { get; } = [];

    public List<string> MissedCutoff { get; } = [];

    public List<string> HeldBy { get; } = [];

    public TimeSpan CriticalWave { get; set; }

    public Dictionary<string, double> PlannerMs { get; set; } = new(StringComparer.Ordinal);

    public uint NativeWorkTicks { get; set; }

    public StepReason Reason { get; set; } = new();

    /// <summary>Copies every planner result <paramref name="other"/> holds into this one, leaving the ones it does not.</summary>
    internal void MergeFrom(ClockSchedulerResult other)
    {
        Butcher = other.Butcher ?? Butcher;
        Fields = other.Fields ?? Fields;
        FoodAcquisition = other.FoodAcquisition ?? FoodAcquisition;
        Rules = other.Rules ?? Rules;
        ResourceAcquisition = other.ResourceAcquisition ?? ResourceAcquisition;
        PestAcquisition = other.PestAcquisition ?? PestAcquisition;
        Work = other.Work ?? Work;
        Supplies = other.Supplies ?? Supplies;
        Blight = other.Blight ?? Blight;
        Pollution = other.Pollution ?? Pollution;
        MechCharger = other.MechCharger ?? MechCharger;
        GeneBank = other.GeneBank ?? GeneBank;
        Ledger = other.Ledger ?? Ledger;
        Armory = other.Armory ?? Armory;
        Clearance = other.Clearance ?? Clearance;
        Shrine = other.Shrine ?? Shrine;
        Sleeping = other.Sleeping ?? Sleeping;
        Cooking = other.Cooking ?? Cooking;
        Comfort = other.Comfort ?? Comfort;
        BasicComfort = other.BasicComfort ?? BasicComfort;
        Workshop = other.Workshop ?? Workshop;
        Hospital = other.Hospital ?? Hospital;
        SleepingUpkeep = other.SleepingUpkeep ?? SleepingUpkeep;
        Expansion = other.Expansion ?? Expansion;
        Power = other.Power ?? Power;
        Temperature = other.Temperature ?? Temperature;
        Refrigeration = other.Refrigeration ?? Refrigeration;
        Lighting = other.Lighting ?? Lighting;
        Flooring = other.Flooring ?? Flooring;
        Routes = other.Routes ?? Routes;
        Defense = other.Defense ?? Defense;
        Tend = other.Tend ?? Tend;
        Rescue = other.Rescue ?? Rescue;
        Equip = other.Equip ?? Equip;
        Repair = other.Repair ?? Repair;
        FireSafety = other.FireSafety ?? FireSafety;
        Gear = other.Gear ?? Gear;
        Medical = other.Medical ?? Medical;
        Surgery = other.Surgery ?? Surgery;
        FoodStorageUpkeep = other.FoodStorageUpkeep ?? FoodStorageUpkeep;
        AnimalContainment = other.AnimalContainment ?? AnimalContainment;
        Recovery = other.Recovery ?? Recovery;
        Husbandry = other.Husbandry ?? Husbandry;
        PrisonerInteraction = other.PrisonerInteraction ?? PrisonerInteraction;
        PopulationCustody = other.PopulationCustody ?? PopulationCustody;
        PopulationJoiner = other.PopulationJoiner ?? PopulationJoiner;
        Research = other.Research ?? Research;
        Resource = other.Resource ?? Resource;
        HomeCoverage = other.HomeCoverage ?? HomeCoverage;
        MaintainShelter = other.MaintainShelter ?? MaintainShelter;
        Firebreak = other.Firebreak ?? Firebreak;
        Psylink = other.Psylink ?? Psylink;
        CreepJoiners = other.CreepJoiners ?? CreepJoiners;
        Permits = other.Permits ?? Permits;
        IdeoRoles = other.IdeoRoles ?? IdeoRoles;
        Ideoligion = other.Ideoligion ?? Ideoligion;
        Rituals = other.Rituals ?? Rituals;
        Gathering = other.Gathering ?? Gathering;
        StoneShell = other.StoneShell ?? StoneShell;
        Stockpiles = other.Stockpiles ?? Stockpiles;
        DefenseLayout = other.DefenseLayout ?? DefenseLayout;
        Burial = other.Burial ?? Burial;
        Training = other.Training ?? Training;
        Envelope = other.Envelope ?? Envelope;
        Incineration = other.Incineration ?? Incineration;
        MoodRelief = other.MoodRelief ?? MoodRelief;
        Dialog = other.Dialog ?? Dialog;
        Trade = other.Trade ?? Trade;
    }
}
