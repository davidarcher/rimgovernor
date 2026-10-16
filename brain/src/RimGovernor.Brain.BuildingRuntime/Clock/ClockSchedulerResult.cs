namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// What one scheduler step did: the review, each planner's result (null when it did not run) and the proposals
/// the coordinator decided. Go merged planner results by reflection; <see cref="MergeFrom"/> copies each typed
/// result explicitly.
/// </summary>
public sealed class ClockSchedulerResult
{
    public bool Immediate { get; set; }

    /// <summary>When the step began: everything the event poll had reviewed before it is what the step planned against.</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>Whether the clock was found running when the step read it (reported by the run driver).</summary>
    public bool Running { get; set; }

    public RoundsResult? Rounds { get; set; }

    public RoundsFieldResult? Fields { get; set; }

    public RoundsAcquisitionResult? ResourceAcquisition { get; set; }

    public RoundsWorkResult? Work { get; set; }

    public RoundsSupplyResult? Supplies { get; set; }

    public RoundsPollutionResult? Pollution { get; set; }

    public RoundsBuildingResult? MechCharger { get; set; }

    public RoundsBuildingResult? GeneBank { get; set; }

    public RoundsLedgerResult? Ledger { get; set; }

    public RoundsArmoryResult? Armory { get; set; }

    public RoundsBuildingResult? Sleeping { get; set; }

    public RoundsBuildingResult? Comfort { get; set; }

    public RoundsBuildingResult? BasicComfort { get; set; }

    public RoundsBuildingResult? Workshop { get; set; }

    public RoundsBuildingResult? Hospital { get; set; }

    public RoundsBuildingResult? SleepingUpkeep { get; set; }

    public RoundsBuildingResult? Expansion { get; set; }

    public RoundsBuildingResult? Power { get; set; }

    public RoundsBuildingResult? Temperature { get; set; }

    public RoundsBuildingResult? Lighting { get; set; }

    public RoundsBuildingResult? Flooring { get; set; }

    public RoundsBuildingResult? Routes { get; set; }

    public RoundsDefenseResult? Defense { get; set; }

    public RoundsTendResult? Tend { get; set; }

    public RoundsRescueResult? Rescue { get; set; }

    public RoundsGearResult? Gear { get; set; }

    public RoundsMedicalResult? Medical { get; set; }

    public RoundsSurgeryResult? Surgery { get; set; }

    public RoundsRecoveryResult? Recovery { get; set; }

    public RoundsPopulationCustodyResult? PopulationCustody { get; set; }

    public RoundsPopulationJoinerResult? PopulationJoiner { get; set; }

    public RoundsResourceResult? Resource { get; set; }

    public RoundsHomeCoverageResult? HomeCoverage { get; set; }

    public MaintainShelterResult? MaintainShelter { get; set; }

    public RoundsFirebreakResult? Firebreak { get; set; }

    public RoundsPsylinkResult? Psylink { get; set; }

    public RoundsPermitsResult? Permits { get; set; }

    public RoundsGatheringResult? Gathering { get; set; }

    public RoundsStoneShellResult? StoneShell { get; set; }

    public RoundsStockpileResult? Stockpiles { get; set; }

    public RoundsDefenseLayoutResult? DefenseLayout { get; set; }

    public RoundsBurialResult? Burial { get; set; }

    public RoundsEnvelopeResult? Envelope { get; set; }

    public RoundsMoodReliefResult? MoodRelief { get; set; }

    public RoundsDialogResult? Dialog { get; set; }

    public RoundsTradeResult? Trade { get; set; }

    /// <summary>Whether the step deferred its run to the Worker's turn.</summary>
    public bool Deferred { get; set; }

    public TimeSpan Journal { get; set; }

    public List<Exception> PlannerFailures { get; } = [];

    public List<string> Planners { get; set; } = [];

    public List<string> Waiting { get; set; } = [];

    public List<string> MissedCutoff { get; } = [];

    /// <summary>Each family's run (#3250), by family id.</summary>
    public Dictionary<string, FamilyRun> Families { get; } = new(StringComparer.Ordinal);

    public List<string> HeldBy { get; } = [];

    public TimeSpan CriticalWave { get; set; }

    public Dictionary<string, double> PlannerMs { get; set; } = new(StringComparer.Ordinal);

    public StepReason Reason { get; set; } = new();

    /// <summary>The pace the step's clock status reported.</summary>
    public StepPacing Pacing { get; set; } = new();

    /// <summary>Copies every planner result <paramref name="other"/> holds into this one, leaving the ones it does not.</summary>
    internal void MergeFrom(ClockSchedulerResult other)
    {
        Fields = other.Fields ?? Fields;
        ResourceAcquisition = other.ResourceAcquisition ?? ResourceAcquisition;
        Work = other.Work ?? Work;
        Supplies = other.Supplies ?? Supplies;
        Pollution = other.Pollution ?? Pollution;
        MechCharger = other.MechCharger ?? MechCharger;
        GeneBank = other.GeneBank ?? GeneBank;
        Ledger = other.Ledger ?? Ledger;
        Armory = other.Armory ?? Armory;
        Sleeping = other.Sleeping ?? Sleeping;
        Comfort = other.Comfort ?? Comfort;
        BasicComfort = other.BasicComfort ?? BasicComfort;
        Workshop = other.Workshop ?? Workshop;
        Hospital = other.Hospital ?? Hospital;
        SleepingUpkeep = other.SleepingUpkeep ?? SleepingUpkeep;
        Expansion = other.Expansion ?? Expansion;
        Power = other.Power ?? Power;
        Temperature = other.Temperature ?? Temperature;
        Lighting = other.Lighting ?? Lighting;
        Flooring = other.Flooring ?? Flooring;
        Routes = other.Routes ?? Routes;
        Defense = other.Defense ?? Defense;
        Tend = other.Tend ?? Tend;
        Rescue = other.Rescue ?? Rescue;
        Gear = other.Gear ?? Gear;
        Medical = other.Medical ?? Medical;
        Surgery = other.Surgery ?? Surgery;
        Recovery = other.Recovery ?? Recovery;
        PopulationCustody = other.PopulationCustody ?? PopulationCustody;
        PopulationJoiner = other.PopulationJoiner ?? PopulationJoiner;
        Resource = other.Resource ?? Resource;
        HomeCoverage = other.HomeCoverage ?? HomeCoverage;
        MaintainShelter = other.MaintainShelter ?? MaintainShelter;
        Firebreak = other.Firebreak ?? Firebreak;
        Psylink = other.Psylink ?? Psylink;
        Permits = other.Permits ?? Permits;
        Gathering = other.Gathering ?? Gathering;
        StoneShell = other.StoneShell ?? StoneShell;
        Stockpiles = other.Stockpiles ?? Stockpiles;
        DefenseLayout = other.DefenseLayout ?? DefenseLayout;
        Burial = other.Burial ?? Burial;
        Envelope = other.Envelope ?? Envelope;
        MoodRelief = other.MoodRelief ?? MoodRelief;
        Dialog = other.Dialog ?? Dialog;
        Trade = other.Trade ?? Trade;
        foreach (var (family, run) in other.Families)
        {
            Families[family] = run;
        }
    }
}
