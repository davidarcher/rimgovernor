using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Host;

// The ledger's OrderDeclarer is satisfied implicitly through a DeclareOrders method. Five of the C# planners
// carry the method but not the IOrderDeclarer interface (or, for gear and resource, a method of another shape), so the
// composition adapts them, each adapter named ReviewState<Name>Declarer after the planner it wraps.

/// <summary>Declares the armory's orders to the work ledger.</summary>
internal sealed class ArmoryDeclarer(ArmoryPlanner planner) : IOrderDeclarer
{
    public Task<Declared> DeclareOrdersAsync(GenerationSnapshot snapshot, ColonyReading projection, IReadOnlyList<GearBench> benches, CancellationToken ct) =>
        planner.DeclareOrdersAsync(snapshot, projection, benches, ct);
}

/// <summary>Declares the gear planner's orders to the work ledger (the planner's declaration needs no instance state).</summary>
internal sealed class GearDeclarer : IOrderDeclarer
{
    public Task<Declared> DeclareOrdersAsync(GenerationSnapshot snapshot, ColonyReading projection, IReadOnlyList<GearBench> benches, CancellationToken ct) =>
        Task.FromResult(GearPlanner.DeclareOrders(snapshot, projection, benches, ct));
}

/// <summary>Declares the food stores family's preservation order to the work ledger.</summary>
internal sealed class FoodStoresDeclarer(FoodStoresReview planner) : IOrderDeclarer
{
    public Task<Declared> DeclareOrdersAsync(GenerationSnapshot snapshot, ColonyReading projection, IReadOnlyList<GearBench> benches, CancellationToken ct) =>
        planner.DeclareOrdersAsync(snapshot, projection, benches, ct);
}

/// <summary>Declares the joiners family's decree orders to the work ledger.</summary>
internal sealed class JoinersDeclarer(JoinersReview planner) : IOrderDeclarer
{
    public Task<Declared> DeclareOrdersAsync(GenerationSnapshot snapshot, ColonyReading projection, IReadOnlyList<GearBench> benches, CancellationToken ct) =>
        planner.DeclareOrdersAsync(snapshot, projection, benches, ct);
}

/// <summary>Declares the resource planner's orders to the work ledger.</summary>
internal sealed class ResourceDeclarer(ResourcePlanner planner) : IOrderDeclarer
{
    public Task<Declared> DeclareOrdersAsync(GenerationSnapshot snapshot, ColonyReading projection, IReadOnlyList<GearBench> benches, CancellationToken ct) =>
        Task.FromResult(planner.DeclareOrders(snapshot, projection, benches, ct));
}
