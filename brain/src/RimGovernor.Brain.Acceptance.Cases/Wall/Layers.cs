using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>wall/layers</c> (Go <c>layers.go</c>).</summary>
internal static class WallLayers
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "wall/layers",
        Scope = "All three layers of a funded wall ring start as standable frames and finish through native pawn work without stranded frames or trapped colonists (#2314). A completion that seals an unfinished neighbour is refused; a separate pawn-on-frame completion records vanilla relocation and requires escape. A Go snapshot cannot prove frame standability, Harmony completion vetoes or vanilla pawn displacement.",
        Start = new LabStart(),
        RequiredOps = ["test/wall_layers"],
        Budget = TimeSpan.FromMinutes(4),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        _ = await Authority.GrantAutoAsync(h.WireFunc(), "wall-layer-authority", s.Identity, cancellationToken).ConfigureAwait(false);

        async Task<JsonObject> Call(string label, string action)
        {
            var row = await h.CallAsync(label, "test/wall_layers", new JsonObject { ["action"] = action }, cancellationToken).ConfigureAwait(false);
            return row["success"].AsBool() == true ? row : throw new AcceptanceException($"wall_layers {action} refused: {row.ToJson()}");
        }

        var before = await Call("fund-all-layers", "prepare").ConfigureAwait(false);
        var expected = before["expected"].AsNumber();
        if (expected != 69 || before["frames"].AsNumber() != expected || before["funded"].AsNumber() != expected || before["standable"].AsNumber() != expected || before["standing"].AsNumber() != 0)
        {
            throw new AcceptanceException($"all layers must start funded, standable and unfinished: {before.ToJson()}");
        }

        s.Report["precondition"] = before;
        for (var ticks = 0; ticks <= 24000; ticks += 300)
        {
            var row = await Call("layers-" + CaseSupport.Tag(ticks), "audit").ConfigureAwait(false);
            if (row["trapped"].AsNumber() != 0)
            {
                throw new AcceptanceException($"wall ring trapped a colonist: {row.ToJson()}");
            }

            if (row["standing"].AsNumber() == expected && row["frames"].AsNumber() == 0)
            {
                s.Report["completed"] = row;
                s.Report["ticks"] = ticks;
                var controls = await Call("completion-controls", "controls").ConfigureAwait(false);
                s.Report["completion_controls"] = controls;
                foreach (var field in new[] { "refused", "skipped", "completed", "escaped" })
                {
                    if (controls[field].AsBool() != true)
                    {
                        throw new AcceptanceException($"completion control {field} failed: {controls.ToJson()}");
                    }
                }

                return;
            }

            if (ticks == 24000)
            {
                break;
            }

            _ = await s.AdvanceAsync(300, null, cancellationToken).ConfigureAwait(false);
        }

        throw new AcceptanceException("three-thick ring did not finish within 24000 ticks");
    }
}
