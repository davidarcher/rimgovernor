using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The shared native pawn rows the tests stand pawns with: a whole PawnState (every required section, each in its
/// "nothing here" state) that a test then changes with <c>with</c>, and the policy inputs a pawn row carries.
/// </summary>
internal static class PawnFixtures
{
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
        Health = new O.PawnHealth { SummaryFraction = 1 },
        Equipment = new O.PawnEquipment(),
        Biography = new O.PawnBiography
        {
            BiologicalAgeYears = 30,
            Skills = [],
            Traits = [],
            IncapableWorkTypes = [],
            DisabledWorkTags = [],
            SkillStatCurves = [],
            SkillStatFindings = [],
        },
        Settings = new O.PawnSettings
        {
            Care = new O.PawnCare.None(),
            AllowedAreaId = null,
            Schedule = [],
            FoodPolicyRespected = false,
            PolicyInputs = Inputs(),
            Body = new O.PawnBodyStats(),
        },
        Social = new O.PawnSocial { HighExpectations = false, ExpectationDefName = "", Memories = [], Situational = [], SituationalCacheStale = false },
        AnimalState = new O.PawnAnimalState.NotAnimal(),
        NearestColonist = new O.NearestColonist.None(),
        OwnedBed = null,
        LordJobClass = null,
        LordToilClass = null,
        RaidArmor = new O.RaidArmor.NoHostiles(),
        Fogged = false,
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
