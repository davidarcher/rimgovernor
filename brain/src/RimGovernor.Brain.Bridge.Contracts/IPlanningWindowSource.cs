using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Facts;
using C = RimGovernor.Contracts.Wire.Common;
using O = RimGovernor.Contracts.Wire.Observations;
using L = RimGovernor.Contracts.Wire.Lifecycle;
namespace RimGovernor.Brain.Bridge.Contracts;

public interface IPlanningWindowSource
{
    Task<Held<PlanningCells>> PlanningWindowAsync(C.Identity identity, Rectangle region, CancellationToken cancellationToken);
}

/// <summary>
/// A planning window source that also knows the stored layout plan's extent (<c>LayoutPlan.Extent</c>): the
/// window then covers the plan as well as the colonists. An empty rect means no plan.
/// </summary>
public interface IPlanExtentSource : IPlanningWindowSource
{
    Task<Rectangle> PlanExtentAsync(CancellationToken cancellationToken);
}

