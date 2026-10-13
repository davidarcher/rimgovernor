using RimGovernor.Brain.Store;
using ReviewRecord = RimGovernor.Brain.Store.Rounds;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The <c>child/*</c> acceptance cases (Go <c>cases/child</c>); <see cref="Register"/> replaces the Go package's
/// <c>init()</c>. The journal helpers here are shared by the other serve-driven families of this port (anomaly, dialog,
/// explain), which read the service's durable plan history the way Go read <c>*store.Store</c>.
/// </summary>
public static class ChildCases
{
    /// <summary>Registers every <c>child/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(ChildFirstChild.Case());
        registry.Register(ChildMechGestation.Case());
    }

    /// <summary>Opens the run's durable journal (Go <c>service.Store(ctx)</c>), retrying while the service is still creating it.</summary>
    internal static Task<Journal> OpenJournalAsync(ISession s, CancellationToken cancellationToken) =>
        ServiceJournal.OpenWithRetryAsync(ServiceJournal.PathOf(s.Config), cancellationToken);

    /// <summary>The most recently committed plans admitted under a method matching the GLOB pattern (Go <c>PlanHistoryWithMethods(ctx, 256, pattern)</c>).</summary>
    internal static Task<IReadOnlyList<PlanState>> PlanHistoryAsync(Journal journal, string pattern, CancellationToken cancellationToken) =>
        Task.Run(() => journal.PlanHistoryWithMethods(256, [pattern], cancellationToken), cancellationToken);

    /// <summary>The persisted review (Go <c>st.LoadRounds</c>), or null while none was persisted yet.</summary>
    internal static async Task<ReviewRecord?> TryRoundsAsync(Journal journal, CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(() => journal.LoadRounds(cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }
}
