using RimGovernor.Brain.Policy;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>Complete population and pawn observations for policy tests.</summary>
internal static class PopulationFixtures
{
    internal static O.PopulationPerson NativePerson(string pawn, bool recruitable = false, string? ideo = null) => new()
    {
        Pawn = new(pawn), Admitted = false, Guest = false, Recruitable = recruitable,
        IdeoId = ideo, WildMan = false,
        Custody = new O.PersonCustody.None(), WillJoinIfRescued = false,
    };

    internal static PrisonerProspect Prospect(string pawn = "p", EquatableArray<O.Skill> skills = default)
    {
        var observed = PawnFixtures.Row(pawn);
        return new PrisonerProspect { Observed = observed with { Biography = observed.Biography with { BiologicalAgeYears = 30, Skills = skills } } };
    }

    internal static PrisonerFacts Held(string pawn, bool recruitable = true, string? ideo = null)
    {
        var held = new O.PersonCustody.Prisoner(0, "MaintainOnly", 0, null, 0);
        var prospect = Prospect(pawn);
        var observed = prospect.Observed with { Prisoner = true, Colonist = false, FreeColonist = false, Settings = prospect.Observed.Settings with { Care = new O.PawnCare.Configured(RimGovernor.Contracts.Wire.Operations.MedicalCare.HerbalOrWorse, false, RimGovernor.Contracts.Wire.Operations.HostilityResponse.Flee) } };
        return new PrisonerFacts
        {
            Person = NativePerson(pawn, recruitable, ideo) with { Custody = held, Guest = true },
            Observed = observed,
            Held = held,
            CurrentInteraction = new PrisonerHeldInteraction.Settable(PrisonerInteractionMode.Maintain),
            Prospect = prospect with { Observed = observed },
        };
    }

    internal static CustodyFacts Person(string pawn, bool downed = false, bool guest = false, bool hostile = false, bool admitted = false) => new()
    {
        Person = NativePerson(pawn) with { Guest = guest, Admitted = admitted },
        Observed = PawnFixtures.Row(pawn) with { Downed = downed, Hostile = hostile },
        Prospect = Prospect(pawn),
    };

    internal static CustodyFacts Admitted(string pawn) => Person(pawn, admitted: true);

    internal static PrisonerCensus Census(IEnumerable<PrisonerFacts>? prisoners = null, IEnumerable<CustodyFacts>? custody = null, PrisonerColony? colony = null) => new()
    {
        Prisoners = [.. prisoners ?? []], Custody = [.. custody ?? []],
        Colony = colony ?? new PrisonerColony(), Guests = [], Names = [],
    };
}
