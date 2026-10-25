using DefinitionCatalog = RimGovernor.Brain.Bridge.DefinitionCatalog;
namespace RimGovernor.Brain.BuildingRuntime;

public static class ColonyReads
{
    /// <summary>
    /// The rule every routine read applies to a reply's context: the expected load, map and native generation.
    /// Its tick never makes it stale; every section of one read comes from one frame.
    /// </summary>
    /// <summary>
    /// Requires an externally observed identity and reads the facts under it. Every reply carries an
    /// ObservationContext, so the facts themselves prove they were read at the expected load, map and
    /// generation; no identity read brackets them. Missing generations cannot confirm the boundary.
    /// </summary>
    public static async Task<ColonyReading> ObserveColony(
        IColonySource? source, IClock? clock, Identity expected, TimeSpan maxAge, bool planning, CancellationToken cancellationToken)
    {
        if (source is null || clock is null || maxAge <= TimeSpan.Zero)
        {
            throw new ObservationContractException("missing observation dependency");
        }
        expected.Validate();
        if (!ColonyContexts.SameColonyContext(expected, expected))
        {
            throw new ObservationChangedException();
        }
        var startedAt = clock.Now();
        if (startedAt == default)
        {
            throw new ObservationStaleException();
        }
        cancellationToken.ThrowIfCancellationRequested();
        var id = new C.Identity { ColonyId = expected.Colony.Value, LoadToken = expected.Load.Value, MapId = expected.Map.Value };
        var reply = await source.ReadColonyFactsAsync(id, cancellationToken).ConfigureAwait(false);
        var tables = await source.FrameTablesAsync(id, cancellationToken).ConfigureAwait(false);
        var catalog = source is IDefinitionSource definitionSource
            ? await definitionSource.DefinitionCatalogAsync(id, cancellationToken).ConfigureAwait(false)
            : null;
        var reading = ColonyPlanningReads.Compose(reply, expected, tables, catalog);
        if (planning)
        {
            reading = await PlanningWindowReads.FillPlanningWindow(id, reading, cancellationToken).ConfigureAwait(false);
            if (catalog is not null && reading.Window is not null)
            {
                reading = reading with { Facts = reading.Facts with { RingSources = catalog.RingStuffSourcesOf(reading.PlanningWindow.Cells) } };
            }
            var facts = await DefinitionReads.ReadDefinitionFacts(source, id, reply.Crops, cancellationToken).ConfigureAwait(false);
            if (facts is not null)
            {
                var definitions = facts.AppendDefinitions([.. reading.Facts.Definitions], DefinitionsReads.StarterDefinitions);
                // The dining table, chair and recreation foothold are the catalog's rules, not names
                // (DefinitionCatalog.DiningFurniture).
                var dining = facts.Catalog.DiningFurniture();
                definitions = facts.AppendDefinitions(definitions, dining.Definitions());
                // The furniture the room planners place is the catalog's rules too (DefinitionCatalog.RoomFurniture).
                var shapes = facts.Catalog.PieceShapes();
                definitions = facts.AppendDefinitions(definitions, shapes.Furniture.Definitions());
                reading = reading with { Facts = reading.Facts with { Definitions = definitions, Shapes = shapes } };
            }
        }
        // Colony facts do not carry pause state; that fact is the caller's.
        var observed = reading.Identity with { Paused = expected.Paused };
        if (!ColonyContexts.SameColonyContext(observed, expected))
        {
            throw new ObservationChangedException();
        }
        var observedAt = clock.Now();
        if (observedAt < startedAt || observedAt - startedAt > maxAge)
        {
            throw new ObservationStaleException();
        }
        cancellationToken.ThrowIfCancellationRequested();
        return reading with { Identity = observed, StartedAt = startedAt, ObservedAt = observedAt };
    }
}
