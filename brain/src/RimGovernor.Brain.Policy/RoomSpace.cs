namespace RimGovernor.Brain.Policy;

/// <summary>
/// The game's Space and Impressiveness room stats as the planner prices a planned room's size by them (#3748).
/// </summary>
/// <remarks>
/// What the game says (RimWorld 1.7, ilspycmd):
///   - RoomStatWorker_Space.GetScore: 350 for a psychologically outdoors room, else 1.4 per standable cell plus 0.5 per
///     cell walkable but not standable, at most 350. An empty rectangular room's cells are all standable.
///   - RoomStatWorker_Impressiveness.GetScore: with GetFactor(x) = x while |x| &lt; 1, else 1 + ln x above and -1 - ln -x
///     below, the factors of Wealth / 1500, Beauty / 3, Space / 125 and 1 + min(Cleanliness, 0) / 2.5 give
///     n = Lerp(mean, min, 0.35); above five times the space factor n is pulled Lerp(n, 5 x space, 0.75); the score is
///     100 n.
/// A planned room is priced at the bare-room baseline (#3748): Beauty, Cleanliness and Wealth zero, as no floor or
/// furniture is predicted here, so its Space alone moves its score.
/// </remarks>
public static class RoomSpaceRules
{
    // RoomStatWorker_Space: a standable cell's space and the cap.
    private const double StandableSpace = 1.4;
    private const double MaxSpace = 350;

    // RoomStatWorker_Impressiveness: each stat's normaliser, the min's weight and the space ceiling's factor and pull.
    internal const double WealthScale = 1500;
    internal const double BeautyScale = 3;
    internal const double SpaceScale = 125;
    internal const double CleanlinessScale = 2.5;
    private const double MinWeight = 0.35;
    private const double SpaceCeiling = 5;
    private const double SpaceCeilingPull = 0.75;

    /// <summary>The Space score of an empty rectangular room of <paramref name="interiorCells"/> cells (RoomStatWorker_Space).</summary>
    public static double Space(int interiorCells) => Math.Min(StandableSpace * Math.Max(interiorCells, 0), MaxSpace);

    /// <summary>The Impressiveness score of a room with these stats (RoomStatWorker_Impressiveness).</summary>
    public static double Impressiveness(double space, double beauty, double cleanliness, double wealth)
    {
        var w = Factor(wealth / WealthScale);
        var b = Factor(beauty / BeautyScale);
        var s = Factor(space / SpaceScale);
        var c = Factor(1 + (Math.Min(cleanliness, 0) / CleanlinessScale));
        var mean = (w + b + s + c) / 4;
        var min = Math.Min(w, Math.Min(b, Math.Min(s, c)));
        var n = Lerp(mean, min, MinWeight);
        var ceiling = s * SpaceCeiling;
        if (n > ceiling)
        {
            n = Lerp(n, ceiling, SpaceCeilingPull);
        }
        return n * 100;
    }

    /// <summary>The Impressiveness of an empty rectangular room of <paramref name="interiorCells"/> cells at the bare-room baseline (Beauty, Cleanliness and Wealth zero).</summary>
    public static double BareImpressiveness(int interiorCells) => Impressiveness(Space(interiorCells), 0, 0, 0);

    private static double Factor(double x) => Math.Abs(x) < 1 ? x : x > 0 ? 1 + Math.Log(x) : -1 - Math.Log(-x);

    private static double Lerp(double a, double b, double t) => a + ((b - a) * t);
}
