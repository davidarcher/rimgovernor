using RimGovernor.Brain.Policy;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>Complete native animal observations used by policy fixtures.</summary>
internal static class AnimalFixtures
{
    public static readonly O.AnimalState Adult = new()
    {
        Gender = RimGovernor.Contracts.Wire.Defs.Gender.Female,
        AgeYears = 3.5,
        Pregnancy = new O.AnimalPregnancy.NotPregnant(),
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
        NextLifeStage = new O.AnimalNextLifeStage.LastStage(),
        BondedPawnIds = [],
    };

    public static O.PawnState Row(string id, string race, O.AnimalState? state = null)
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
            AnimalState = new O.PawnAnimalState.Animal(state ?? Adult),
        };
    }

    public static UpkeepAnimal Owned(string id, string race, O.AnimalState? state = null) => new()
    {
        Pawn = Row(id, race, state),
        Colony = new() { Pawn = new(id), Roping = new O.AnimalRoping.Unroped() },
    };
}
