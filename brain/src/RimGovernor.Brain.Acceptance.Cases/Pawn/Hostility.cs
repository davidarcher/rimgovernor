namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>pawn/hostility</c> (Go <c>hostility.go</c>).</summary>
internal static class PawnHostility
{
    private const string Flee = "HOSTILITY_RESPONSE_FLEE";

    internal static AcceptanceCase Case() => new()
    {
        Name = "pawn/hostility",
        Scope = "A fixture pawn made violence-incapable and set to Ignore is set to Flee by EnsureWorkAssignments through a PawnSettingsIntent (#1299), and the native pawn read shows Flee. Native contract: the intent's write, the care read's hostility_response and the violence tag; the mapping itself is policy/hostility_test.go.",
        Start = new FixtureStart { Op = "test/pacifist_ignore", On = new LabStart() },
        Serve = PawnSupport.WorkSpec,
        Budget = TimeSpan.FromMinutes(4),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var pawnId = PawnSupport.Fixture(s, "pawn", "pawn");
        s.Report["pawn"] = pawnId;
        var h = s.Harness;
        await PawnSupport.SettleAsync(
            s,
            async ct =>
            {
                var row = await PawnSupport.ReadPawnAsync(h, s, "hostility-wait", pawnId, true, ct).ConfigureAwait(false);
                return row["settings"].Member("hostilityResponse").AsString() == Flee ? ("Flee applied", true) : ("waiting for the Flee setting", false);
            },
            cancellationToken).ConfigureAwait(false);
        var row = await PawnSupport.ReadPawnAsync(s.Harness, s, "hostility", pawnId, true, cancellationToken).ConfigureAwait(false);
        var got = row["settings"].Member("hostilityResponse").AsString();
        s.Report["hostility"] = got;
        if (got != Flee)
        {
            throw new AcceptanceException($"hostility readback {got}, want Flee");
        }
    }
}
