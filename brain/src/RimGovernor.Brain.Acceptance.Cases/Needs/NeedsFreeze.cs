using System.Globalization;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>needs/freeze</c>: proves test/freeze_needs, the harness control every case's need freeze rests on. With every need
/// but Rest frozen, a tenth of a day leaves each free colonist's frozen needs at maximum while Rest keeps moving; release
/// lets them fall again.
/// </summary>
internal static class NeedsFreeze
{
    // The one need the session leaves live; the freeze itself is what the case proves.
    private const string Kept = "Rest";

    // A tenth of a day: Rest falls by a few hundredths awake, more than enough to show it is live, while a frozen need
    // would fall the same.
    private const int Advance = 6000;

    private static readonly FixtureOp<string, Fx.FrozenNeedsRead> Freeze = new(
        Needs.FreezeNeedsTool,
        action => ServedOps.Args(("action", action)),
        ServedOps.Reply<Fx.FrozenNeedsRead>(Needs.FreezeNeedsTool));

    internal static TypedCase Case() => new()
    {
        Name = "needs/freeze",
        Scope = "test/freeze_needs pins every free colonist need but the kept ones at maximum across an advance and releases "
            + "them on request.",
        Ticks = 2 * Advance,
        Tier = CaseTier.OnDemand,
        Ops = [Needs.FreezeNeedsTool],
        Keep = [Kept],
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        async Task<Dictionary<(string Pawn, string Def), double>> LevelsAsync(string label, string action)
        {
            var read = await s.FixtureAsync(label, Freeze, action, cancellationToken).ConfigureAwait(false);
            s.Record(label, ServedOps.Json(read));
            var flagged = read.Needs.SelectMany(p => p.Needs.Where(n => n.Frozen != (read.Frozen && n.Def != Kept)).Select(n => $"{n.Def} on {p.Pawn}")).ToList();
            if (flagged.Count > 0)
            {
                throw new AcceptanceException($"{label}: needs flagged against the freeze {read.Frozen}: {string.Join("; ", flagged)}");
            }

            var levels = read.Needs.SelectMany(p => p.Needs.Select(n => (Key: (p.Pawn, n.Def), n.Level))).ToDictionary(r => r.Key, r => r.Level);
            return levels.Count == 0 ? throw new AcceptanceException($"{label}: no free colonist needs read") : levels;
        }

        var before = await LevelsAsync("needs-before", "inspect").ConfigureAwait(false);
        await s.RunTicksAsync("frozen", Advance, cancellationToken).ConfigureAwait(false);
        var after = await LevelsAsync("needs-after", "inspect").ConfigureAwait(false);
        var fallen = after.Where(n => n.Key.Def != Kept && n.Value < 0.95).Select(n => string.Create(CultureInfo.InvariantCulture, $"{n.Key.Def} on {n.Key.Pawn} at {n.Value:0.000}")).ToList();
        if (fallen.Count > 0)
        {
            throw new AcceptanceException($"frozen needs fell: {string.Join("; ", fallen)}");
        }

        if (!after.Any(n => n.Key.Def == Kept && n.Value != before.GetValueOrDefault(n.Key)))
        {
            throw new AcceptanceException($"kept need {Kept} did not move on any colonist");
        }

        var released = await s.FixtureAsync("release", Freeze, "release", cancellationToken).ConfigureAwait(false);
        if (!released.Success || released.Frozen)
        {
            throw new AcceptanceException($"release left needs frozen: {ServedOps.Json(released)}");
        }

        await s.RunTicksAsync("released", Advance, cancellationToken).ConfigureAwait(false);
        var final = await LevelsAsync("needs-released", "inspect").ConfigureAwait(false);
        var fell = final.Count(n => n.Key.Def != Kept && n.Value < after.GetValueOrDefault(n.Key));
        if (fell == 0)
        {
            throw new AcceptanceException("no need fell after release");
        }

        s.Record("fell_after_release", fell);
    }
}
