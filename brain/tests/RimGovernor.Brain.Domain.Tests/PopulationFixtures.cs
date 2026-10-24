using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>Population census rows for tests: every required fact set to an ordinary held prisoner or a living, unhosted person.</summary>
internal static class PopulationFixtures
{
    /// <summary>A living, recruitable prisoner maintained without resistance, held no time.</summary>
    internal static PrisonerFacts Held(string pawn, bool recruitable = true) => new()
    {
        Pawn = new(pawn),
        Dead = false,
        Recruitable = recruitable,
        CurrentInteraction = new PrisonerHeldInteraction.Settable(PrisonerInteractionMode.Maintain),
        Resistance = 0,
        HeldTicks = 0,
        HarvestGoodwill = 0,
        Prospect = new PrisonerProspect(),
    };

    /// <summary>A living person with every custody flag false.</summary>
    internal static CustodyFacts Person(string pawn) => new()
    {
        Pawn = new(pawn),
        WillJoinIfRescued = false,
        Dead = false,
        Downed = false,
        Guest = false,
        Admitted = false,
        Prisoner = false,
        Hostile = false,
        Recruitable = false,
        Prospect = new PrisonerProspect(),
        Luciferium = false,
        WearingApparel = false,
    };

    /// <summary>An admitted colonist.</summary>
    internal static CustodyFacts Admitted(string pawn) => Person(pawn) with { Admitted = true };

    /// <summary>The census of these rows.</summary>
    internal static PrisonerCensus Census(IEnumerable<PrisonerFacts>? prisoners = null, IEnumerable<CustodyFacts>? custody = null, PrisonerColony? colony = null) => new()
    {
        Prisoners = [.. prisoners ?? []],
        Custody = [.. custody ?? []],
        Colony = colony ?? new PrisonerColony(),
        Guests = [],
        Names = [],
    };
}
