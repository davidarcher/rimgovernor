using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using JournalStore = RimGovernor.Brain.Store.Store;
using StoreRounds = RimGovernor.Brain.Store.Rounds;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// A running service together with the controller journal it writes (Go: the <c>*store.Store</c> a case got from
/// <c>service.Store(ctx)</c>). The player API shows a goal's method and plan but not its Finding, so the cases
/// that assert on it open the journal itself, read-only in
/// practice: <c>&lt;output&gt;/service.sqlite</c>, the file the checkpoint ring and the postmortem already expect there.
/// Where the in-mod brain keeps that file is the one thing this seam depends on; every case reaches it through
/// <see cref="Open"/>.
/// </summary>
internal sealed class ControllerJournal : IDisposable
{
    private ControllerJournal(ServiceProcess service, JournalStore store)
    {
        Service = service;
        Store = store;
    }

    /// <summary>The service handle whose waits (methods, plans, rounds) the journal complements.</summary>
    internal ServiceProcess Service { get; }

    /// <summary>The journal itself.</summary>
    internal JournalStore Store { get; }

    /// <summary>Opens the case's journal beside <paramref name="service"/>.</summary>
    internal static ControllerJournal Open(ISession s, ServiceProcess service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(service);
        var path = ServiceJournal.PathOf(s.Config);
        return new ControllerJournal(service, JournalStore.Open(path, cancellationToken));
    }

    /// <summary>The stored review; an empty journal has the zero review.</summary>
    internal Task<StoreRounds> LoadRoundsAsync(CancellationToken cancellationToken) => Store.LoadRoundsAsync(cancellationToken);

    /// <summary>
    /// Polls the review under <paramref name="w"/> until <paramref name="ready"/> accepts it (Go <c>na.WaitReview</c>);
    /// the progress signature is the review's revision and tick.
    /// </summary>
    internal Task<StoreRounds> WaitReviewAsync(Wait w, Func<StoreRounds, bool> ready, CancellationToken cancellationToken) =>
        ServiceJournal.WaitReviewAsync(
            Store,
            w.Terminal is null ? w with { Terminal = Service.Exited } : w,
            ready,
            review => Wait.Signature(review.Revision, review.Tick.Value),
            cancellationToken);

    public void Dispose() => Store.Dispose();
}

/// <summary>The journal readings the upkeep family makes of a goal.</summary>
internal static class JournalReads
{
    /// <summary>The node as an object, or an empty one (Go: a missing map reads as its zero value).</summary>
    internal static JsonObject Obj(this JsonNode? value) => value as JsonObject ?? [];

    /// <summary>A Finding as Go printed it (met, unmet, unclear).</summary>
    internal static string FindingName(Finding finding) => finding.ToString().ToLowerInvariant();
}
