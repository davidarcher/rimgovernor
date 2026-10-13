using System.Runtime.Versioning;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>combatlab/breach</c> (Go <c>breach.go</c>).</summary>
internal static class CombatLabBreach
{
    // breachDigTicks bounds the walk to the wall and the first Mine job: the sappers stand 15 cells off it.
    private const int DigTicks = 3000;

    internal static AcceptanceCase Case() => new()
    {
        Name = "combatlab/breach",
        Scope = "The sapper Mine job on lab-breach (#1149, #913-#915): a sapper raid staged against a doorless walled room digs in, and a frame's combat_pawns row "
            + "reads a raider's job Mine while it stands against the wall outside the room, within 3000 ticks. Native because the job is vanilla's sapper AI digging, not a recorded fact.",
        Start = new LabStart { Colonists = 3 },
        RequiredOps = [DebugGame.LabStartTool, CombatFixtures.StageTool],
        QuietWorld = true,
        Budget = AcceptanceCase.LabBudget,
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new AcceptanceException("combatlab/breach reads the snapshot ring from Windows named shared memory");
        }

        await RunWindowsAsync(s, cancellationToken).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    private static async Task RunWindowsAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var report = s.Report;
        var staged = await CombatStage.StageAsync(h, "lab-breach", cancellationToken).ConfigureAwait(false);
        var raiders = new HashSet<string>(staged.Hostiles(), StringComparer.Ordinal);

        // The room's centre is the first colonist's cell two north.
        var cx = staged.Fixture.Pawns[0].X + 2;
        var cz = staged.Fixture.Pawns[0].Z + 2;
        using var frames = await CombatFrames.OpenAsync(h, s.Identity, cancellationToken).ConfigureAwait(false);
        var jobs = new SortedDictionary<string, int>(StringComparer.Ordinal);
        for (var ticks = CombatLabMirror.StepTicks; ticks <= DigTicks; ticks += CombatLabMirror.StepTicks)
        {
            var (_, tick) = await CombatStage.TickAsync(h, CombatLabMirror.StepTicks, cancellationToken).ConfigureAwait(false);
            var state = await frames.NextAsync(tick, cancellationToken).ConfigureAwait(false);
            foreach (var row in state.Pawns)
            {
                var id = row["id"].AsString();
                if (!raiders.Contains(id))
                {
                    continue;
                }

                var job = row["job"].AsString();
                jobs[job] = jobs.GetValueOrDefault(job) + 1;
                if (job != "Mine")
                {
                    continue;
                }

                // Mine walks to the wall first: digging is Mine against it, from a cell just outside the ring.
                var cell = row["cell"].AsMap() ?? [];
                var x = (int)cell["x"].AsNumberOrZero();
                var z = (int)cell["z"].AsNumberOrZero();
                if (Math.Max(Math.Abs(x - cx), Math.Abs(z - cz)) != CombatFixtures.BreachHalf + 1)
                {
                    continue;
                }

                report["mine"] = new JsonObject { ["pawn"] = id, ["x"] = x, ["z"] = z, ["ticks"] = ticks };
                report["jobs"] = JobsJson(jobs);
                return;
            }
        }

        report["jobs"] = JobsJson(jobs);
        throw new AcceptanceException($"no raider read job Mine against the wall in {CaseSupport.Tag(DigTicks)} ticks of lab-breach: {JobsJson(jobs).ToJson()}");
    }

    private static JsonObject JobsJson(SortedDictionary<string, int> jobs)
    {
        var output = new JsonObject();
        foreach (var (job, n) in jobs)
        {
            output[job] = n;
        }

        return output;
    }
}
