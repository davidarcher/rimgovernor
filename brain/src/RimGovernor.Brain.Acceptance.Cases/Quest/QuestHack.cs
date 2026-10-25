
namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>quest/hack</c>: the HackDesignationIntent arm of operations_apply on an untouched Ideology terminal. A thing
/// without CompHackable is refused; enable, its replay and disable land on the comp's autohack flag; re-enabled, the
/// ordinary Research WorkGiver sends the colony's one Research worker to hack it to completion.
/// </summary>
internal static class QuestHack
{
    private const int HackTicks = 6000;

    internal static TypedCase Case() => new()
    {
        Name = "quest/hack",
        Scope = "Native autohack enable/disable, replay and unhackable-target refusal, followed by ordinary Research WorkGiver "
            + "hacking an Ideology terminal to completion by the exact Research worker.",
        Ticks = HackTicks,
        Tier = CaseTier.OnDemand,
        NoKeep = true,
        Expansions = ["ludeon.rimworld.ideology"],
        Ops = [QuestKit.HackPrepareTool, QuestKit.HackReadTool],
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var prepared = await s.FixtureAsync("prepare", QuestKit.HackPrepare, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        var terminal = prepared.TerminalId;
        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);

        Task<RimGovernor.Brain.GameFacts.Operations.ActionResult> Apply(string key, string target, bool enabled) => TradeKit.ApplyAsync(s, key, new RimGovernor.Brain.GameOrders.Operations.HackDesignationIntent
        {
            Target = new RimGovernor.Brain.GameFacts.Common.Ref { Id = target },
            Enabled = enabled,
        }, cancellationToken);

        async Task Applied(string key, bool enabled)
        {
            var effect = TradeKit.Applied(key, await Apply(key, terminal, enabled).ConfigureAwait(false)).HackDesignation;
            if (effect?.Enabled != enabled || effect.TargetId != terminal)
            {
                throw new AcceptanceException($"{key}: expected the hack designation on {terminal} {(enabled ? "enabled" : "disabled")}, got {effect}");
            }
        }

        Task<RimGovernor.Fixtures.Contracts.QuestHackRead> Read(string label) => s.FixtureAsync(label, QuestKit.HackRead, terminal, cancellationToken);

        var before = await Read("hack-before").ConfigureAwait(false);
        if (before.Hacked || before.Progress != 0 || before.Autohack)
        {
            throw new AcceptanceException($"hack-before: the fixture supplied progress: {ServedOps.Json(before)}");
        }

        var unhackable = await Apply("hack-unavailable", prepared.UnhackableItemId, true).ConfigureAwait(false);
        if (TradeKit.RefusedCode(unhackable) != RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable)
        {
            throw new AcceptanceException($"hack-unavailable: expected an UNAVAILABLE refusal, got {unhackable}");
        }

        await Applied("hack-enable", true).ConfigureAwait(false);
        await Applied("hack-enable", true).ConfigureAwait(false);
        if (!(await Read("hack-enabled").ConfigureAwait(false)).Autohack)
        {
            throw new AcceptanceException("hack-enabled: the enable or its replay left autohack off");
        }

        await Applied("hack-disable", false).ConfigureAwait(false);
        if ((await Read("hack-disabled").ConfigureAwait(false)).Autohack)
        {
            throw new AcceptanceException("hack-disabled: the disable left autohack on");
        }

        // Re-enabled, ordinary Research work hacks the terminal in game time.
        await Applied("hack-work", true).ConfigureAwait(false);
        RimGovernor.Fixtures.Contracts.QuestHackRead? done = null;
        _ = await s.RunUntilAsync("hack-work", HackTicks, async ct =>
        {
            var read = await s.FixtureAsync("hack-work-read", QuestKit.HackRead, terminal, ct).ConfigureAwait(false);
            done = read.Hacked ? read : null;
            return ($"{read.Progress:F3} {read.Hacked}", read.Hacked);
        }, cancellationToken).ConfigureAwait(false);
        s.Record("hacked", ServedOps.Json(done));
        if (done!.HackerId != prepared.HackerId)
        {
            throw new AcceptanceException($"hack-work: expected {prepared.HackerId} to hack the terminal, got {ServedOps.Json(done)}");
        }
    }
}
