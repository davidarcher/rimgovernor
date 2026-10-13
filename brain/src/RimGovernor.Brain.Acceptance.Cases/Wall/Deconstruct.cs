using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>wall/deconstruct</c> (Go <c>deconstruct.go</c>).</summary>
internal static class WallDeconstruct
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "wall/deconstruct",
        Scope = "DECONSTRUCT Designate designates colony targets, adopts player designations and applies again on owned work; revoking authority releases only owned work; a native pawn demolishes an adopted target.",
        Start = new FixtureStart { Op = "test/deconstruct_prepare", On = new LabStart() },
        Budget = TimeSpan.FromMinutes(3),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var ids = s.Prepared?["ids"].AsSlice() ?? [];
        if (ids.Count != 6)
        {
            throw new AcceptanceException($"expected six fixture targets: {s.Prepared.ToJson()}");
        }

        Task<JsonObject> Mutate(string label, int i, string action) =>
            h.CallAsync(label, "test/deconstruct_target", new JsonObject { ["target"] = ids[i]?.DeepClone(), ["action"] = action }, cancellationToken);

        var grant = await Authority.GrantAutoAsync(h.WireFunc(), "designate-authority", s.Identity, cancellationToken).ConfigureAwait(false);
        foreach (var i in new[] { 0, 2, 3, 4 })
        {
            _ = await WallCases.ApplyDeconstructAsync(s, $"designate-{i}", ids[i], cancellationToken).ConfigureAwait(false);
        }

        _ = await Mutate("player-replacement", 2, "replace").ConfigureAwait(false);
        _ = await Mutate("external-disappearance", 4, "vanish").ConfigureAwait(false);

        // Owned work applies again under a new key.
        _ = await WallCases.ApplyDeconstructAsync(s, "designate-3-again", ids[3], cancellationToken).ConfigureAwait(false);
        _ = await Authority.RevokeManualAsync(h.WireFunc(), "release", s.Identity, grant, cancellationToken).ConfigureAwait(false);
        foreach (var i in new[] { 0, 1, 2, 3 })
        {
            var row = await Mutate($"after-release-{i}", i, "inspect").ConfigureAwait(false);
            var designated = row["designated"].AsBool() == true;
            var present = row["present"].AsBool() == true;
            if (!present || designated != (i == 1 || i == 2))
            {
                throw new AcceptanceException($"release altered player work or missed own designation: {row.ToJson()}");
            }
        }

        _ = await Authority.GrantAutoAsync(h.WireFunc(), "adopt-authority", s.Identity, cancellationToken).ConfigureAwait(false);
        _ = await Mutate("colony-target", 5, "colony").ConfigureAwait(false);
        _ = await Mutate("player-designation-to-adopt", 5, "replace").ConfigureAwait(false);
        var first = await WallCases.ApplyDeconstructAsync(s, "adopt-and-finish", ids[5], cancellationToken).ConfigureAwait(false);
        var again = await WallCases.ApplyDeconstructAsync(s, "adopt-and-finish", ids[5], cancellationToken).ConfigureAwait(false);
        if (!Dyn.DeepEqual(first, again))
        {
            throw new AcceptanceException($"resent key changed its result: {first.ToJson()} then {again.ToJson()}");
        }

        for (var advanced = 0; advanced < 6000; advanced += 300)
        {
            _ = await s.AdvanceAsync(300, null, cancellationToken).ConfigureAwait(false);
            var row = await Mutate($"job-{advanced}", 5, "inspect").ConfigureAwait(false);
            if (row["present"].AsBool() != true)
            {
                s.Report["deconstruction_result"] = first;
                s.Report["demolished_after_ticks"] = advanced + 300;
                return;
            }
        }

        throw new AcceptanceException("native deconstruction did not finish within 6000 ticks");
    }
}
