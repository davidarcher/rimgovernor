namespace RimGovernor.Brain.Domain;

/// <summary>
/// Explicit intent to have one pawn open one filled ancient cryptosleep casket. Opening one casket ejects every
/// casket of its shrine group at once, so a plan carries a single OpenCasket after the drafts and moves that
/// stand a melee colonist in front of each filled casket. The pawn may be drafted: the vanilla Open job is an
/// ordered job the draft does not refuse. Native eligibility (the casket still holds something, the pawn can
/// reach its interaction cell) is established at inspection; Cell is the casket's cell, carried like Repair's
/// so a fresh CAS token can be re-scoped from the building listing.
/// </summary>
public sealed record OpenCasket : IActionPayload
{
    /// <summary>Go <c>NewOpenCasket</c>; throws <see cref="DomainException"/> when invalid.</summary>
    public OpenCasket(PawnId pawn, string casket, Cell cell)
    {
        DomainException.Require(
            pawn.IsValid && Identity.IsValidText(casket) && cell.X >= 0 && cell.Z >= 0,
            "open casket requires a valid pawn, casket and nonnegative cell"
        );
        Pawn = pawn;
        Casket = casket;
        Cell = cell;
    }

    /// <summary>The native job def the order issues (Go <c>JobDef</c>).</summary>
    public const string JobDef = "Open";

    public PawnId Pawn { get; }

    public string Casket { get; }

    public Cell Cell { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewOpenCasketAction(ActionId id, OpenCasket open) => Create(id, ActionKind.OpenCasket, open);

    /// <summary>Go <c>Action.OpenCasket() (OpenCasket, bool)</c>: the payload when this is an open casket action, otherwise null.</summary>
    public OpenCasket? OpenCasket => PayloadOf<OpenCasket>(ActionKind.OpenCasket);
}
