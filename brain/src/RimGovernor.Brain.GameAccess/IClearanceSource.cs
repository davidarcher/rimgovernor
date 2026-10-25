using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Facts;
namespace RimGovernor.Brain.GameAccess;

public interface IClearanceSource
{
    /// <summary>
    /// The clearance census for <paramref name="expected"/>; non-empty <paramref name="ground"/> widens it to the
    /// player's buildings (Player rows) and constructed floors on those rectangles. Throws when the native cannot
    /// read it, and <see cref="ObservationChangedException"/> when the reply answers another colony context.
    /// </summary>
    Task<RimGovernor.Brain.GameFacts.Observations.ClearanceTargetsSnapshot> ReadClearanceAsync(ObservationIdentity expected, bool includeSalvage, IReadOnlyList<Rectangle> ground, CancellationToken cancellationToken);
}

