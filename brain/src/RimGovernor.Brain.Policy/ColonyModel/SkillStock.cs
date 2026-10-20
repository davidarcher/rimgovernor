using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

// The tier-2 skill stock (#3634 of #3344): one per pawn and skill, keyed skill/<pawn>/<skill>, started from the observed SkillLearning.
// XP moves at piecewise-constant rates between the engine's events: the practice it is staffed on (raw XP a day, through the pawn's
// learning factors and the daily saturation) less the native decay of its level, coarsened to days. Levels change at the native
// RequiredXp thresholds by the level rule ForecastSkill uses (ForecastSkillPolicy.Settle); each change is dated inside the interval.

/// <summary>A level a skill stock reached and the day, from now, it reached it.</summary>
public sealed record SkillLevelDate(double Day, int StoredLevel, int Level);

/// <summary>One skill stock over a run: where it started and each level change in order.</summary>
public sealed record SkillTrack
{
    /// <summary><see cref="ProjectGraph.SkillKey"/>.</summary>
    public string Key { get; init; } = "";

    public string Pawn { get; init; } = "";

    public string Skill { get; init; } = "";

    /// <summary>The observed stored level (<c>levelInt</c>) and the level with aptitude, at time zero.</summary>
    public int StoredLevel { get; init; }

    public int Level { get; init; }

    public EquatableArray<SkillLevelDate> Dates { get; init; }

    /// <summary>The level with aptitude on <paramref name="day"/>: the last change at or before it.</summary>
    public int LevelOn(double day) => Dates.LastOrDefault(d => d.Day <= day)?.Level ?? Level;
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
        StoredLevel = startStored = mechanics.StoredLevel.Value;
        xp = mechanics.Xp.Value;
    }

    internal string Pawn { get; }

    internal string Skill { get; }

    internal string Key => ProjectGraph.SkillKey(Pawn, Skill);

    internal int StoredLevel { get; private set; }

    /// <summary>XP toward the next level.</summary>
    internal double Xp => xp;

    /// <summary>Raw XP a day the stock's practice pays until the next event (<see cref="ProjectRun"/> sets it per interval).</summary>
    internal double RawPerDay { get; set; }

    internal bool Disabled => mechanics.Disabled.Value;

    /// <summary>A stock for each skill of the pawn whose mechanics are read; each other skill is named, never defaulted.</summary>
    internal static IEnumerable<SkillStock> Of(ModelPawn pawn, SortedSet<string> findings)
    {
        if (!pawn.Skills.TryGetValue(out var skills))
        {
            yield break;
        }

        foreach (var skill in skills.Where(s => s.Name.Length > 0))
        {
            if (ForecastSkillPolicy.MechanicsGap(skill.Learning) is { } gap)
            {
                findings.Add($"skill_stock_unknown:{ProjectGraph.SkillKey(pawn.Pawn, skill.Name)}:{gap}");
                continue;
            }

            yield return new SkillStock(pawn.Pawn, skill.Name, skill.Learning!);
        }
    }

    /// <summary>The level with aptitude at <paramref name="stored"/>: zero while disabled, else clamped to the native range.</summary>
    private int Effective(int stored) => Disabled ? 0 : (int)Math.Clamp((long)stored + mechanics.Aptitude.Value, 0, mechanics.Levels.Count - 1);

    /// <summary>
    /// Applied XP a day from <paramref name="raw"/> raw XP a day of practice callbacks: the unsaturated factor until the day's XP
    /// passes the saturation threshold, the saturated factor on the rest (<c>SkillRecord.Learn</c>, reset at midnight). Coarsened
    /// to days: each day starts unsaturated.
    /// </summary>
    private double Applied(double raw)
    {
        if (Disabled || !(raw > 0))
        {
            return 0;
        }

        var unsaturated = raw * mechanics.UnsaturatedFactor.Value;
        var threshold = Math.Max(0, mechanics.SaturationThreshold.Value);
        return unsaturated <= threshold ? unsaturated : threshold + ((unsaturated - threshold) * mechanics.SaturatedFactor.Value);
    }

    /// <summary>The native decay of a level a day: its <c>DecayXp</c> once per decay interval while learning is allowed.</summary>
    private double Decay(int level) =>
        Disabled || !mechanics.TrackerAllowsLearning.Value ? 0 : mechanics.Levels[level].DecayXp * GameTime.TicksPerDay / mechanics.DecayInterval.Value;

    /// <summary>Adds applied XP at <paramref name="day"/> (the perturbation of the marginal value).</summary>
    internal void Add(double amount, double day)
    {
        xp += amount;
        Settle(day);
    }

    /// <summary>Moves the stock over <paramref name="span"/> days from <paramref name="from"/> at the current practice less the decay of each level it passes through.</summary>
    internal void Advance(double from, double span)
    {
        var gain = Applied(RawPerDay);
        var top = mechanics.Levels.Count - 1;
        var elapsed = 0.0;
        while (elapsed < span)
        {
            var rate = gain - Decay(StoredLevel);
            var left = span - elapsed;
            double need;
            if (rate > 0)
            {
                if (StoredLevel == top)
                {
                    xp = Math.Min(xp + (rate * left), mechanics.Levels[top].RequiredXp - 1);
                    return;
                }

                need = (mechanics.Levels[StoredLevel].RequiredXp - xp) / rate;
            }
            else if (rate < 0 && StoredLevel > 0)
            {
                need = (xp - mechanics.LossThreshold.Value) / -rate;
            }
            else
            {
                xp += rate * left;
                return;
            }

            if (need > left)
            {
                xp += rate * left;
                return;
            }

            elapsed += Math.Max(0, need);
            xp = rate > 0 ? mechanics.Levels[StoredLevel].RequiredXp : mechanics.LossThreshold.Value;
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
            dates.Add(new SkillLevelDate(day, level, Effective(level)));
        }
    }

    internal SkillTrack Track() => new()
    {
        Key = Key,
        Pawn = Pawn,
        Skill = Skill,
        StoredLevel = startStored,
        Level = Effective(startStored),
        Dates = EquatableArray.From(dates),
    };
}
