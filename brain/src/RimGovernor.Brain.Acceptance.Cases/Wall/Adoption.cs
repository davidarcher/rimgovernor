using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>wall/adoption</c> (Go <c>adoption.go</c>).</summary>
internal static class WallAdoption
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "wall/adoption",
        Scope = "Smoke: a DECONSTRUCT Designate adopts a player designation with applied evidence; revoking authority removes adopted work and preserves unadopted player work.",
        Start = new FixtureStart { Op = "test/deconstruct_prepare", On = new LabStart() },
        Budget = TimeSpan.FromMinutes(1),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var ids = s.Prepared?["ids"].AsSlice() ?? [];
        if (ids.Count != 6)
        {
            throw new AcceptanceException("missing fixture targets");
        }

        // Target 1 already has a foreign order; target 0 is a colony building.
        _ = await h.CallAsync("player-colony-order", "test/deconstruct_target", new JsonObject { ["target"] = ids[0]?.DeepClone(), ["action"] = "replace" }, cancellationToken).ConfigureAwait(false);
        var grant = await Authority.GrantAutoAsync(h.WireFunc(), "adoption-authority", s.Identity, cancellationToken).ConfigureAwait(false);
        var result = await WallCases.ApplyDeconstructAsync(s, "adopt", ids[0], cancellationToken).ConfigureAwait(false);
        var effect = CaseSupport.AppliedEffect(result, "deconstruct");
        if (effect is null || !Dyn.DeepEqual(effect["targetId"], ids[0]) || effect["designationId"].AsString().Length == 0)
        {
            throw new AcceptanceException($"adoption lacks exact designation evidence: {result.ToJson()}");
        }

        s.Report["adoption_result"] = result;
        _ = await Authority.RevokeManualAsync(h.WireFunc(), "release", s.Identity, grant, cancellationToken).ConfigureAwait(false);
        foreach (var i in new[] { 0, 1 })
        {
            var row = await h.CallAsync($"inspect-{i}", "test/deconstruct_target", new JsonObject { ["target"] = ids[i]?.DeepClone(), ["action"] = "inspect" }, cancellationToken).ConfigureAwait(false);
            var designated = row["designated"].AsBool() == true;
            var present = row["present"].AsBool() == true;
            if (!present || designated != (i == 1))
            {
                throw new AcceptanceException($"release did not distinguish adopted and foreign orders: {row.ToJson()}");
            }
        }
    }
}
