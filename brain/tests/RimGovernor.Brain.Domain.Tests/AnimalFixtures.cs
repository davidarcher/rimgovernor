using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>Complete native animal observations used by policy fixtures.</summary>
internal static class AnimalFixtures
{
    public static readonly RimGovernor.Brain.GameFacts.Observations.AnimalState Adult = new()
    {
        Gender = RimGovernor.Brain.GameFacts.Defs.Gender.Female,
        AgeYears = 3.5,
        Pregnancy = new RimGovernor.Brain.GameFacts.Observations.AnimalPregnancy.NotPregnant(),
        PenId = null,
        Training = [],
        Release = false,
        Slaughter = false,
        Tameable = false,
        Tame = false,
        SafeToRelease = true,
        MasterId = "",
        FollowDrafted = false,
        FollowFieldwork = false,
        Obedient = false,
        SupportsAllowedAreas = false,
        Sick = false,
        Venerated = false,
        Adult = true,
        Bonded = false,
        Sterilized = false,
        SterilizeQueued = false,
        Downed = false,
        InMentalState = false,
        ColonistBonded = false,
        SlaughterDesignatable = true,
        LifeStageIndex = 2,
        NextLifeStage = new RimGovernor.Brain.GameFacts.Observations.AnimalNextLifeStage.LastStage(),
        BondedPawnIds = [],
    };

    public static RimGovernor.Brain.GameFacts.Observations.PawnState Row(string id, string race, RimGovernor.Brain.GameFacts.Observations.AnimalState? state = null)
    {
        var pawn = PawnFixtures.Row(id);
        return pawn with
        {
            Pawn = pawn.Pawn with { DefName = race },
            KindDefName = race,
            Colonist = false,
            FreeColonist = false,
            Humanlike = false,
            Animal = true,
            AnimalState = new RimGovernor.Brain.GameFacts.Observations.PawnAnimalState.Animal(state ?? Adult),
        };
    }

    public static UpkeepAnimal Owned(string id, string race, RimGovernor.Brain.GameFacts.Observations.AnimalState? state = null) => new()
    {
        Pawn = Row(id, race, state),
        Colony = new() { Pawn = new(id), Roping = new RimGovernor.Brain.GameFacts.Observations.AnimalRoping.Unroped() },
    };
}
