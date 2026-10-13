using System.Runtime.Versioning;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>combatlab/stun</c> (Go <c>stun.go</c>).</summary>
internal static class CombatLabStun
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "combatlab/stun",
        Scope = "The combat_pawns stun_ticks_left fact (#1050, #1118) on lab-mech: a scyther staged stunned for 600 ticks reads stun_ticks_left > 0 and at most the "
            + "stun left in the first frame after one 60-tick step, and absent in the first frame after the stun expires.",
        Start = new LabStart { Colonists = 1 },
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
            throw new AcceptanceException("combatlab/stun reads the snapshot ring from Windows named shared memory");
        }

        await RunWindowsAsync(s, cancellationToken).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    private static async Task RunWindowsAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var report = s.Report;
        var staged = await CombatStage.StageAsync(h, "lab-mech", cancellationToken).ConfigureAwait(false);
        var mechs = staged.Mechs();
        if (mechs.Count != 1)
        {
            throw new AcceptanceException($"staged {CaseSupport.Tag(mechs.Count)} mechs, want 1");
        }

        using var frames = await CombatFrames.OpenAsync(h, s.Identity, cancellationToken).ConfigureAwait(false);

        async Task<JsonObject> RowAsync(int tick)
        {
            var state = await frames.NextAsync(tick, cancellationToken).ConfigureAwait(false);
            foreach (var r in state.Pawns)
            {
                if (r["id"].AsString() == mechs[0])
                {
                    return r;
                }
            }

            throw new AcceptanceException($"no combat_pawns row for scyther {mechs[0]} in {CaseSupport.Tag(state.Pawns.Count)} rows");
        }

        var (_, tick) = await CombatStage.TickAsync(h, CombatLabMirror.StepTicks, cancellationToken).ConfigureAwait(false);
        var stunned = await RowAsync(tick).ConfigureAwait(false);
        report["stunnedRow"] = stunned["stunTicksLeft"].AsNumberOrZero();

        // The fact rounds up to 30 ticks.
        var left = (int)stunned["stunTicksLeft"].AsNumberOrZero();
        if (left <= 0 || left > CombatFixtures.MechStunTicks - CombatLabMirror.StepTicks + 30)
        {
            throw new AcceptanceException(
                $"stunned scyther reads stun_ticks_left {CaseSupport.Tag(left)} after {CaseSupport.Tag(CombatLabMirror.StepTicks)} of {CaseSupport.Tag(CombatFixtures.MechStunTicks)} ticks: {stunned.ToJson()}");
        }

        (_, tick) = await CombatStage.TickAsync(h, CombatFixtures.MechStunTicks, cancellationToken).ConfigureAwait(false);
        var after = await RowAsync(tick).ConfigureAwait(false);
        report["afterRow"] = after["stunTicksLeft"].AsNumberOrZero();
        if (after["stunTicksLeft"] is not null)
        {
            throw new AcceptanceException(
                $"scyther still reads stun_ticks_left {CaseSupport.Tag((int)after["stunTicksLeft"].AsNumberOrZero())} {CaseSupport.Tag(CombatLabMirror.StepTicks + CombatFixtures.MechStunTicks)} ticks after a {CaseSupport.Tag(CombatFixtures.MechStunTicks)}-tick stun: {after.ToJson()}");
        }
    }
}
