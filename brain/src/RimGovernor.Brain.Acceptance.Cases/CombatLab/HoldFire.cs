using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>combatlab/holdfire</c> (Go <c>holdfire.go</c>).</summary>
internal static class CombatLabHoldFire
{
    // holdFireTicks bounds each phase: a drafted rifleman 25 cells from a raider fires well inside it.
    private const int PhaseTicks = 1200;

    internal static AcceptanceCase Case() => new()
    {
        Name = "combatlab/holdfire",
        Scope = "fire_mode hold stops a drafted pawn shooting (#861), vanilla behaviour the #850 contract only reads back as a flag: on lab-ranged two drafted riflemen, "
            + "one ordered stop and fire_mode hold, the other ordered to attack; the attacker fires and the held one fires no shot over at least 600 ticks, "
            + "then fire_mode at_will and the formerly held one fires within 1200 ticks.",
        Start = new LabStart { Colonists = 4 },
        RequiredOps = [DebugGame.LabStartTool, FixtureStage.Tool, CombatFixtures.LabTool],
        QuietWorld = true,
        Budget = AcceptanceCase.LabBudget,
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        await RunWindowsAsync(s, cancellationToken).ConfigureAwait(false);
    }

    private static async Task RunWindowsAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var identity = s.Identity;
        var report = s.Report;
        var staged = await CombatStage.StageAsync(h, "lab-ranged", cancellationToken).ConfigureAwait(false);
        var frames = await CombatFrames.OpenAsync(h, identity, cancellationToken).ConfigureAwait(false);
        var colonists = staged.Colonists();
        var hostiles = staged.Hostiles();
        if (colonists.Count < 2 || hostiles.Count < 2)
        {
            throw new AcceptanceException($"staged {CaseSupport.Tag(colonists.Count)} colonists and {CaseSupport.Tag(hostiles.Count)} hostiles");
        }

        _ = await Authority.GrantAutoAsync(h.WireFunc(), "combat-holdfire-acquire", identity, cancellationToken).ConfigureAwait(false);
        var held = colonists[0];
        var shooter = colonists[1];
        await LabOps.DraftAllAsync(h, identity, "draft", [held, shooter], cancellationToken).ConfigureAwait(false);
        var results = await LabOps.IssueAsync(
            h,
            identity,
            "combat-holdfire-1",
            new JsonArray(
                new JsonObject { ["pawn"] = LabOps.PawnTarget(held), ["stop"] = new JsonObject() },
                new JsonObject { ["pawn"] = LabOps.PawnTarget(held), ["fireMode"] = "COMBAT_FIRE_MODE_HOLD" },
                new JsonObject { ["pawn"] = LabOps.PawnTarget(shooter), ["attack"] = LabOps.PawnTarget(hostiles[1]) }),
            cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < results.Count; i++)
        {
            if (results[i]["applied"].AsBool() != true)
            {
                throw new AcceptanceException($"order {CaseSupport.Tag(i)} refused: {results[i].ToJson()}");
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var shots = new SortedDictionary<string, int>(StringComparer.Ordinal);
        try
        {
            await ShotsForAsync(h, frames, seen, shots, shooter, PhaseTicks / 2, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            report["heldPhase"] = ShotsJson(shots);
        }

        if (shots.GetValueOrDefault(held) != 0)
        {
            throw new AcceptanceException($"held rifleman fired {CaseSupport.Tag(shots.GetValueOrDefault(held))} shots under hold fire: {ShotsJson(shots).ToJson()}");
        }

        results = await LabOps.IssueAsync(
            h,
            identity,
            "combat-holdfire-2",
            new JsonArray(new JsonObject { ["pawn"] = LabOps.PawnTarget(held), ["fireMode"] = "COMBAT_FIRE_MODE_AT_WILL" }),
            cancellationToken).ConfigureAwait(false);
        if (results[0]["applied"].AsBool() != true)
        {
            throw new AcceptanceException($"fire at will refused: {results[0].ToJson()}");
        }

        shots = new SortedDictionary<string, int>(StringComparer.Ordinal);
        try
        {
            await ShotsForAsync(h, frames, seen, shots, held, 0, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            report["atWillPhase"] = ShotsJson(shots);
        }
    }

    // shotsFor steps the lab, at least min ticks and at most holdFireTicks, until until fires a shot, and counts every
    // new shot_fired event by shooter.
    private static async Task ShotsForAsync(
        Harness h,
        CombatFrames frames,
        HashSet<string> seen,
        SortedDictionary<string, int> shots,
        string until,
        int min,
        CancellationToken cancellationToken)
    {
        for (var ticks = 0; ticks < PhaseTicks; ticks += CombatLabMirror.StepTicks)
        {
            var (_, tick) = await CombatStage.TickAsync(h, CombatLabMirror.StepTicks, cancellationToken).ConfigureAwait(false);
            var state = await frames.NextAsync(tick, cancellationToken).ConfigureAwait(false);
            foreach (var e in state.Events)
            {
                var id = Geometry.EventId(e);
                if (seen.Add(id) && e["kind"].AsString() == "COMBAT_LOG_KIND_SHOT_FIRED")
                {
                    var thing = e["thingId"].AsString();
                    shots[thing] = shots.GetValueOrDefault(thing) + 1;
                }
            }

            if (shots.GetValueOrDefault(until) > 0 && ticks + CombatLabMirror.StepTicks >= min)
            {
                return;
            }
        }

        throw new AcceptanceException($"{until} fired no shot in {CaseSupport.Tag(PhaseTicks)} ticks: {ShotsJson(shots).ToJson()}");
    }

    private static JsonObject ShotsJson(SortedDictionary<string, int> shots)
    {
        var output = new JsonObject();
        foreach (var (thing, n) in shots)
        {
            output[thing] = n;
        }

        return output;
    }
}
