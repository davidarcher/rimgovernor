using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Simulation.Combat;

/// <summary>The numeric helpers and constants of the shot rules.</summary>
internal static class ShotMath
{
    /// <summary>The floor ShotReport puts under the aim chance.</summary>
    public const double MinAim = 0.0201;

    /// <summary>The bound of Gen.RandomHorizontalVector(0.3f) that Projectile.Launch adds to the destination cell centre.</summary>
    public const double LaunchScatter = 0.3;

    /// <summary>
    /// The TickInterval cadence of a thing the camera does not see: min(max(GenTicks.GetCameraUpdateRate,
    /// MinTickIntervalRate), MaxTickIntervalRate) = 15. A lab run is off camera.
    /// </summary>
    public const int OffCameraInterval = 15;

    /// <summary>
    /// The y of a pawn's DrawPos, Altitudes.AltitudeFor(AltitudeLayer.Pawn): layer 23 times the 0.36585367
    /// layer spacing. The projectile launches from DrawPos and lands on y 0, so the 3D distance
    /// StartingTicksToImpact divides by speed is longer than the map distance.
    /// </summary>
    public const double PawnAltitude = 8.41463441; // 23 * 0.36585367, exact as Go's constant arithmetic rounds it

    public static double Lerp(double a, double b, double t) => a + ((b - a) * Math.Min(Math.Max(t, 0), 1));

    /// <summary>Go <c>math.Hypot</c> for finite arguments (the same formula, so the two agree).</summary>
    public static double Hypot(double p, double q)
    {
        p = Math.Abs(p);
        q = Math.Abs(q);
        if (p < q)
        {
            (p, q) = (q, p);
        }
        if (p == 0)
        {
            return 0;
        }
        q /= p;
        return p * Math.Sqrt(1 + (q * q));
    }

    /// <summary>Ports the four-band distance interpolation shared by HitFactorFromShooter and VerbProperties.GetHitChanceFactor.</summary>
    public static double Bands(double d, double touch, double @short, double medium, double @long)
    {
        if (d <= 3)
        {
            return touch;
        }
        if (d <= 12)
        {
            return Lerp(touch, @short, (d - 3) / 9);
        }
        if (d <= 25)
        {
            return Lerp(@short, medium, (d - 12) / 13);
        }
        if (d <= 40)
        {
            return Lerp(medium, @long, (d - 25) / 15);
        }
        return @long;
    }

    /// <summary>
    /// Evaluates ShootTuning.MissDistanceFromAimOnChanceCurves: the columns at aim-on chances 0.02, 0.04, 0.07,
    /// 0.11, 0.22 and 1 map a uniform draw u to a distance from 1 up to 10, 8, 6, 4, 2 and 1 cells, blended
    /// linearly between neighbouring columns.
    /// </summary>
    public static double MissDistance(double aim, double u)
    {
        double[] xs = [0.02, 0.04, 0.07, 0.11, 0.22, 1];
        double[] far = [10, 8, 6, 4, 2, 1];
        double Col(int i) => 1 + ((far[i] - 1) * Math.Min(Math.Max(u, 0), 1));
        if (aim <= xs[0])
        {
            return Col(0);
        }
        for (var i = 1; i < xs.Length; i++)
        {
            if (aim <= xs[i])
            {
                return Lerp(Col(i - 1), Col(i), (aim - xs[i - 1]) / (xs[i] - xs[i - 1]));
            }
        }
        return Col(xs.Length - 1);
    }

    /// <summary>Ports VerbUtility.InterceptChanceFactorFromDistance: no chance within 5 cells of the origin, full from 12.</summary>
    public static double InterceptFactor(double ox, double oz, Cell c)
    {
        var dx = c.X + 0.5 - ox;
        var dz = c.Z + 0.5 - oz;
        var d2 = (dx * dx) + (dz * dz);
        if (d2 <= 25)
        {
            return 0;
        }
        if (d2 >= 144)
        {
            return 1;
        }
        return (d2 - 25) / (144 - 25);
    }
}
