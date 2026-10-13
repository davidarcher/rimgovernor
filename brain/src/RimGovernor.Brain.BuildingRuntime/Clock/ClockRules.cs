namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class ClockScheduler
{
    /// <summary>
    /// Reads the latest rules attachment, including retired methods, and returns the native-work window it
    /// leaves. A queued intent or missing receipt cannot establish a live native lease.
    /// </summary>
    internal async Task<uint> RulesWindowAsync(GenerationSnapshot current, Tick tick, uint maxTicks, CancellationToken ct)
    {
        if (Config.Rules is null)
        {
            return maxTicks;
        }
        var plans = await _player.Journal.PlanHistoryWithMethodsAsync(1, "rules-*", ct).ConfigureAwait(false);
        var lease = Unknown<RuleLease>();
        if (plans.Count == 1)
        {
            foreach (var progress in plans[0].Progress)
            {
                var attach = progress.Action.RulesAttach;
                var view = progress.View;
                if (attach is not null && attach.Rules.Count > 0 && view.Stage == Stage.Completed
                    && view.Receipt.TryGetValue(out var receipt) && receipt == Receipt.Accepted
                    && view.Snapshot.SameWorld(current) && view.Snapshot.Native == current.Native)
                {
                    lease = Known(new RuleLease { Dispatched = view.Tick, Ticks = attach.LeaseTicks });
                }
            }
        }
        return PolicyFunctions.RulesWindowTicks(tick, maxTicks, lease);
    }

    /// <summary>
    /// Each rules method is keyed by its review tick, so only its admitting step yields to Hands. A retry,
    /// unknown census or failed receipt cannot park time.
    /// </summary>
    internal static bool RulesDispatchTurn(bool worker, RoundsRulesResult? rules) =>
        worker && rules is not null && rules.Verdict == BuildingReasonAdmitted;
}
