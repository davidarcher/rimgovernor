namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>draft/intent</c> (Go <c>intent.go</c>): the draft intent through Actions/Apply (#939): draft issues, a same-state
/// draft applies with issued=false, an unknown pawn is refused, and undraft issues; census readbacks prove each effect.
/// </summary>
internal static class DraftIntent
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "draft/intent",
        Scope = "The draft intent through Actions/Apply (#939): draft issues, a same-state draft applies with issued=false, "
            + "an unknown pawn is refused, and undraft issues; census readbacks prove each effect.",
        Start = new LabStart(),
        Budget = TimeSpan.FromMinutes(2),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var identity = s.Identity;
        var before = await DraftCases.ReadPawnAsync(h, identity, "initial-pawn", "", cancellationToken).ConfigureAwait(false);
        if (before["drafted"].AsBool() == true)
        {
            throw new AcceptanceException("fixture pawn starts drafted");
        }

        var id = before["pawn"].Member("id").AsString();
        _ = await Authority.GrantAutoAsync(h.WireFunc(), "acquire", identity, cancellationToken).ConfigureAwait(false);
        await DraftCases.ExpectDraftAsync(h, identity, "draft", id, drafted: true, issued: true, cancellationToken).ConfigureAwait(false);
        await DraftCases.ExpectDraftAsync(h, identity, "draft-again", id, drafted: true, issued: false, cancellationToken).ConfigureAwait(false);
        var refused = false;
        try
        {
            _ = await ApplyIntents.ApplyDraftAsync(h, "draft-unknown", identity, "draft-unknown", "NoSuchPawn0", true, cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException)
        {
            refused = true;
        }

        if (!refused)
        {
            throw new AcceptanceException("draft-unknown: an unknown pawn was not refused");
        }

        await DraftCases.ExpectDraftAsync(h, identity, "undraft", id, drafted: false, issued: true, cancellationToken).ConfigureAwait(false);
        s.Report["pawn_id"] = id;
    }
}
