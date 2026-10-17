using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// The colonist's native body stats (observations PawnBodyStats): the game's own stat and hediff values at
/// the read tick. Each field is unknown where native could not read it, never zero. Severities are 0 when the
/// pawn has no such hediff; capacity levels are 1 at full.
/// </summary>
public sealed record PawnBody
{
    /// <summary>The comfortable temperature range with the worn apparel, in degrees C (minimum).</summary>
    public Fact<double> ComfortableMinC { get; init; }

    /// <summary>The comfortable temperature range with the worn apparel, in degrees C (maximum).</summary>
    public Fact<double> ComfortableMaxC { get; init; }

    /// <summary>Cells per second, with the current Moving capacity.</summary>
    public Fact<double> MoveSpeed { get; init; }

    /// <summary>The temperature at the pawn's cell.</summary>
    public Fact<double> CellTemperatureC { get; init; }

    /// <summary>The Hypothermia hediff severity.</summary>
    public Fact<double> Hypothermia { get; init; }
}
