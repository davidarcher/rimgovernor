using RimGovernor.Contracts.Wire.Common;
using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Bridge.Contracts;

/// <summary>
/// The complete zone census, rows in id order. Rows are immutable after publication to the
/// facts store.
/// </summary>
public sealed record ZonesRead(ObservationContext? Context, IReadOnlyList<ZoneState> Rows, long AsOf)
{
    /// <summary>The empty read.</summary>
    public static readonly ZonesRead Empty = new(null, [], 0);
}
