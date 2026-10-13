using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Common;

namespace RimGovernor.Brain.Bridge.Contracts;

/// <summary>
/// Go's <c>PrisonerCensus</c>: one rounds cycle's whole prisoner census, read once per cycle so the prisoner
/// interaction planner can detect MaintainPopulation's deficit and select a candidate the same way the
/// always-present generic colony census lets the husbandry planner detect and select from animal state. Unlike that
/// generic census, prisoner facts live only on the dedicated population read. The read and its decoder
/// (<c>ReadRoundsPopulation</c>, <c>decodePopulation</c>, <c>prisonerProspect</c>) are wire plumbing and not
/// ported.
/// </summary>
public sealed record PrisonerCensus
{
    public ObservationContext? Context { get; init; }

    public Fact<EquatableArray<PrisonerFacts>> Prisoners { get; init; }

    /// <summary>The same read's capture/rescue candidate census: every observed humanlike, not only prisoners.</summary>
    public Fact<EquatableArray<CustodyFacts>> Custody { get; init; }

    /// <summary>The snapshot's storyteller population outlook.</summary>
    public PopulationOutlook Outlook { get; init; } = new();

    /// <summary>The colony side of each prisoner's use: the free colonists' best skills and the snapshot's Ideology facts.</summary>
    public Fact<PrisonerColony> Colony { get; init; }

    /// <summary>The colony guests' care cap inputs.</summary>
    public Fact<EquatableArray<CarePatient>> Guests { get; init; }

    /// <summary>The owned-pawn short-name census.</summary>
    public Fact<EquatableArray<OwnedName>> Names { get; init; }
}

public static partial class BridgeFunctions
{
    /// <summary>
    /// Go's <c>prisonerInteractionDefNames</c>: maps the open native defName a population person's interaction
    /// carries to the domain mode; any other current interaction (Execution, DLC modes the intent does not expose)
    /// stays unknown rather than misread.
    /// </summary>
    public static bool TryPrisonerInteractionMode(string defName, out PrisonerInteractionMode mode)
    {
        PrisonerInteractionMode? found = defName switch
        {
            "AttemptRecruit" => PrisonerInteractionMode.Recruit,
            "MaintainOnly" => PrisonerInteractionMode.Maintain,
            "ReduceResistance" => PrisonerInteractionMode.ReduceResistance,
            "Release" => PrisonerInteractionMode.Release,
            "Enslave" => PrisonerInteractionMode.Enslave,
            "Convert" => PrisonerInteractionMode.Convert,
            _ => null,
        };
        mode = found.GetValueOrDefault();
        return found.HasValue;
    }
}
