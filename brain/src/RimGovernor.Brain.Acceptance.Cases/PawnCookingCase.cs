namespace RimGovernor.Brain.Acceptance.Cases;

internal static class PawnCookingCase
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "control/pawn-activity-cooking",
        Scope = "Exact cooking consumes only authorized quantities from selected stacks and completes one selected recipe iteration despite competing bill/ingredient options. Missing ingredients and a blocked bench fail visibly. Unit tests cannot prove native consumption or food creation; fixture revisions are not autonomous forecast replanning.",
        Start = new FixtureStart { Op = "test/pawn_cooking_prepare", On = new LabStart() },
        RequiredOps = ["test/pawn_cooking_prepare", "test/pawn_cooking_select", "test/pawn_cooking_read"],
        QuietWorld = true, Crew = new Crew { Size = 3 }, Budget = TimeSpan.FromMinutes(4),
        Run = async (s, ct) =>
        {
            _ = await s.RuntimeAsync(ct).ConfigureAwait(false);
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                _ = await s.Harness.CallAsync("select-" + attempt, "test/pawn_cooking_select", new() { ["attempt"] = attempt }, ct).ConfigureAwait(false);
                var read = await WaitAsync(s, attempt, ct).ConfigureAwait(false);
                if (read["execution"]?["Revision"].AsNumber() != attempt || read["unselected"].AsNumber() != 60 || read["otherRemaining"].AsNumber() != 2)
                    throw new AcceptanceException("Cooking substituted authorization: " + read.ToJson());
                if (attempt < 3)
                {
                    var reason = read["execution"]?["Reason"].AsString();
                    var expectedFailure = attempt == 1 ? reason == "cooking_ingredient_unavailable"
                        : read["benchUsable"].AsBool() == false && reason is "target_unreachable_or_reserved" or "cooking_bench_unavailable" or "Incompletable";
                    if (!expectedFailure || read["meals"].AsNumber() != 0
                        || read["raw"].AsNumber() != read["beforeRaw"].AsNumber() || read["selectedRemaining"].AsNumber() != 2)
                        throw new AcceptanceException("Cooking did not preserve inputs and report the expected refusal: " + read.ToJson());
                }
                else if (read["meals"].AsNumber() != read["expectedMeals"].AsNumber()
                    || read["raw"].AsNumber() != read["beforeRaw"].AsNumber() - read["required"].AsNumber()
                    || read["selectedRemaining"].AsNumber() != 1)
                    throw new AcceptanceException("Missing exact physical recipe conversion: " + read.ToJson());
                s.Report["attempt-" + attempt] = read;
            }
        },
    };

    private static async Task<System.Text.Json.Nodes.JsonObject> WaitAsync(ISession s, int attempt, CancellationToken ct)
    {
        for (var ticks = 0; ticks < 2400; ticks += 30)
        {
            var paused = false;
            try { _ = await s.AdvanceAsync(30, null, ct).ConfigureAwait(false); }
            catch (ScenarioInterruptedException e) when (e.Reason == "Unexpected native interruption: clock stopped on requested_pause") { paused = true; }
            var read = await s.Harness.CallAsync($"cook-{attempt}-{ticks}", "test/pawn_cooking_read", [], ct).ConfigureAwait(false);
            if (read["forced"].AsBool() == true) throw new AcceptanceException("Cooking became player-forced.");
            var state = read["execution"]?["State"].AsString();
            if (state == (attempt < 3 ? "failed" : "completed")) return read;
            if (paused || state == "failed") throw new AcceptanceException(read.ToJson());
        }
        throw new AcceptanceException("No terminal cooking evidence within the bounded fixture.");
    }
}
