using System.Collections.Immutable;

namespace RimGovernor.Brain.Domain;

/// <summary>
/// Population-*'s direct-write prisoner custody order: one exclusive native interaction. Recruit and
/// Maintain are the routine pair; ReduceResistance and Release are Core modes and Enslave and Convert exist
/// only while Ideology is active (native refuses them otherwise). Execution and the non-exclusive toggles
/// stay unsupported at this boundary.
/// </summary>
public enum PrisonerInteractionMode
{
    Recruit,
    Maintain,
    ReduceResistance,
    Release,
    Enslave,
    Convert,
}

public static class PrisonerInteractionModeExtensions
{
    /// <summary>Every supported mode in wire order.</summary>
    public static readonly ImmutableArray<PrisonerInteractionMode> PrisonerInteractionModes =
    [
        PrisonerInteractionMode.Recruit,
        PrisonerInteractionMode.Maintain,
        PrisonerInteractionMode.ReduceResistance,
        PrisonerInteractionMode.Release,
        PrisonerInteractionMode.Enslave,
        PrisonerInteractionMode.Convert,
    ];
}

/// <summary>
/// Explicit intent to write one already-observed prisoner's exclusive interaction mode. Native eligibility
/// (recruitable, alive, not a wild man barred from the mode) is established at inspection, not here.
/// </summary>
public sealed record PrisonerInteraction : IActionPayload
{
    public PrisonerInteraction(PawnId pawn, PrisonerInteractionMode interaction)
    {
        DomainException.Require(pawn.IsValid, "prisoner interaction requires a valid pawn identity");
        DomainException.Require(Wire.IsDefined(interaction), "invalid prisoner interaction mode");
        Pawn = pawn;
        Interaction = interaction;
    }

    public PawnId Pawn { get; }

    public PrisonerInteractionMode Interaction { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewPrisonerInteractionAction(ActionId id, PrisonerInteraction interaction) => Create(id, ActionKind.PrisonerInteraction, interaction);

    public PrisonerInteraction? PrisonerInteraction => PayloadOf<PrisonerInteraction>(ActionKind.PrisonerInteraction);
}
