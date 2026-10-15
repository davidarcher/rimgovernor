using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>Separates unsupported automatic labor from explicit native mechanics coverage.</summary>
internal static class UnsupportedLabor
{
    internal static async Task<Harness> ReviewAsync(ISession s, string subject, ActionKind kind, CancellationToken ct)
    {
        var service = await s.ServeAsync(s.Spec, ct).ConfigureAwait(false);
        try
        {
            _ = await service.AcquireAsync(ct).ConfigureAwait(false);
            service.KeepAuthority(ct);
            using var store = await ServiceJournal.OpenWithRetryAsync(s.Config, ct).ConfigureAwait(false);
            await new Wait { Ceiling = TimeSpan.FromMinutes(2), Terminal = service.Exited }.WaitProgressAsync(async token =>
            {
                var review = await ServiceJournal.TryRoundsAsync(store, token).ConfigureAwait(false);
                return (Wait.Signature(review?.Revision, review?.Tick.Value),
                    review?.Progress.Any(p => p.PlannerSubject == subject) == true);
            }, ct).ConfigureAwait(false);
            var plans = await ServiceJournal.PlanHistoryAsync(store, "*", ct).ConfigureAwait(false);
            if (plans.Any(p => p.Spec.Actions.Any(a => a.Kind == kind)))
                throw new AcceptanceException("Unsupported automatic labor admitted " + kind);
            s.Report["unsupported_labor"] = subject;
            s.Report["retired_action_admissions"] = 0;
        }
        finally { _ = await service.StopAsync().ConfigureAwait(false); }
        var h = s.Reattach();
        await NativeReads.PauseAsync(h, "unsupported-labor-pause", ct).ConfigureAwait(false);
        return h;
    }
}
