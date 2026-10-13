namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>pawn/inspiration</c> (Go <c>inspiration.go</c>).</summary>
internal static class PawnInspiration
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "pawn/inspiration",
        Scope = "A fixture pawn given Inspired_Creativity reads it back through the pawn read and the Go pawn profile (#1187).",
        Start = new FixtureStart { Op = "test/inspire_creativity", On = new LabStart() },
        Budget = TimeSpan.FromMinutes(1),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var pawnId = PawnSupport.Fixture(s, "pawn", "pawn");

        // The profile's inspiration is the pawn row's own inspiration defName (empty: none).
        var row = await PawnSupport.ReadPawnAsync(s.Harness, s, "inspiration", pawnId, false, cancellationToken).ConfigureAwait(false);
        var got = row["inspiration"].AsString();
        if (got != "Inspired_Creativity")
        {
            throw new AcceptanceException($"inspiration readback: known={row.ContainsKey("inspiration")} got=\"{got}\"");
        }

        s.Report["pawn_inspiration"] = got;
    }
}
