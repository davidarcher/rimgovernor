namespace RimGovernor.Brain.Simulation.Combat;

// The ShootingAccuracyPawn stat (Core Stats_Pawns_Combat.xml) is computed by
// RimWorld.StatWorker.GetValueUnfinalized and FinalizeValue: a raw value of the skill term, the pawn's trait
// and gear offsets and the capacity offsets (Sight scale 12 max 2, Manipulation scale 8 max 1,
// PawnCapacityOffset.GetOffset = (min(level, max) - 1) x scale), then the stat's postProcessCurve. The native
// read gives the finished value of a healthy pawn; an injured pawn's Manipulation (limb damage, pain) and
// Sight (eye damage) fall and take the raw value down with them, so its accuracy per cell drops (A43). The
// importer's pawns are healthy at tick 0 (A28), so the capacity offsets are 0 there and the read value is the
// curve's output for the raw value the curve is inverted to.
internal static class Accuracy
{
    /// <summary>The stat's postProcessCurve as (raw, accuracy) points.</summary>
    private static readonly (double Raw, double Acc)[] Curve =
    [
        (-20, 0.70), (-10, 0.80), (-6, 0.83), (-4, 0.85), (-2, 0.87), (0, 0.89),
        (2, 0.93), (4, 0.94), (6, 0.95), (8, 0.96), (10, 0.97), (12, 0.975),
        (14, 0.98), (16, 0.98333), (18, 0.98666), (20, 0.99), (22, 0.9925),
        (26, 0.995), (30, 0.9965), (40, 0.998), (60, 0.999),
    ];

    /// <summary>Evaluates the postProcessCurve (SimpleCurve.Evaluate clamps outside the first and last point).</summary>
    public static double FromRaw(double raw)
    {
        if (raw <= Curve[0].Raw)
        {
            return Curve[0].Acc;
        }
        for (var i = 1; i < Curve.Length; i++)
        {
            if (raw <= Curve[i].Raw)
            {
                return ShotMath.Lerp(Curve[i - 1].Acc, Curve[i].Acc, (raw - Curve[i - 1].Raw) / (Curve[i].Raw - Curve[i - 1].Raw));
            }
        }
        return Curve[^1].Acc;
    }

    /// <summary>Inverts the curve, which rises everywhere. A value outside the curve's range has no single raw value; it maps to the nearest end.</summary>
    public static double RawFrom(double acc)
    {
        if (acc <= Curve[0].Acc)
        {
            return Curve[0].Raw;
        }
        for (var i = 1; i < Curve.Length; i++)
        {
            if (acc <= Curve[i].Acc)
            {
                return Curve[i - 1].Raw + ((acc - Curve[i - 1].Acc) / (Curve[i].Acc - Curve[i - 1].Acc) * (Curve[i].Raw - Curve[i - 1].Raw));
            }
        }
        return Curve[^1].Raw;
    }

    /// <summary>PawnCapacityOffset.GetOffset.</summary>
    public static double CapacityOffset(double level, double max, double scale) => (Math.Min(level, max) - 1) * scale;
}
