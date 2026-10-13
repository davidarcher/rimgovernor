using RimGovernor.Contracts.Wire.Common;
using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Bridge.Contracts;

/// <summary>
/// Go's <c>ZonesRead</c>: the complete zone census, rows in id order. Rows are immutable after publication to the
/// facts store.
/// </summary>
public sealed record ZonesRead(ObservationContext? Context, IReadOnlyList<ZoneState> Rows, long AsOf)
{
    /// <summary>The empty read (Go's <c>ZonesRead{}</c>).</summary>
    public static readonly ZonesRead Empty = new(null, [], 0);
}
