using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class ClockScheduler
{
    /// <summary>Records the colony's extent from the held census at the review's tick.</summary>
    internal async Task EstablishExtentAsync(Tick tick, CancellationToken ct)
    {
        if (!FactStore.TryGet<ColonyProjection>(_facts.Store, Section.Colony, out var held) || !held.Complete)
        {
            return;
        }
        var p = held.Value;
        var state = Player.Session.State();
        if (!state.ObservationKnown || p.Identity.Tick > tick || p.Identity.Colony != state.Snapshot.Colony || p.Identity.Map != state.Snapshot.Map || p.Identity.Load != state.Snapshot.Load)
        {
            return;
        }
        var f = p.Facts;
        var extent = ColonyExtentRules.DeriveColonyExtent(p.Bounds, f.CurrentConstruction, f.ConstructionClaims, f.HomeCoverage);
        if (extent is ExtentDerivation.Derived { Extent: var value })
        {
            // Cached evidence may predate a player's latest expansion. Record at this review's live tick so
            // caching cannot masquerade as a rewind.
            _ = await Player.Journal.EstablishColonyExtentAsync(state.Snapshot, tick, value.Regions, ct).ConfigureAwait(false);
        }
    }
}
