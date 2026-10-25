using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Facts;
using C = RimGovernor.Contracts.Wire.Common;
using O = RimGovernor.Contracts.Wire.Observations;
using L = RimGovernor.Contracts.Wire.Lifecycle;
namespace RimGovernor.Brain.Bridge.Contracts;

public interface IClearanceSource
{
    /// <summary>
    /// The clearance census for <paramref name="expected"/>; non-empty <paramref name="ground"/> widens it to the
    /// player's buildings (Player rows) and constructed floors on those rectangles. Throws when the native cannot
    /// read it, and <see cref="ObservationChangedException"/> when the reply answers another colony context.
    /// </summary>
    Task<O.ClearanceTargetsSnapshot> ReadClearanceAsync(ObservationIdentity expected, bool includeSalvage, IReadOnlyList<Rectangle> ground, CancellationToken cancellationToken);
}

