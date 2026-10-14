namespace RimGovernor.Brain.Acceptance.Cases;

internal static class PawnDeliveryCase
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "control/pawn-activity-delivery",
        Scope = "Prepared harvest produces edible food; exact delivery refuses a newly blocked destination and a new explicit revision delivers only the selected quantity. Unit tests cannot prove native carrying, splitting, or destination behavior. Fixture revisions are not autonomous forecast replanning.",
        Start = new FixtureStart { Op = "test/pawn_delivery_prepare", On = new LabStart() },
        RequiredOps = ["test/pawn_delivery_prepare", "test/pawn_delivery_harvest", "test/pawn_delivery_select", "test/pawn_delivery_read"],
        QuietWorld = true, Crew = new Crew { Size = 3 }, Budget = TimeSpan.FromMinutes(3),
        Run = async (s, ct) =>
        {
            _ = await s.RuntimeAsync(ct).ConfigureAwait(false);
            _ = await s.Harness.CallAsync("harvest", "test/pawn_delivery_harvest", [], ct).ConfigureAwait(false);
            await WaitAsync(s, "harvest", "completed", ct).ConfigureAwait(false);
            _ = await s.Harness.CallAsync("blocked-delivery", "test/pawn_delivery_select", [], ct).ConfigureAwait(false);
            var blocked = await WaitAsync(s, "blocked", "failed", ct).ConfigureAwait(false);
            if (blocked["execution"]?["Reason"].AsString() != "delivery_unavailable"
                || blocked["execution"]?["Revision"].AsNumber() != 2
                || blocked["destination"].AsNumber() != 0 || blocked["revisedDestination"].AsNumber() != 0
                || blocked["source"].AsNumber() != blocked["harvestedCount"].AsNumber())
                throw new AcceptanceException("Blocked delivery moved food or silently chose another destination: " + blocked.ToJson());
            _ = await s.Harness.CallAsync("revised-delivery", "test/pawn_delivery_select", new() { ["revised"] = true }, ct).ConfigureAwait(false);
            var delivered = await WaitAsync(s, "delivered", "completed", ct).ConfigureAwait(false);
            if (delivered["execution"]?["Revision"].AsNumber() != 3
                || delivered["destination"].AsNumber() != 0 || delivered["revisedDestination"].AsNumber() != 1
                || delivered["source"].AsNumber() != delivered["harvestedCount"].AsNumber() - 1)
                throw new AcceptanceException("No exact physical delivery with conserved quantity: " + delivered.ToJson());
            s.Report["blocked"] = blocked;
            s.Report["delivered"] = delivered;
        },
    };

    private static async Task<System.Text.Json.Nodes.JsonObject> WaitAsync(ISession s, string stage, string expected, CancellationToken ct)
    {
        for (var ticks = 0; ticks < 1800; ticks += 30)
        {
            var paused = false;
            try { _ = await s.AdvanceAsync(30, null, ct).ConfigureAwait(false); }
            catch (ScenarioInterruptedException e) when (e.Reason == "Unexpected native interruption: clock stopped on requested_pause")
            {
                // Activity completion/refusal deliberately stops the window for a new decision.
                // Accept it only after the exact assignment's expected terminal state is read below.
                paused = true;
            }
            var read = await s.Harness.CallAsync(stage + ticks, "test/pawn_delivery_read", [], ct).ConfigureAwait(false);
            if (read["forced"].AsBool() == true) throw new AcceptanceException("Delivery became player-forced.");
            var state = read["execution"]?["State"].AsString();
            if (state == expected) return read;
            if (state == "failed" || paused) throw new AcceptanceException(stage + ": " + read.ToJson());
        }
        throw new AcceptanceException(stage + " did not reach " + expected + " within its tick budget.");
    }
}
