namespace RimGovernor.Brain.Policy;

public static partial class PolicyConstants
{
    /// <summary>The divisor RoomStatWorker_Impressiveness puts under the room's cleanliness (1 + min(cleanliness, 0) / 2.5) before it takes the cleanliness factor of the stat.</summary>
    internal const double ImpressivenessCleanlinessScale = 2.5;

    private static readonly FilthGame FilthGameValue = new() { UnitsPerRun = 1, SpeedStat = "CleaningSpeed" };

    private static readonly FoodPoisonTable FoodPoisonGameValue = new() { Hediff = "FoodPoisoning", WorkCapacity = "Consciousness", VomitJob = "Vomit", VomitTicks = 600 };

    internal static FilthGame FilthGameJudgment => FilthGameValue;

    internal static FoodPoisonTable FoodPoisonJudgment => FoodPoisonGameValue;
}

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

    /// <summary>What one poisoned meal costs the eater (the FoodPoisoning hediff); the zero value prices no poisoning.</summary>
    public PoisoningModel Poisoning { get; init; } = new();

    /// <summary>
    /// The filth def's cleaningWorkToReduceThickness, the work (JobDriver_CleanFilth's cleaning-speed ticks)
    /// one unit of thickness takes to clean.
    /// </summary>
    public double CleaningWork { get; init; }

    /// <summary>The CleaningSpeed stat of a colonist with no modifier (its defaultBaseValue, the speed the work is divided by).</summary>
    public double CleaningSpeed { get; init; }

    /// <summary>WorkGiver_CleanFilth.MinTicksSinceThickened, the ticks filth stands before anyone may clean it (#2988). A zero leaves that part unpriced.</summary>
    public double StandTicks { get; init; }

    /// <summary>
    /// Reports a model that prices the poisoning; a merge into a room is not proposed while it is unknown, as
    /// it would look free. The cleaning labor is not a condition: a room pays it merged or apart
    /// (CleaningWorkDays), so it cancels from the decision.
    /// </summary>
    public bool Priced() => Def != string.Empty && Poisoning.WorkDays() > 0 && PolicyFunctions.FinitePositive(StandTicks);

    /// <summary>
    /// The work-days a day of cleaning the filth of the nodes' runs takes: the units a day (runs x
    /// FilthGame.UnitsPerRun) times the def's CleaningWork over the colonist's CleaningSpeed. Zero while the def
    /// work or the speed is unknown.
    /// </summary>
    public double CleaningWorkDays(IReadOnlyDictionary<string, FilthRuns> runs)
    {
        if (!PolicyFunctions.FinitePositive(CleaningWork) || !PolicyFunctions.FinitePositive(CleaningSpeed))
        {
            return 0;
        }
        var days = 0.0;
        foreach (var r in runs.Values)
        {
            days += r.Runs * PolicyConstants.FilthGameJudgment.UnitsPerRun * CleaningWork / CleaningSpeed / GameTime.TicksPerDay;
        }
        return days;
    }

    /// <summary>
    /// Prices each filthy node of a room of cells cells that also holds stations the filth is not for; exposed
    /// are the nodes that read cleanliness, meals the meals a day they make and runs each filthy node's
    /// butchering. A model that prices nothing owes nothing.
    /// </summary>
    internal IReadOnlyList<FilthCost> FilthCosts(IReadOnlyList<string> filthy, IReadOnlyList<string> exposed, int cells, double meals, IReadOnlyDictionary<string, FilthRuns> runs)
    {
        if (Def == string.Empty || cells <= 0 || filthy.Count == 0)
        {
            return [];
        }
        var per = Cleanliness / cells;
        var onset = 0.0;
        if (FoodPoison.Count > 0 && FoodPoison[^1].Y == 0)
        {
            onset = FoodPoison[^1].X;
        }
        var units = 0.0;
        if (per < 0 && onset < 0)
        {
            units = onset / per;
        }
        var chance = PolicyFunctions.EvaluateCurve(FoodPoison, per);
        var output = new List<FilthCost>();
        foreach (var node in filthy)
        {
            var r = runs.GetValueOrDefault(node);
            var (standing, exposure) = PolicyFunctions.Residual(r.Runs * PolicyConstants.FilthGameJudgment.UnitsPerRun, r.Ticks, StandTicks);
            var standingChance = PolicyFunctions.EvaluateCurve(FoodPoison, per * standing);
            var poisonings = meals * exposure * standingChance;
            output.Add(new FilthCost
            {
                Node = node,
                Filth = Def,
                Cells = cells,
                Exposed = [.. exposed],
                CleanlinessPerUnit = per,
                FoodPoisonPerUnit = chance,
                PoisonOnsetCleanliness = onset,
                UnitsToPoison = units,
                ImpressivenessInputPerUnit = per / PolicyConstants.ImpressivenessCleanlinessScale,
                RunsPerDay = r.Runs,
                RunTicks = r.Ticks,
                StandingUnits = standing,
                Exposure = exposure,
                StandingPoison = standingChance,
                MealsPerDay = meals,
                PoisoningsPerDay = poisonings,
                PoisonWorkDays = poisonings * Poisoning.WorkDays(),
            });
        }
        return output;
    }
}

/// <summary>
/// The judgment behind the cleaning price (#2988), registered in DefTables. UnitsPerRun is the filth units a
/// butchery run leaves: Corpse.ButcherProducts calls FilthMaker.TryMakeFilth once with the default count of one
/// (read with ilspycmd), so a unit of thickness per corpse. SpeedStat is the stat a pawn's cleaning speed is
/// (JobDriver_CleanFilth divides the work by it). The floor's Cleanliness (RoomStatWorker_Cleanliness adds each
/// cell's terrain stat) and CleaningTimeFactor, and the dirt pawns track in (Pawn_FilthTracker.
/// Notify_EnteredNewCell: FilthRate times 0.005 a cell entered), are not priced: the floor is laid after the
/// layout and the cells entered are the embedding's traffic, neither known in the station chain. No ThoughtDef
/// reads room cleanliness (only the Impressiveness stat does, recorded as ImpressivenessInputPerUnit), so the
/// cleanliness mood is record-only.
/// </summary>
public sealed record FilthGame
{
    public double UnitsPerRun { get; init; }

    public string SpeedStat { get; init; } = string.Empty;
}

/// <summary>A node's butchering: the runs a day and the work ticks of one.</summary>
public readonly record struct FilthRuns
{
    public double Runs { get; init; }

    public double Ticks { get; init; }

    public FilthRuns Plus(FilthRuns o) => new() { Runs = Runs + o.Runs, Ticks = o.Ticks };
}

/// <summary>
/// The judgment behind the poisoning price (#2955), registered in DefTables. Hediff is the sickness a poisoned
/// meal gives (FoodUtility.AddFoodPoisoningHediff). WorkCapacity is the capacity of its stages that stands for
/// the sick colonist's work: Consciousness, the lowest of the capacities the hediff cuts that a worker uses
/// (Manipulation 0.9 / 0.8 / 0.9 and Moving 0.8 / 0.5 / 0.8 are higher or equal in every stage), and the one
/// the game scales all awake action by; the price is the work lost at that factor. VomitJob is the job a vomit
/// starts (JobDriver_Vomit, read with ilspycmd) and VomitTicks its mean length, 300 to 900 ticks drawn
/// uniformly, during which the colonist does no work.
/// </summary>
public sealed record FoodPoisonTable
{
    public string Hediff { get; init; } = string.Empty;

    public string WorkCapacity { get; init; } = string.Empty;

    public string VomitJob { get; init; } = string.Empty;

    public double VomitTicks { get; init; }
}

/// <summary>One stage of the poisoning hediff from MinSeverity up: WorkFactor the stage's factor on the work capacity (1 when it cuts none) and VomitMtbDays the mean days between vomits (0 for none).</summary>
public readonly record struct PoisonStage(double MinSeverity, double WorkFactor, double VomitMtbDays);

/// <summary>The FoodPoisoning hediff: it starts at severity Initial and loses PerDay of it a day (HediffCompProperties_SeverityPerDay), passing through Stages from the top one down to zero.</summary>
public sealed record PoisoningModel
{
    public double Initial { get; init; }

    public double PerDay { get; init; }

    public IReadOnlyList<PoisonStage> Stages { get; init; } = [];

    /// <summary>
    /// The work one poisoning costs, in work-days: for each stage the days spent in it times the work capacity
    /// it takes away, plus the days of vomiting (the stage's days over its vomit interval, each VomitTicks
    /// long). Worked: from severity 1 at 1 a day, the initial stage (0.8 up) lasts 0.2 days at x0.6, the major
    /// (0.2 up) 0.6 days at x0.5 and the recovering 0.2 days at x0.6, 0.08 + 0.30 + 0.08 = 0.46 work-days;
    /// vomits every 0.3, 0.2 and 0.4 days are 0.667 + 3 + 0.5 = 4.17 vomits of 600 ticks, 0.042 work-days;
    /// 0.50 in all. Zero for a model that is not known.
    /// </summary>
    public double WorkDays()
    {
        if (!PolicyFunctions.FinitePositive(Initial) || !PolicyFunctions.FinitePositive(PerDay) || Stages.Count == 0)
        {
            return 0;
        }
        var stages = Stages.SortedBy((a, b) => a.MinSeverity < b.MinSeverity);
        var total = 0.0;
        var top = Initial;
        for (var i = stages.Count - 1; i >= 0; i--)
        {
            var low = Math.Max(stages[i].MinSeverity, 0);
            if (i == 0)
            {
                low = 0;
            }
            if (low >= top)
            {
                continue;
            }
            var days = (top - low) / PerDay;
            total += days * (1 - stages[i].WorkFactor);
            if (stages[i].VomitMtbDays > 0)
            {
                total += days / stages[i].VomitMtbDays * PolicyConstants.FoodPoisonJudgment.VomitTicks / GameTime.TicksPerDay;
            }
            top = low;
        }
        return total;
    }
}

/// <summary>
/// The price of a filth node standing in a room that holds other stations, in the units the game reads it in.
/// Each filth unit adds its Cleanliness stat to the room's sum, which the room's cell count divides
/// (RoomStatWorker_Cleanliness), so one unit moves the room's cleanliness by CleanlinessPerUnit. Food poison
/// chance is the curve of the room's cleanliness, zero from PoisonOnsetCleanliness up, so FoodPoisonPerUnit is
/// the chance one standing unit alone gives and UnitsToPoison the units it takes to leave the zero.
/// ImpressivenessInputPerUnit is the change one unit makes to the cleanliness input of the Impressiveness stat
/// (1 + cleanliness / 2.5, while the room is not clean), which the stat's combination then weighs. Everything is
/// read from a room at cleanliness 0, the clamp of that input. Exposed are the nodes in the room whose effect
/// scales with cleanliness (a cook's meals read the food poison chance).
///
/// The price of keeping the node in this room is the poisoning residual in the #2945 work-day unit (#2988). The
/// cleaning labor of its filth is not part of it: the blood is cleaned wherever the butchery stands, so every
/// group that holds a filth node pays it (RoomGroup.Cleaning) and a merge changes none of it (#2993). The
/// residual is the poisoning while the filth stands: nobody may clean a unit before StandTicks, so the units of
/// the runs that fall in that window (StandingUnits, a day's runs being one batch of runs RunTicks apart) stand
/// at once, at cleanliness StandingUnits x CleanlinessPerUnit, and the cooks' meals made while they stand (the
/// Exposure share of the day) are poisoned at the curve's chance there (StandingPoison).
/// </summary>
public sealed record FilthCost
{
    public string Node { get; init; } = string.Empty;

    public string Filth { get; init; } = string.Empty;

    public int Cells { get; init; }

    public IReadOnlyList<string> Exposed { get; init; } = [];

    public double CleanlinessPerUnit { get; init; }

    public double FoodPoisonPerUnit { get; init; }

    public double PoisonOnsetCleanliness { get; init; }

    public double UnitsToPoison { get; init; }

    public double ImpressivenessInputPerUnit { get; init; }

    /// <summary>The node's butchering: runs a day.</summary>
    public double RunsPerDay { get; init; }

    /// <summary>The node's butchering: work ticks of one run.</summary>
    public double RunTicks { get; init; }

    /// <summary>The units standing at once.</summary>
    public double StandingUnits { get; init; }

    /// <summary>The share of the day any stands.</summary>
    public double Exposure { get; init; }

    /// <summary>The food poison chance at their cleanliness.</summary>
    public double StandingPoison { get; init; }

    /// <summary>
    /// The meals a day the exposed nodes' stations in the room make (the graph's demand), each poisoned with
    /// chance StandingPoison while the filth stands (CompFoodPoisonable.Notify_RecipeProduced) and each
    /// poisoned meal giving its eater one poisoning (PostIngested).
    /// </summary>
    public double MealsPerDay { get; init; }

    /// <summary>Their product with Exposure.</summary>
    public double PoisoningsPerDay { get; init; }

    /// <summary>The work-days a day they cost (PoisoningModel.WorkDays).</summary>
    public double PoisonWorkDays { get; init; }

    /// <summary>The work-days a day the node costs the room: the poisoning residual.</summary>
    public double WorkDays() => PoisonWorkDays;
}

public static partial class PolicyFunctions
{
    /// <summary>The table the bridge reads the cleaning model with.</summary>
    public static FilthGame FilthGameTable() => PolicyConstants.FilthGameJudgment;

    /// <summary>The table the bridge reads the poisoning model with.</summary>
    public static FoodPoisonTable FoodPoisonGame() => PolicyConstants.FoodPoisonJudgment;

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
}
