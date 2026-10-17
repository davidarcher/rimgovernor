using Fx = RimGovernor.Fixtures.Contracts;
using Operations = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>draft/intent</c>: the draft intent through operations_apply (#939). A draft the player set outside the controller
/// is undrafted by the Auto undraft intent (Auto has full control), a draft issues, a same-state draft applies with
/// issued=false, and an unknown pawn is refused; list_pawns reads each effect back.
/// </summary>
internal static class DraftIntent
{
    // test/b04f_setup's external-draft is the native player-order equivalent: Pawn_DraftController.Drafted set directly.
    private const string PlayerDraftTool = "test/b04f_setup";

    private static readonly FixtureOp<string, Fx.FixtureDone> PlayerDraft = new(
        PlayerDraftTool,
        pawn => ServedOps.Args(("op", "external-draft"), ("pawn", pawn), ("drafted", true)),
        ServedOps.Done(PlayerDraftTool));

    internal static TypedCase Case() => new()
    {
        Name = "draft/intent",
        Scope = "The draft intent through operations_apply (#939): a player's draft set outside the controller is undrafted by the "
            + "Auto undraft intent, a draft issues, a same-state draft applies with issued=false, and an unknown pawn is refused; "
            + "list_pawns reads back each effect.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Ops = [PlayerDraftTool],
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var pawn = await TypedPawns.FirstColonistAsync(s, "initial", null, cancellationToken).ConfigureAwait(false);
        var id = pawn.Pawn.Id;
        if (pawn.Drafted)
        {
            throw new AcceptanceException($"{id} starts drafted");
        }

        _ = await s.FixtureAsync("player-draft", PlayerDraft, id, cancellationToken).ConfigureAwait(false);
        await ExpectDraftedAsync(s, "player-draft-readback", id, true, cancellationToken).ConfigureAwait(false);
        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);
        await ExpectDraftAsync(s, "auto-undraft", id, drafted: false, issued: true, cancellationToken).ConfigureAwait(false);
        await ExpectDraftAsync(s, "draft", id, drafted: true, issued: true, cancellationToken).ConfigureAwait(false);
        await ExpectDraftAsync(s, "draft-again", id, drafted: true, issued: false, cancellationToken).ConfigureAwait(false);
        var unknown = await TypedPawns.ApplyAsync(s, "draft-unknown", new Operations.DraftIntent { PawnId = "NoSuchPawn0", Drafted = true }, cancellationToken).ConfigureAwait(false);
        if (unknown.Outcome?.Refused is null && unknown.Outcome?.Failed is null)
        {
            throw new AcceptanceException($"draft-unknown: an unknown pawn was not refused: {unknown.Outcome}");
        }

        s.Record("pawn", id);
        s.RecordWire("unknown", unknown);
    }

    private static async Task ExpectDraftAsync(TypedSession s, string key, string id, bool drafted, bool issued, CancellationToken cancellationToken)
    {
        var effect = await TypedPawns.AppliedAsync(s, key, new Operations.DraftIntent { PawnId = id, Drafted = drafted }, cancellationToken).ConfigureAwait(false);
        if (effect?.Job?.Issued != issued)
        {
            throw new AcceptanceException($"{key}: issued={effect?.Job?.Issued}, want {issued}: {effect}");
        }

        await ExpectDraftedAsync(s, key + "-readback", id, drafted, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExpectDraftedAsync(TypedSession s, string label, string id, bool drafted, CancellationToken cancellationToken)
    {
        var row = await TypedPawns.OneAsync(s, label, id, null, cancellationToken).ConfigureAwait(false);
        if (row.Drafted != drafted)
        {
            throw new AcceptanceException($"{label}: {id} reads drafted={row.Drafted}, want {drafted}");
        }
    }
}
