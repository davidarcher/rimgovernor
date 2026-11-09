namespace RimGovernor.Brain.Policy.Methods.Generation;

/// <summary>The game's SimpleCurve, for every stage (the live Method stages may call only Generation helpers, RGARCH010).</summary>
internal static class Curves
{
    /// <summary>SimpleCurve.Evaluate: linear between points, the end values outside them; 0 for no points.</summary>
    internal static double Evaluate(IReadOnlyList<CurvePoint> points, double x)
    {
        if (points.Count == 0)
        {
            return 0;
        }
        if (x <= points[0].X)
        {
            return points[0].Y;
        }
        if (x >= points[^1].X)
        {
            return points[^1].Y;
        }
        for (var i = 1; i < points.Count; i++)
        {
            if (x <= points[i].X)
            {
                CurvePoint a = points[i - 1], b = points[i];
                return a.Y + ((b.Y - a.Y) * (x - a.X) / (b.X - a.X));
            }
        }
        return 0;
    }
}
