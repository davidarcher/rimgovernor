using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The shared native pawn rows the tests stand pawns with: a whole PawnState (every required section, each in its
/// "nothing here" state) that a test then changes with <c>with</c>, and the policy inputs a pawn row carries.
/// </summary>
internal static class PawnFixtures
{
    /// <summary>Full health: no hediffs, no bleeding, in no bed, no surgery bills, missing parts or operations.</summary>
    public static readonly RimGovernor.Brain.GameFacts.Observations.PawnHealth Healthy = new()
    {
        SummaryFraction = 1,
        NeedsTend = false,
        Bleeding = false,
        BleedRatePerDay = 0,
        BloodLoss = 0,
        BleedOut = new RimGovernor.Brain.GameFacts.Observations.BleedOut.NotBleedingOut(),
        LifeThreatening = false,
        Hediffs = [],
        ShouldSeekMedicalRest = false,
        BedId = null,
        Snapshot = new RimGovernor.Brain.GameFacts.Observations.SnapshotRef(),
        Surgery = new RimGovernor.Brain.GameFacts.Observations.PawnSurgery { SurgeryBills = [], MissingParts = [], Operations = [] },
    };

    /// <summary>A body at 21 C inside a 16-26 C comfort range, walking at 4.6 cells a second, with no hypothermia.</summary>
    public static readonly RimGovernor.Brain.GameFacts.Observations.PawnBodyStats Body = new() { ComfortableMinC = 16, ComfortableMaxC = 26, MoveSpeed = 4.6, CellTemperatureC = 21, Hypothermia = 0 };

    /// <summary>
    /// A planner pawn: an available adult colonist in manual-priorities mode with no work rows, skills, traits, job,
    /// timetable, needs or Biotech, at full health; a test changes it with <c>with</c>.
    /// </summary>
    public static WorkPawn Worker(string id) => Worker(new PawnId(id));

    /// <inheritdoc cref="Worker(string)"/>
    public static WorkPawn Worker(PawnId id) => new()
    {
        Observed = Row(id.Value),
        Work = WorkTable(true, []),
        Ranged = false,
        Hunts = false,
        Traits = [],
        Biotech = new PawnBiotechStatus.Inactive(),
        SeriousBloodLoss = 0.4,
    };

    /// <summary>A native work table and the catalog views over exactly its observed rows.</summary>
    public static PawnWorkTable.Works WorkTable(bool manual, EquatableArray<WorkPriority> rows) =>
        new(new RimGovernor.Brain.GameFacts.Observations.PawnWork.Works(manual, EquatableArray.From(rows.Select(row => row.Observed))), rows);

    /// <summary>A live, standing colonist with nothing else read into it: no job, needs, conditions, gear or ideoligion.</summary>
    public static RimGovernor.Brain.GameFacts.Observations.PawnState Row(string id) => new()
    {
        Pawn = new RimGovernor.Brain.GameFacts.Observations.EntityRef { Id = id, DefName = "Human", Label = id, Place = new RimGovernor.Brain.GameFacts.Observations.EntityPlace.Unplaced() },
        KindDefName = "Colonist",
        Colonist = true,
        FreeColonist = true,
        Prisoner = false,
        Humanlike = true,
        Animal = false,
        Mechanoid = false,
        Dead = false,
        Downed = false,
        Drafted = false,
        Hostile = false,
        MentalState = null,
        InBed = false,
        ActivityExecution = null,
        Job = new RimGovernor.Brain.GameFacts.Observations.JobEvidence(),
        Needs = PawnNeedsTests.NoNeeds,
        Health = Healthy,
        Equipment = GearFixtures.NoEquipment,
        Biography = new RimGovernor.Brain.GameFacts.Observations.PawnBiography
        {
            BiologicalAgeYears = 30,
            Skills = [],
            Traits = [],
            IncapableWorkTypes = [],
            DisabledWorkTags = [],
            SkillStatCurves = [],
            WorkSpeeds = [],
            SkillStatFindings = [],
        },
        Settings = new RimGovernor.Brain.GameFacts.Observations.PawnSettings
        {
            Care = new RimGovernor.Brain.GameFacts.Observations.PawnCare.None(),
            AllowedAreaId = null,
            Schedule = [],
            FoodPolicyRespected = false,
            PolicyInputs = Inputs(),
            Body = Body,
            Work = new RimGovernor.Brain.GameFacts.Observations.PawnWork.Works(true, []),
        },
        Social = new RimGovernor.Brain.GameFacts.Observations.PawnSocial { HighExpectations = false, ExpectationDefName = "", Memories = [], Situational = [], SituationalCacheStale = false },
        AnimalState = new RimGovernor.Brain.GameFacts.Observations.PawnAnimalState.NotAnimal(),
        NearestColonist = new RimGovernor.Brain.GameFacts.Observations.NearestColonist.None(),
        OwnedBed = null,
        LordJobClass = null,
        LordToilClass = null,
        RaidArmor = new RimGovernor.Brain.GameFacts.Observations.RaidArmor.NoHostiles(),
        Fogged = false,
        ConstructionCapable = true,
        Inspiration = null,
        PawnSnapshot = null,
        Biotech = new RimGovernor.Brain.GameFacts.Observations.PawnBiotechState.Inactive(),
        Anomaly = new RimGovernor.Brain.GameFacts.Observations.PawnAnomalyState.Inactive(),
        Royalty = new RimGovernor.Brain.GameFacts.Observations.PawnRoyaltyState.Inactive(),
        FactionDefName = null,
    };

    /// <summary>The policy inputs of a pawn: an ideoligion member when <paramref name="ideo"/> names one, a guest when <paramref name="guest"/> is set.</summary>
    public static RimGovernor.Brain.GameFacts.Observations.PawnPolicyInputs Inputs(
        string? ideo = null,
        double certainty = 0,
        IEnumerable<string>? precepts = null,
        string? role = null,
        RimGovernor.Brain.GameFacts.Defs.GuestStatus? guest = null,
        string? readingPolicyId = null,
        IEnumerable<RimGovernor.Brain.GameFacts.Observations.InventoryStockSetting>? stock = null) => new()
    {
        ReadingPolicyId = readingPolicyId,
        InventoryStock = [.. stock ?? []],
        Ideo = ideo is null ? new RimGovernor.Brain.GameFacts.Observations.PawnIdeo.None() : new RimGovernor.Brain.GameFacts.Observations.PawnIdeo.Member(ideo, [.. precepts ?? []], role, certainty),
        Guest = guest is { } status ? new RimGovernor.Brain.GameFacts.Observations.GuestState.Hosted(status) : new RimGovernor.Brain.GameFacts.Observations.GuestState.Free(),
    };
}
