using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;

namespace RimGovernor.Brain.Policy;

// The tier-2 skill stock (#3634 of #3344): one per pawn and skill, keyed skill/<pawn>/<skill>, started from the observed SkillLearning.
// XP falls by the native decay of its level, coarsened to days; no tier-2 work pays it. Levels change at the native thresholds by the
// level rule ForecastSkill uses (ForecastSkillPolicy.Settle); each change is dated inside the interval. The marginal value of XP is
// the value of one level more (Raise) over the XP that level takes (ProjectMarginalValue).

/// <summary>A level a skill stock reached and the day, from now, it reached it.</summary>
public sealed record SkillLevelDate(double Day, int StoredLevel);

/// <summary>One skill stock over a run: where it started and each level change in order.</summary>
public sealed record SkillTrack
{
    /// <summary><see cref="ProjectGraph.SkillKey"/>.</summary>
    public string Key { get; init; } = "";

    public string Pawn { get; init; } = "";

    public string Skill { get; init; } = "";

    /// <summary>The observed stored level (<c>levelInt</c>) at time zero; the pawn's native stat curves are indexed by it (#3633).</summary>
    public int StoredLevel { get; init; }

    /// <summary>What <see cref="StoredLevel"/> takes to the next level.</summary>
    public required SkillNextLevel NextLevel { get; init; }

    public EquatableArray<SkillLevelDate> Dates { get; init; }

    /// <summary>The stored level on <paramref name="day"/>: the last change at or before it.</summary>
    public int StoredLevelOn(double day) => Dates.LastOrDefault(d => d.Day <= day)?.StoredLevel ?? StoredLevel;
}

/// <summary>What a stored level takes to the next (#3783): the native XP (<c>RequiredXp</c>) below the top level; nothing more
/// at the top, where XP raises no level.</summary>
public abstract record SkillNextLevel
{
    private protected SkillNextLevel() { }

    public sealed record Takes(double Xp) : SkillNextLevel;

    public sealed record Top : SkillNextLevel;
}

/// <summary>The mutable state of one skill stock in a run.</summary>
internal sealed class SkillStock
{
    private readonly SkillLearning mechanics;
    private readonly List<SkillLevelDate> dates = [];
    private readonly int startStored;
    private double xp;

    private SkillStock(string pawn, string skill, SkillLearning mechanics)
    {
        Pawn = pawn;
        Skill = skill;
        this.mechanics = mechanics;
        StoredLevel = startStored = mechanics.StoredLevel;
        xp = mechanics.Xp;
    }

    internal string Pawn { get; }

    internal string Skill { get; }

    internal string Key => ProjectGraph.SkillKey(Pawn, Skill);

    internal int StoredLevel { get; private set; }

    /// <summary>XP toward the next level.</summary>
    internal double Xp => xp;

    internal bool Disabled => mechanics.Disabled;

    /// <summary>
    /// A stock for each skill of the pawn whose mechanics are read; each other skill is named, never defaulted.
    /// </summary>
    internal static IEnumerable<SkillStock> Of(ModelPawn pawn, SortedSet<string> findings)
    {
        foreach (var skill in pawn.Skills.Where(s => s.DefName.Length > 0))
        {
            if (ForecastSkillPolicy.MechanicsGap(skill.Learning) is { } gap)
            {
                findings.Add($"skill_stock_unknown:{ProjectGraph.SkillKey(pawn.Pawn, skill.DefName)}:{gap}");
                continue;
            }

            yield return new SkillStock(pawn.Pawn, skill.DefName, skill.Learning!);
        }
    }

    /// <summary>The native decay of a level a day: its <c>DecayXp</c> once per decay interval while learning is allowed.</summary>
    private double Decay(int level) =>
        Disabled || !mechanics.TrackerAllowsLearning ? 0 : mechanics.Levels[level].DecayXp * GameTime.TicksPerDay / mechanics.DecayInterval;

    /// <summary>
    /// Starts the stock one stored level higher, its XP toward the next level kept (the perturbation of the marginal value, #3344);
    /// a stock at the top level is left as it is.
    /// </summary>
    internal void Raise(double day = 0)
    {
        var top = mechanics.Levels.Count - 1;
        if (StoredLevel == top)
        {
            return;
        }

        StoredLevel++;
        if (StoredLevel == top)
        {
            xp = Math.Min(xp, mechanics.Levels[top].RequiredXp - 1);
        }

        dates.Add(new SkillLevelDate(day, StoredLevel));
    }

    /// <summary>
    /// The days of <paramref name="horizonDays"/> a raise of the stock by one stored level lasts (#3912): it is raised at time zero and
    /// advanced by the decay of each level it passes through, and the raise is over on the first day the stored level falls below the
    /// raised one. 0 at the top level, where there is no level to raise; the whole horizon when decay never takes it back. Mutates the stock.
    /// </summary>
    internal double RaisedLasts(double horizonDays)
    {
        if (StoredLevel == mechanics.Levels.Count - 1)
        {
            return 0;
        }

        Raise();
        var raised = StoredLevel;
        Advance(0, horizonDays);
        return dates.Skip(1).FirstOrDefault(d => d.StoredLevel < raised)?.Day ?? horizonDays;
    }

    /// <summary>Moves the stock over <paramref name="span"/> days from <paramref name="from"/> by the decay of each level it passes through.</summary>
    internal void Advance(double from, double span)
    {
        var elapsed = 0.0;
        while (elapsed < span)
        {
            var rate = Decay(StoredLevel);
            var left = span - elapsed;
            var need = rate > 0 && StoredLevel > 0 ? (xp - mechanics.LossThreshold) / rate : double.PositiveInfinity;
            if (need > left)
            {
                xp -= rate * left;
                return;
            }

            elapsed += Math.Max(0, need);
            xp = mechanics.LossThreshold;
            Settle(from + elapsed);
        }
    }

    private void Settle(double day)
    {
        var level = StoredLevel;
        ForecastSkillPolicy.Settle(mechanics, ref level, ref xp);
        if (level != StoredLevel)
        {
            StoredLevel = level;
            dates.Add(new SkillLevelDate(day, level));
        }
    }

    internal SkillTrack Track() => new()
    {
        Key = Key,
        Pawn = Pawn,
        Skill = Skill,
        StoredLevel = startStored,
        NextLevel = startStored < mechanics.Levels.Count - 1 ? new SkillNextLevel.Takes(mechanics.Levels[startStored].RequiredXp) : new SkillNextLevel.Top(),
        Dates = EquatableArray.From(dates),
    };
}
