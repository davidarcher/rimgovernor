namespace RimGovernor.Brain.Observation;

/// <summary>
/// The planner-side colony read. Identity is the paused identity a planner observes before it plans;
/// <see cref="ObservationPackage.ObserveColony"/> itself reads only the facts and validates them by their
/// ObservationContext.
/// </summary>
public interface IColonySource : ISource
{
    Task<(L.IdentityReply Reply, Result Receipt)> IdentityAsync(CancellationToken cancellationToken);

    Task<(O.ColonyFactsReply Reply, Result Receipt)> ReadColonyFactsAsync(C.Identity identity, bool planning, CancellationToken cancellationToken);

    /// <summary>The keyed tables the facts' references resolve against.</summary>
    Task<Tables> FrameTablesAsync(C.Identity identity, CancellationToken cancellationToken);
}

/// <summary>
/// An observation read under the caller's expected world, at any tick. It conveys facts, not authority; callers
/// must still check their player direction before committing.
/// </summary>
public sealed record ColonyReading
{
    public ColonyProjection Projection { get; init; } = new();

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset ObservedAt { get; init; }

    public Result? Receipt { get; init; }
}

public static partial class ObservationPackage
{
    /// <summary>
    /// The rule every routine read applies to a reply's context: the expected load, map and native generation.
    /// Its tick never makes it stale; every section of one read comes from one frame.
    /// </summary>
    public static bool SameColonyContext(Identity actual, Identity expected) =>
        actual.NativeGeneration.TryGetValue(out var a)
        && expected.NativeGeneration.TryGetValue(out var b)
        && actual.SameContext(expected)
        && a == b;

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
        if (!SameColonyContext(expected, expected))
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
        var (reply, receipt) = await source.ReadColonyFactsAsync(id, planning, cancellationToken).ConfigureAwait(false);
        var tables = await source.FrameTablesAsync(id, cancellationToken).ConfigureAwait(false);
        var catalog = source is IDefinitionSource definitionSource
            ? await definitionSource.DefinitionCatalogAsync(id, cancellationToken).ConfigureAwait(false)
            : null;
        var projection = DecodeColony(reply, expected, tables, catalog);
        if (planning)
        {
            projection = await FillPlanningWindow(reply, id, projection, cancellationToken).ConfigureAwait(false);
            var facts = await ReadDefinitionFacts(source, id, reply.Outcome?.Observed?.Planning?.Outcome?.Observed, cancellationToken).ConfigureAwait(false);
            if (facts is not null)
            {
                var definitions = facts.AppendDefinitions([.. projection.Definitions], StarterDefinitions);
                // The dining table, chair and recreation foothold are the catalog's rules, not names
                // (DefinitionCatalog.DiningFurniture).
                var dining = facts.Catalog.DiningFurniture();
                definitions = facts.AppendDefinitions(definitions, dining.Definitions());
                // The furniture the room planners place is the catalog's rules too (DefinitionCatalog.RoomFurniture).
                var shapes = facts.Catalog.PieceShapes();
                definitions = facts.AppendDefinitions(definitions, shapes.Furniture.Definitions());
                var gearDefs = new GearDefinitions { Catalog = facts.Catalog, Finished = facts.Finished };
                var gear = GearFacts(reply.Outcome?.Observed!, tables, gearDefs);
                projection = projection with
                {
                    Definitions = definitions,
                    Shapes = shapes,
                    Facts = projection.Facts with { Gear = gear, Garments = EquatableArray.From(ClothingGarments(gearDefs, gear)) },
                };
            }
        }
        projection = await FillZones(source as IZonesNative, id, expected, projection, cancellationToken).ConfigureAwait(false);
        if (planning)
        {
            projection = projection with { Facts = projection.Facts with { Blight = BlightCensus(projection) } };
        }
        // Colony facts do not carry pause state; that fact is the caller's.
        var observed = projection.Identity with { Paused = expected.Paused };
        if (!SameColonyContext(observed, expected))
        {
            throw new ObservationChangedException();
        }
        var observedAt = clock.Now();
        if (observedAt < startedAt || observedAt - startedAt > maxAge)
        {
            throw new ObservationStaleException();
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new ColonyReading { Projection = projection, StartedAt = startedAt, ObservedAt = observedAt, Receipt = receipt };
    }
}
