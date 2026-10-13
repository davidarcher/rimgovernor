using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>takeover/draft</c> (Go <c>cases/draft/takeover.go</c>): a Manual player draft is undrafted by the Auto undraft
/// intent; pawn readbacks prove both effects.
/// </summary>
internal static class DraftTakeover
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "takeover/draft",
        Scope = "A Manual player draft is undrafted by the Auto undraft intent; pawn readbacks prove both effects.",
        Start = new LabStart(),
        RequiredOps = ["test/b04f_setup"],
        Budget = TimeSpan.FromMinutes(2),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        await TakeoverAudit.ManualAsync(s, cancellationToken).ConfigureAwait(false);
        var h = s.Harness;
        var identity = s.Identity;
        var before = await DraftCases.ReadPawnAsync(h, identity, "manual-pawn", "", cancellationToken).ConfigureAwait(false);
        var id = before["pawn"].Member("id").AsString();
        var edit = await h.CallAsync(
            "manual-draft",
            "test/b04f_setup",
            new JsonObject { ["op"] = "external-draft", ["pawn"] = id, ["drafted"] = true },
            cancellationToken).ConfigureAwait(false);
        if (edit["success"].AsBool() != true)
        {
            throw new AcceptanceException($"player draft refused: {edit.ToJson()}");
        }

        var player = await DraftCases.ReadPawnAsync(h, identity, "manual-draft-readback", id, cancellationToken).ConfigureAwait(false);
        if (player["drafted"].AsBool() != true)
        {
            throw new AcceptanceException($"player draft is not standing: {player.ToJson()}");
        }

        _ = await Authority.GrantAutoAsync(h.WireFunc(), "takeover-auto", identity, cancellationToken).ConfigureAwait(false);

        // Auto has full control: no plan needs the player's draft, so the undraft intent releases it.
        await DraftCases.ExpectDraftAsync(h, identity, "auto-undraft", id, drafted: false, issued: true, cancellationToken).ConfigureAwait(false);
        s.Report["undrafted"] = id;
    }
}
