namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>Reads the colony facts section (Go <c>colonyFactsReader</c>).</summary>
public interface IColonyFactsReader
{
    Task<PbObservations.ColonyFactsReply> ReadColonyFactsAsync(PbCommon.Identity identity, bool planning, CancellationToken ct);
}

/// <summary>Port of colony_mirror.go: the review's colony facts, mirrored per sub-section in the fact store.</summary>
internal static class ColonyMirror
{
    /// <summary>
    /// Publishes every colony facts sub-section (split by row path) of the review frame's colony facts (planning,
    /// no extra definitions) and returns the tables' mirror versions.
    /// </summary>
    public static Dictionary<string, ulong> PublishColony(FactStore m, FactScope scope, PbObservations.ColonyFactsSnapshot observed)
    {
        var split = BridgeApi.SplitColonyFacts(observed);
        var tick = observed.Context.Tick;
        var versions = new Dictionary<string, ulong>(split.Count, StringComparer.Ordinal);
        foreach (var name in BridgeApi.ColonySections())
        {
            versions[name] = StoreTables.PutTable(m, scope, name, split[name], Watermark.At(tick)).Version;
        }
        return versions;
    }

    /// <summary>Rebuilds the colony facts the mirror holds at <paramref name="versions"/>; null when any section moved on or is gone.</summary>
    public static PbObservations.ColonyFactsSnapshot? ColonyTables(FactStore m, FactScope scope, IReadOnlyDictionary<string, ulong> versions)
    {
        var tables = new Dictionary<string, IReadOnlyDictionary<string, ColonyRow>>(versions.Count, StringComparer.Ordinal);
        foreach (var (name, version) in versions)
        {
            if (!StoreTables.TryGetTable<string, ColonyRow>(m, scope, name, out var table) || table.Version != version)
            {
                return null;
            }
            tables[name] = table.Rows;
        }
        try
        {
            return BridgeApi.JoinColonyFacts(tables);
        }
        catch (DomainException)
        {
            return null;
        }
    }
}

public sealed partial class Rounder
{
    /// <summary>
    /// A planner's colony facts read (no extra definitions): the review's mirrored census while it serves the
    /// planner's current identity under the part-1 reuse rule (same world and generation, within the pawn cadence
    /// after the review, no clock invalidation since), otherwise native. A read without planning gets the planning
    /// section the native answers it with.
    /// </summary>
    internal Task<PbObservations.ColonyFactsReply> ColonyFactsAsync(IColonyFactsReader native, PbCommon.Identity identity, bool planning, CancellationToken ct)
    {
        if (!SameNativeSource(native, Native))
        {
            return native.ReadColonyFactsAsync(identity, planning, ct);
        }
        return ServedColonyFactsAsync(native, identity, planning, ct);
    }

    private async Task<PbObservations.ColonyFactsReply> ServedColonyFactsAsync(IColonyFactsReader native, PbCommon.Identity identity, bool planning, CancellationToken ct)
    {
        var mirrored = await MirroredColonyAsync(identity, ct).ConfigureAwait(false);
        if (mirrored is not null)
        {
            if (!planning)
            {
                mirrored.Planning = BridgeApi.NotRequestedPlanning();
            }
            return new PbObservations.ColonyFactsReply { Observed = mirrored };
        }
        return await native.ReadColonyFactsAsync(identity, planning, ct).ConfigureAwait(false);
    }

    private async Task<PbObservations.ColonyFactsSnapshot?> MirroredColonyAsync(PbCommon.Identity identity, CancellationToken ct)
    {
        if (Store is null)
        {
            return null;
        }
        RoundsCensus? census;
        ulong generation;
        lock (Census.Mu)
        {
            census = Census.Latest;
            generation = Census.Generation;
        }
        if (census is null || census.Colony is null || census.Generation != generation)
        {
            return null;
        }
        Identity expected;
        try
        {
            expected = await StepScopeAsync(Native, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
        if (expected.Colony.Value != identity.ColonyId || expected.Load.Value != identity.LoadToken || expected.Map.Value != identity.MapId)
        {
            return null;
        }
        if (!SameObservedIdentity(census.Reading.Projection.Identity, expected))
        {
            return null;
        }
        expected.NativeGeneration.TryGetValue(out var nativeGeneration);
        var scope = new FactScope(expected.Load.Value, expected.Map.Value, (ulong)nativeGeneration.Value);
        return ColonyTables(Store, scope, census.Colony);
    }

    private static PbObservations.ColonyFactsSnapshot? ColonyTables(FactStore store, FactScope scope, IReadOnlyDictionary<string, ulong> versions) =>
        ColonyMirror.ColonyTables(store, scope, versions);
}
