namespace RimGovernor.Brain.Policy;

// Mood currency (#2954). The planner trades mood against work and watts, so a mood point needs a price. The
// user's decision (epic #2855): a mood point is priced as break risk. One point is the change in a colonist's
// expected break cost a day when their expected mood moves by one point, and a break costs the work the
// colonist loses in it, in the work-days of the #2945 rule (a colonist's work-day is worth their food upkeep,
// turned into watts at WorkDayW).
//
// What the game says (ilspycmd, Verse.AI.MentalBreaker, MentalState, MentalBreakWorker, RimWorld.Need_Mood):
//   - Need_Mood.CurInstantLevel = base 0.5 + the thoughts' offsets / 100, so one mood point is 0.01 of the level.
//   - The thresholds are the MentalBreakThreshold stat T (default 0.35): minor below T, major below T x
//     0.5714286, extreme below T / 7.
//   - A pawn below a threshold for 2000 ticks may break; every 150 ticks the highest tier it is in fires with an
//     MTB of 4 (minor), 0.8 (major) or 0.5 (extreme) days. After a break it recovers and may not break again for
//     15000 awake ticks.
//   - The break is drawn among the tier's MentalBreakDefs by baseCommonality, and lasts its MentalStateDef's
//     minTicksBeforeRecovery, then ends with recoveryMtbDays or at maxTicksBeforeRecovery.
//
// So with the break a renewal cycle of MTB + cooldown + duration days, a tier costs duration/cycle work-days a
// day: the share of time the colonist is in a break. The mood price is the difference of that cost between the
// mood with and without the effect, a step function that is zero while the loss stays inside a tier and jumps
// where it crosses a threshold.
//
// What it leaves out, so the price is a lower bound on the harm: the damage and risk of aggro breaks, the
// breaks that remove the pawn (RunWild, GiveUpExit), the per-colony-size commonality curves, the sleeping hours
// in which the MTB roll fires but cannot start a break, and trait changes to the threshold.

/// <summary>A break intensity, ordered mildest first.</summary>
public enum MoodTier
{
    Minor,
    Major,
    Extreme,
}

/// <summary>
/// The game's mood-break numbers that its defs do not carry: the literals of MentalBreaker and MentalState, plus
/// the defs they name. Fractions are each tier's share of the MentalBreakThreshold stat, MTBDays the tier's break
/// MTB, CooldownDays the ticks without a break after a recovery, PointsPerLevel Need_Mood's divisor. Unpriced are
/// the breaks the game gives no duration for. HediffBreaks are the breaks that are a hediff (Catatonic) and the
/// hediff whose disappear time is the break's length. Registered in DefTables.
/// </summary>
public sealed record MoodGameTable
{
    public string ThresholdStat { get; init; } = string.Empty;

    public IReadOnlyList<double> Fractions { get; init; } = [];

    public IReadOnlyList<double> MTBDays { get; init; } = [];

    public double CooldownDays { get; init; }

    public double PointsPerLevel { get; init; }

    public IReadOnlyList<string> Unpriced { get; init; } = [];

    public IReadOnlyDictionary<string, string> HediffBreaks { get; init; } = new Dictionary<string, string>();

    public string DarkThought { get; init; } = string.Empty;

    public IReadOnlyDictionary<RoomRole, string> RoleThoughts { get; init; } = new Dictionary<RoomRole, string>();

    /// <summary>The impressiveness stage a room not yet furnished is priced at (the planner's judgment, #2878).</summary>
    public int PlannedStage { get; init; }
}

public static partial class PolicyConstants
{
    internal const int MoodTierCount = 3;

    private static readonly MoodGameTable MoodGameValue = new()
    {
        ThresholdStat = "MentalBreakThreshold",
        Fractions = [1, 0.5714286, 1.0 / 7],
        MTBDays = [4, 0.8, 0.5],
        CooldownDays = 15000.0 / GameTime.TicksPerDay,
        PointsPerLevel = 100,
        Unpriced = ["RunWild", "GiveUpExit"],
        HediffBreaks = new Dictionary<string, string> { ["Catatonic"] = "CatatonicBreakdown" },
        DarkThought = "EnvironmentDark",
        // The thought each role's mood effect is: the stage is the room's impressiveness stage. Throne and
        // worship carry no mood offset.
        RoleThoughts = new Dictionary<RoomRole, string>
        {
            [RoomRole.Bedroom] = "SleptInBedroom",
            [RoomRole.Barracks] = "SleptInBarracks",
            [RoomRole.Hospital] = "HospitalPatientRoomStats",
            [RoomRole.PrisonCell] = "PrisonCell",
            [RoomRole.PrisonBarracks] = "PrisonBarracks",
        },
    };

    internal static MoodGameTable MoodJudgment => MoodGameValue;
}

/// <summary>One tier's break mix: the sum of its priced breaks' commonalities and their weighted mean duration in days. Weight 0 is a tier with no priced break.</summary>
public readonly record struct MoodTierModel(double Weight, double Days)
{
    /// <summary>Folds one break into the tier's mix (the bridge builds the model).</summary>
    public MoodTierModel Add(double weight, double days)
    {
        if (!PolicyFunctions.FinitePositive(weight) || !PolicyFunctions.FinitePositive(days))
        {
            return this;
        }
        var total = Weight + weight;
        return new MoodTierModel(total, ((Days * Weight) + (days * weight)) / total);
    }
}

/// <summary>The break cost curve of a colonist: the break threshold and each tier's mix. The default value is unknown.</summary>
public sealed record MoodBreakModel
{
    public double Threshold { get; init; }

    /// <summary>The tier mixes, mildest first; empty is unknown.</summary>
    public IReadOnlyList<MoodTierModel> Tiers { get; init; } = [];

    /// <summary>Reports a model with a threshold and a priced break in every tier.</summary>
    public bool Valid()
    {
        if (!PolicyFunctions.FinitePositive(Threshold) || Tiers.Count != PolicyConstants.MoodTierCount)
        {
            return false;
        }
        return Tiers.All(t => PolicyFunctions.FinitePositive(t.Weight) && PolicyFunctions.FinitePositive(t.Days));
    }

    /// <summary>The tier a mood level is in; false above the minor threshold.</summary>
    internal (MoodTier Tier, bool Ok) TierAt(double level)
    {
        for (var tier = MoodTier.Extreme; tier >= MoodTier.Minor; tier--)
        {
            if (level < Threshold * PolicyConstants.MoodJudgment.Fractions[(int)tier])
            {
                return (tier, true);
            }
        }
        return (MoodTier.Minor, false);
    }

    /// <summary>The work-days a colonist at the mood level loses a day to breaks: the share of a break cycle spent in the break.</summary>
    public double CostPerDay(double level)
    {
        var (tier, ok) = TierAt(level);
        if (!ok)
        {
            return 0;
        }
        var days = Tiers[(int)tier].Days;
        return days / (PolicyConstants.MoodJudgment.MTBDays[(int)tier] + PolicyConstants.MoodJudgment.CooldownDays + days);
    }
}

/// <summary>One stage of a ThoughtDef; a null stage (the game keeps unused ones null) is not Present and carries no mood.</summary>
public readonly record struct MoodStage(double Effect, bool Present);

/// <summary>Everything the mood price reads from the catalog: the break curve and the stages of the thoughts the planner prices.</summary>
public sealed record MoodModel
{
    public MoodBreakModel Break { get; init; } = new();

    public IReadOnlyDictionary<string, IReadOnlyList<MoodStage>> Stages { get; init; } = new Dictionary<string, IReadOnlyList<MoodStage>>();

    /// <summary>The mood a thought gives at a stage index, clamped to the stages it has; a null stage gives none. False when the thought is unknown.</summary>
    public (double Effect, bool Ok) StageEffect(string thought, int stage)
    {
        if (!Stages.TryGetValue(thought, out var stages) || stages.Count == 0)
        {
            return (0, false);
        }
        var s = stages[Math.Min(Math.Max(stage, 0), stages.Count - 1)];
        return s.Present ? (s.Effect, true) : (0, true);
    }

    /// <summary>The mood points a colonist loses to darkness (EnvironmentDark's stage), 0 and false when unknown. Traits that nullify it (NightOwl, Undergrounder) are the caller's.</summary>
    public (double Points, bool Ok) DarkPoints()
    {
        var (effect, ok) = StageEffect(PolicyConstants.MoodJudgment.DarkThought, 0);
        return (-effect, ok);
    }

    /// <summary>
    /// The mood points a lost role effect costs a pawn in a room of the given impressiveness stage: what the
    /// role it was meant to have gives, less what the role it got gives. Throne and worship carry no mood. False
    /// when a role's thought is unknown.
    /// </summary>
    public (double Points, bool Ok) LostPoints(LostRoleEffect lost, int stage)
    {
        switch (lost.Effect)
        {
            case RoleEffect.Throne:
            case RoleEffect.Worship:
                return (0, true);
            case RoleEffect.SleepMood:
            case RoleEffect.PatientMood:
            case RoleEffect.PrisonMood:
                break;
            default:
                return (0, false);
        }
        var (had, ok) = RoleEffectAt(lost.Role, stage);
        if (!ok)
        {
            return (0, false);
        }
        var (got, ok2) = RoleEffectAt(lost.Got, stage);
        if (!ok2)
        {
            return (0, false);
        }
        return (had - got, true);
    }

    /// <summary>A role's mood at a stage; a role with no mood thought gives 0.</summary>
    private (double Effect, bool Ok) RoleEffectAt(RoomRole role, int stage) =>
        PolicyConstants.MoodJudgment.RoleThoughts.TryGetValue(role, out var thought) ? StageEffect(thought, stage) : (0, true);
}

/// <summary>A way to buy mood instead of paying for its loss, priced in sustained watts for the whole group of pawns it serves (a bedroom impressiveness upgrade is one).</summary>
public readonly record struct MoodSource(double Points, double W);

/// <summary>
/// The mood price's input. Points is the mood a change takes from each of Pawns colonists (negative when it
/// gives mood) and ExpectedMood their mood level with the effect, 0 to 1. WorkDayW is a colonist's work-day in
/// watts (LightPrice.WorkDayW); Sources are the other ways to buy the same mood.
/// </summary>
public sealed record MoodPriceInput
{
    public MoodBreakModel Model { get; init; } = new();

    public double WorkDayW { get; init; }

    public double Points { get; init; }

    public int Pawns { get; init; }

    public double ExpectedMood { get; init; }

    public IReadOnlyList<MoodSource> Sources { get; init; } = [];
}

/// <summary>A mood change in work-days a day and sustained watts. Capped is set when a source of the same mood is cheaper than the break risk.</summary>
public sealed record MoodPrice
{
    public double WorkDays { get; set; }

    public double W { get; set; }

    public bool Capped { get; set; }
}

public static partial class PolicyFunctions
{
    /// <summary>The table the bridge reads the break model with.</summary>
    public static MoodGameTable MoodGame() => PolicyConstants.MoodJudgment;

    /// <summary>
    /// The mean days a mental state lasts: the game ends it at minTicks plus an exponential wait of mean mtbDays
    /// (rolled every 30 ticks) and forces the end at maxTicks, so the mean of that truncated wait.
    /// </summary>
    public static double StateDays(double minTicks, double maxTicks, double mtbDays)
    {
        var min = minTicks / GameTime.TicksPerDay;
        if (mtbDays <= 0)
        {
            return min;
        }
        var span = Math.Max(maxTicks - minTicks, 0) / GameTime.TicksPerDay;
        return min + (mtbDays * (1 - Math.Exp(-span / mtbDays)));
    }

    /// <summary>
    /// Prices a change of mood points as break risk. It is false for an unknown model or work-day or a mood
    /// outside 0 to 1. The price is never more than the cheapest source of the same mood: a loss it is cheaper to
    /// buy back is priced at the buying.
    /// </summary>
    public static (MoodPrice Price, bool Ok) PriceMood(MoodPriceInput input)
    {
        var level = input.ExpectedMood;
        if (!input.Model.Valid() || !FinitePositive(input.WorkDayW) || input.Pawns < 0 || double.IsNaN(input.Points) || double.IsInfinity(input.Points)
            || double.IsNaN(level) || level < 0 || level > 1)
        {
            return (new MoodPrice(), false);
        }
        var after = Math.Min(Math.Max(level - (input.Points / PolicyConstants.MoodJudgment.PointsPerLevel), 0), 1);
        var days = input.Pawns * (input.Model.CostPerDay(after) - input.Model.CostPerDay(level));
        var price = new MoodPrice { WorkDays = days, W = days * input.WorkDayW };
        if (input.Points > 0)
        {
            var cheapest = double.PositiveInfinity;
            foreach (var s in input.Sources)
            {
                if (s.Points >= input.Points && FinitePositive(s.W))
                {
                    cheapest = Math.Min(cheapest, s.W);
                }
            }
            if (price.W > cheapest)
            {
                price.W = cheapest;
                price.WorkDays = cheapest / input.WorkDayW;
                price.Capped = true;
            }
        }
        return (price, true);
    }

    /// <summary>Reports a break the game gives no duration for.</summary>
    public static bool UnpricedBreak(string def) => PolicyConstants.MoodJudgment.Unpriced.Contains(def);

    internal static List<string> MoodThoughtDefs()
    {
        var output = new List<string> { PolicyConstants.MoodJudgment.DarkThought };
        output.AddRange(PolicyConstants.MoodJudgment.RoleThoughts.Values);
        output.Sort(StringComparer.Ordinal);
        return output;
    }

    internal static List<string> MoodBreakHediffs()
    {
        var output = PolicyConstants.MoodJudgment.HediffBreaks.Values.ToList();
        output.Sort(StringComparer.Ordinal);
        return output;
    }
}
