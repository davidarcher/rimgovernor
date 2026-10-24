using RimGovernor.Brain.Policy;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The shared native pawn rows the tests stand pawns with: a whole PawnState (every required section, each in its
/// "nothing here" state) that a test then changes with <c>with</c>, and the policy inputs a pawn row carries.
/// </summary>
internal static class PawnFixtures
{
    /// <summary>Full health: no hediffs, no bleeding, in no bed, no surgery bills, missing parts or operations.</summary>
    public static readonly O.PawnHealth Healthy = new()
    {
        SummaryFraction = 1,
        NeedsTend = false,
        Bleeding = false,
        BleedRatePerDay = 0,
        BloodLoss = 0,
        BleedOut = new O.BleedOut.NotBleedingOut(),
        LifeThreatening = false,
        Hediffs = [],
        ShouldSeekMedicalRest = false,
        BedId = null,
        Snapshot = new O.SnapshotRef(),
        Surgery = new O.PawnSurgery { SurgeryBills = [], MissingParts = [], Operations = [] },
    };

    /// <summary>A body at 21 C inside a 16-26 C comfort range, walking at 4.6 cells a second, with no hypothermia.</summary>
    public static readonly O.PawnBodyStats Body = new() { ComfortableMinC = 16, ComfortableMaxC = 26, MoveSpeed = 4.6, CellTemperatureC = 21, Hypothermia = 0 };

    /// <summary>
    /// A planner pawn: an available adult colonist in manual-priorities mode with no work rows, skills, traits, job,
    /// timetable, needs or Biotech, at full health; a test changes it with <c>with</c>.
    /// </summary>
    public static WorkPawn Worker(string id) => Worker(new PawnId(id));

    /// <inheritdoc cref="Worker(string)"/>
    public static WorkPawn Worker(PawnId id) => new()
    {
        Id = id,
        ConstructionAble = true,
        Available = true,
        Work = new PawnWorkTable.Works(true, []),
        Ranged = false,
        Hunts = false,
        Skills = [],
        SkillStatCurves = [],
        Traits = [],
        Incapable = [],
        Age = 30,
        Schedule = [],
        FoodPolicyRespected = false,
        Care = new O.PawnCare.None(),
        PolicyInputs = Inputs(),
        Body = Body,
        Job = new PawnJob(),
        Inspiration = "",
        Biotech = new PawnBiotechStatus.Inactive(),
        ViolenceCapable = true,
        BloodLoss = 0,
        SeriousBloodLoss = 0.4,
        Health = 1,
        Rest = new O.NeedLevel.Lacks(),
        Mood = new O.NeedLevel.Lacks(),
        Food = new O.FoodNeed.Lacks(),
        BreakThreshold = 0.35,
        HighExpectations = false,
        PsylinkLevel = 0,
    };

    /// <summary>A live, standing colonist with nothing else read into it: no job, needs, conditions, gear or ideoligion.</summary>
    public static O.PawnState Row(string id) => new()
    {
        Pawn = new O.EntityRef { Id = id, DefName = "Human", Label = id, Place = new O.EntityPlace.Unplaced() },
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
        Job = new O.JobEvidence(),
        Needs = PawnNeedsTests.NoNeeds,
        Health = Healthy,
        Equipment = GearFixtures.NoEquipment,
        Biography = new O.PawnBiography
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
        Settings = new O.PawnSettings
        {
            Care = new O.PawnCare.None(),
            AllowedAreaId = null,
            Schedule = [],
            FoodPolicyRespected = false,
            PolicyInputs = Inputs(),
            Body = Body,
            Work = new O.PawnWork.Works(true, []),
        },
        Social = new O.PawnSocial { HighExpectations = false, ExpectationDefName = "", Memories = [], Situational = [], SituationalCacheStale = false },
        AnimalState = new O.PawnAnimalState.NotAnimal(),
        NearestColonist = new O.NearestColonist.None(),
        OwnedBed = null,
        LordJobClass = null,
        LordToilClass = null,
        RaidArmor = new O.RaidArmor.NoHostiles(),
        Fogged = false,
        ConstructionCapable = true,
        Inspiration = null,
        PawnSnapshot = null,
        Biotech = new O.PawnBiotechState.Inactive(),
        Anomaly = new O.PawnAnomalyState.Inactive(),
        Royalty = new O.PawnRoyaltyState.Inactive(),
        FactionDefName = null,
    };

    /// <summary>The policy inputs of a pawn: an ideoligion member when <paramref name="ideo"/> names one, a guest when <paramref name="guest"/> is set.</summary>
    public static O.PawnPolicyInputs Inputs(
        string? ideo = null,
        double certainty = 0,
        IEnumerable<string>? precepts = null,
        string? role = null,
        RimGovernor.Contracts.Wire.Defs.GuestStatus? guest = null,
        string? readingPolicyId = null,
        IEnumerable<O.InventoryStockSetting>? stock = null) => new()
    {
        ReadingPolicyId = readingPolicyId,
        InventoryStock = [.. stock ?? []],
        Ideo = ideo is null ? new O.PawnIdeo.None() : new O.PawnIdeo.Member(ideo, [.. precepts ?? []], role, certainty),
        Guest = guest is { } status ? new O.GuestState.Hosted(status) : new O.GuestState.Free(),
    };
}
