namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class ClockScheduler
{
    /// <summary>Port of colony_extent.go: records the colony's extent from the held census at the review's tick.</summary>
    internal async Task EstablishExtentAsync(Tick tick, CancellationToken ct)
    {
        if (!FactsApi.TryGet<ColonyProjection>(_facts.Store, Section.Colony, out var held) || !held.Complete)
        {
            return;
        }
        var p = held.Value;
        var state = _player.Session.State();
        if (!state.ObservationKnown || p.Identity.Tick > tick || p.Identity.Colony != state.Snapshot.Colony || p.Identity.Map != state.Snapshot.Map || p.Identity.Load != state.Snapshot.Load)
        {
            return;
        }
        var f = p.Facts;
        var extent = ColonyExtentRules.DeriveColonyExtent(new ColonyExtentRequest { Bounds = Known(p.Bounds), Construction = f.CurrentConstruction, Claims = f.ConstructionClaims, Home = f.HomeCoverage });
        if (extent.TryGetValue(out var value))
        {
            // Cached evidence may predate a player's latest expansion. Record at this review's live tick so
            // caching cannot masquerade as a rewind.
            _ = await _player.Journal.EstablishColonyExtentAsync(state.Snapshot, tick, value.Regions, ct).ConfigureAwait(false);
        }
    }
}
