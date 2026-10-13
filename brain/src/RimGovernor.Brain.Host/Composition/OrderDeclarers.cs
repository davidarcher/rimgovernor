using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Observation;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Host;

// Go's planners satisfied the ledger's OrderDeclarer implicitly through a DeclareOrders method. Five of the C# planners
// carry the method but not the IOrderDeclarer interface (or, for gear and resource, a method of another shape), so the
// composition adapts them. The ledger names a declarer by its type name without the Rounds prefix and the Planner or
// Declarer suffix (Rounder.DeclarerName), so each adapter is named Rounds<Name>Declarer to keep the names Go reported.

/// <summary>Declares the armory's orders to the work ledger.</summary>
internal sealed class RoundsArmoryDeclarer(RoundsArmoryPlanner planner) : IOrderDeclarer
{
    public Task<Declared> DeclareOrdersAsync(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches, CancellationToken ct) =>
        planner.DeclareOrdersAsync(snapshot, projection, benches, ct);
}

/// <summary>Declares the gear planner's orders to the work ledger (the planner's declaration needs no instance state).</summary>
internal sealed class RoundsGearDeclarer : IOrderDeclarer
{
    public Task<Declared> DeclareOrdersAsync(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches, CancellationToken ct) =>
        Task.FromResult(RoundsGearPlanner.DeclareOrders(snapshot, projection, benches, ct));
}

/// <summary>Declares the food storage upkeep planner's orders to the work ledger.</summary>
internal sealed class RoundsFoodStorageUpkeepDeclarer(RoundsFoodStorageUpkeepPlanner planner) : IOrderDeclarer
{
    public Task<Declared> DeclareOrdersAsync(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches, CancellationToken ct) =>
        planner.DeclareOrdersAsync(snapshot, projection, benches, ct);
}

/// <summary>Declares the population joiner's decree orders to the work ledger.</summary>
internal sealed class RoundsPopulationJoinerDeclarer(RoundsPopulationJoinerPlanner planner) : IOrderDeclarer
{
    public Task<Declared> DeclareOrdersAsync(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches, CancellationToken ct) =>
        planner.DeclareOrdersAsync(snapshot, projection, benches, ct);
}

/// <summary>Declares the resource planner's orders to the work ledger.</summary>
internal sealed class RoundsResourceDeclarer(RoundsResourcePlanner planner) : IOrderDeclarer
{
    public Task<Declared> DeclareOrdersAsync(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches, CancellationToken ct) =>
        Task.FromResult(planner.DeclareOrders(snapshot, projection, benches, ct));
}
