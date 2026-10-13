namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>pawn/self-tend</c> (Go <c>self_tend.go</c>).</summary>
internal static class PawnSelfTend
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "pawn/self-tend",
        Scope = "A lone doctor-capable colonist with self-tend off has no other doctor, so EnsureWorkAssignments turns self-tend on through a PawnSettingsIntent (#1305), and the native care read shows it on. Native contract: the intent's self_tend write, the care read's self_tend and medical_tend_quality; the comparison itself is policy/self_tend_test.go.",
        Start = new FixtureStart { Op = "test/lone_self_tend_off", On = new LabStart() },
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
                var row = await PawnSupport.ReadPawnAsync(h, s, "self-tend-wait", pawnId, true, ct).ConfigureAwait(false);
                return row["settings"].Member("selfTend").AsBool() == true ? ("self-tend applied", true) : ("waiting for the self-tend setting", false);
            },
            cancellationToken).ConfigureAwait(false);
        var final = await PawnSupport.ReadPawnAsync(s.Harness, s, "self-tend", pawnId, true, cancellationToken).ConfigureAwait(false);
        var settings = final["settings"].AsMap() ?? [];
        var quality = settings["policyInputs"].Member("medicalTendQuality");
        s.Report["self_tend"] = settings["selfTend"].AsBool() == true;
        s.Report["tend_quality"] = quality?.DeepClone();
        if (settings["selfTend"].AsBool() != true)
        {
            throw new AcceptanceException("self-tend readback off, want on");
        }

        if (quality is null)
        {
            throw new AcceptanceException("doctor-capable pawn read carried no medical tend quality");
        }
    }
}
