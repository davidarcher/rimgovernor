namespace RimGovernor.Brain.Domain;

/// <summary>
/// Sets the game's home-area auto-expand (Find.PlaySettings.autoHomeArea, a save-level setting): an
/// AutoHomeAreaIntent on Actions/Apply. No pawn, map cell or Job is involved. <paramref name="Enabled"/> is the
/// auto-expand value the action sets.
/// </summary>
public sealed record AutoHomeAreaOrder(bool Enabled) : IActionPayload;

public sealed partial record DomainAction
{
    /// <summary>Go <c>NewAutoHomeAreaAction</c>: builds the action that leaves auto-expand enabled or disabled.</summary>
    public static DomainAction NewAutoHomeAreaAction(ActionId id, bool enabled) =>
        Create(id, ActionKind.AutoHomeArea, new AutoHomeAreaOrder(enabled));

    /// <summary>Go <c>Action.AutoHomeArea() (bool, bool)</c>: the auto-expand value when this is an auto home area action, otherwise null.</summary>
    public bool? AutoHomeArea => PayloadOf<AutoHomeAreaOrder>(ActionKind.AutoHomeArea)?.Enabled;
}
