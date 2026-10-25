namespace RimGovernor.Brain.Policy;

/// <summary>One point of a game SimpleCurve.</summary>
public readonly record struct CurvePoint(double X, double Y);

/// <summary>
/// What a butchering costs a room in game units, read from the defs: the filth a butchered corpse spawns
/// (Corpse.ButcherProducts calls FilthMaker.TryMakeFilth once with the race's BloodDef), that filth's
/// Cleanliness stat per unit of thickness, and the FoodPoisonChance room stat's curve over the room's
/// cleanliness. The zero value prices nothing.
/// </summary>
public sealed record FilthModel
{
    public string Def { get; init; } = string.Empty;

    public double Cleanliness { get; init; }

    /// <summary>The curve of RoomStatDefOf.FoodPoisonChance (a RoomStatWorker_FromStatByCurve over Cleanliness), points by rising X.</summary>
    public IReadOnlyList<CurvePoint> FoodPoison { get; init; } = [];

    /// <summary>RoomStatDefOf.FoodPoisonChance's roomlessScore: the chance a meal made outside a proper room (Room.GetStat without stats, at
    /// the map edge or over 60 regions) is poisoned by the room (CompFoodPoisonable.Notify_RecipeProduced, #3802).</summary>
    public double FoodPoisonRoomless { get; init; }

    /// <summary>
    /// RoomStatDefOf.InfectionChanceFactor's curve over Cleanliness and roomless score (#3804): the factor HediffComp_Infecter stores for
    /// a wound tended in the room.
    /// </summary>
    public IReadOnlyList<CurvePoint> InfectionChance { get; init; } = [];

    public double InfectionChanceRoomless { get; init; }

    /// <summary>
    /// The SurgerySuccessChanceCleanlinessFactor room stat's curve and roomless score (#3804): the room factor a bed's
    /// SurgerySuccessChanceFactor multiplies (its StatPart_RoomStat).
    /// </summary>
    public IReadOnlyList<CurvePoint> SurgeryCleanliness { get; init; } = [];

    public double SurgeryCleanlinessRoomless { get; init; }

    /// <summary>WorkGiver_CleanFilth.MinTicksSinceThickened, the ticks filth stands before anyone may clean it (#2988). A zero leaves that part unpriced.</summary>
    public double StandTicks { get; init; }

}

/// <summary>The native filth thickness produced by one butchered corpse.</summary>
public sealed record FilthGame
{
    public double UnitsPerRun { get; init; }
}

/// <summary>A node's butchering: the runs a day and the work ticks of one.</summary>
public readonly record struct FilthRuns
{
    public double Runs { get; init; }

    public double Ticks { get; init; }

    public FilthRuns Plus(FilthRuns o) => new() { Runs = Runs + o.Runs, Ticks = o.Ticks };
}

public static partial class LayoutFilthRules
{
    private static readonly FilthGame FilthGameValue = new() { UnitsPerRun = 1 };

    internal static FilthGame FilthGameJudgment => FilthGameValue;

    /// <summary>SimpleCurve.Evaluate: linear between points, the end values outside them.</summary>
    internal static double EvaluateCurve(IReadOnlyList<CurvePoint> points, double x)
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

    /// <summary>
    /// The units of a node's filth that stand at once and the share of the day some stands, for runs (a day's
    /// runs, one batch, runTicks apart) and the stand ticks no one may clean before. A unit made stands for
    /// stand ticks, so the units alive at once are those within one stand of each other, at most the batch; the
    /// filth stands from the first run's start to the last unit's end, (runs - 1) x runTicks + stand ticks, once
    /// a day (a day with less than a run is a run that many days a share of it).
    /// </summary>
    internal static (double Units, double Exposure) Residual(double runs, double runTicks, double stand)
    {
        if (runs <= 0 || stand <= 0)
        {
            return (0, 0);
        }
        var batch = Math.Max(1, runs);
        var units = Math.Ceiling(batch);
        if (runTicks > 0)
        {
            units = Math.Min(units, Math.Ceiling(stand / runTicks));
        }
        var batches = Math.Min(1, runs);
        var exposure = Math.Min(1, batches * (((batch - 1) * runTicks) + stand) / GameTime.TicksPerDay);
        return (units, exposure);
    }

    /// <summary>
    /// The filth of a room's filthy nodes standing at once: each node's <see cref="Residual"/> units (its runs x
    /// FilthGame.UnitsPerRun) summed, and the longest share of the day any of it stands.
    /// </summary>
    internal static (double Units, double Exposure) Standing(IEnumerable<FilthRuns> runs, double stand)
    {
        double units = 0, exposure = 0;
        foreach (var r in runs)
        {
            var (standing, share) = Residual(r.Runs * FilthGameJudgment.UnitsPerRun, r.Ticks, stand);
            units += standing;
            exposure = Math.Max(exposure, share);
        }
        return (units, exposure);
    }
}
