using RimGovernor.Brain.GameFacts.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>Hunger-driven cell meals against the prisoner's live memory groups. This projects consequences only;
/// native wardens and prisoner jobs remain the execution owners.</summary>
public sealed class PrisonCellForecast(PrisonerMoodRead mood, PrisonMealRead? meal, int roomStage, bool seat, double servedAt,
    PlannedSleepingSurface? bed = null, EquatableArray<PlannedCellBeauty> beauty = default, double maximumComfort = 0)
{
    private sealed record Member(NeedMemory Memory, NeedMemory Fresh, double At);
    private sealed record Group(string Thought, int Stage, double Multiplier, List<Member> Members);

    /// <summary>The change to the existing-memory projection at this chat tick.</summary>
    public double At(double tick, bool upper = false)
    {
        if (tick < servedAt) return 0;
        return MealsAt(tick, upper) + EnvironmentAt(tick, upper);
    }

    private double MealsAt(double tick, bool upper)
    {
        if (meal is null) return 0;
        DomainException.Require(meal.PeriodTicks > 0 && double.IsFinite(meal.PeriodTicks) && meal.FirstTicks >= 0,
            "prison meal cadence");
        var gains = meal.Thoughts.ToList();
        if (meal.RoomThought)
        {
            gains.AddRange(Stage(ForecastRoomThoughts.DiningRole, roomStage));
            // A seat may be occupied when the prisoner eats. Lower mood keeps the no-table memory; upper mood uses
            // the available seat. An unfurnished room cannot avoid the memory in either bound.
            if (meal.TableDesired && (!seat || !upper)) gains.AddRange(Stage(ForecastRoomThoughts.AteWithoutTableRole, 0));
        }
        var ids = gains.Select(g => g.Thought).ToHashSet(StringComparer.Ordinal);
        var groups = mood.Memories.Where(g => g.Members.Any(m => ids.Contains(m.Id))).Select(g =>
        {
            if (servedAt > 0 && g.BedlessRenewal is { } renewed)
                return new Group(renewed.Id, g.Stage, g.StackedMultiplier, [new(renewed, renewed, servedAt)]);
            if (g.FreshMembers.Count != g.Members.Count) throw new RequiredFactException("prison renewable memory members");
            return new Group(g.Members[0].Id, g.Stage, g.StackedMultiplier,
                g.Members.Select((m, i) => new Member(m, g.FreshMembers[i], 0)).ToList());
        }).ToList();
        for (var at = servedAt + meal.FirstTicks; at <= tick; at += meal.PeriodTicks)
            foreach (var gain in gains)
            {
                if (gain.Lasting is not ThoughtLasting.Memory lasting) throw new RequiredFactException("prison meal memory:" + gain.Thought);
                var group = groups.FirstOrDefault(g => g.Thought == gain.Thought && (lasting.StagesStack || g.Stage == gain.Stage));
                if (group is null) { group = new(gain.Thought, gain.Stage, lasting.StackedMultiplier, []); groups.Add(group); }
                group.Members.RemoveAll(m => Expired(m, at));
                if (group.Members.Count >= Math.Max(1, lasting.StackLimit))
                {
                    // TryMergeWithExistingMemory renews the oldest member, preserving its original offset and stage.
                    var oldest = group.Members.MinBy(m => m.Memory.Life is MemoryLife.Expires e && m.Fresh.Life is MemoryLife.Expires full
                        ? m.At - (full.RemainingTicks - e.RemainingTicks) : m.At)!;
                    group.Members.Remove(oldest);
                    group.Members.Add(oldest with { Memory = oldest.Fresh, At = at });
                }
                else
                {
                    var memory = new NeedMemory(gain.Thought, gain.Offset, new MemoryLife.Expires(lasting.DurationTicks), lasting.Fades)
                    { StackLimit = lasting.StackLimit };
                    group.Members.Add(new(memory, memory, at));
                }
            }
        var projected = groups.Sum(g => ForecastPrison.MoodGroupAt(new(g.Multiplier)
        {
            BedlessRenewal = null,
            Members = EquatableArray.From(g.Members.Where(m => !Expired(m, tick)).Select(m => Aged(m, tick))),
        }, 0));
        return projected - mood.Memories.Where(g => g.Members.Any(m => ids.Contains(m.Id))).Sum(g => ForecastPrison.MoodGroupAt(g, tick, servedAt));
    }

    private NeedThoughtStage[] Stage(string role, int stage)
    {
        var rows = meal!.RoomStages.Where(r => r.Role == role && r.Stage == stage).ToArray();
        return rows.Length > 0 ? rows : throw new RequiredFactException("prison " + role + " stage " + stage);
    }

    private double EnvironmentAt(double tick, bool upper)
    {
        if (mood.Comfort is null && mood.Beauty is null) return 0;
        var comfort = mood.Comfort?.Level ?? 0;
        var beautiful = mood.Beauty?.Level ?? 0;
        var rest = mood.Rest;
        if (rest is { Blocked: false }) DomainException.Require(rest.WakeThreshold > rest.SleepThreshold,
            "prison rest thresholds");
        var level = rest?.Level ?? 1;
        var asleep = rest is { Blocked: false } && (rest.Asleep || level <= rest.SleepThreshold);
        if (rest is { Blocked: false } && bed is null) throw new RequiredFactException("prison sleeping surface");
        // Ground rest remains real service before the planned bed arrives. Carry its level and sleep phase into
        // the planned cell rather than subtracting days of waking fall from a pawn that can already sleep.
        for (var at = 0d; at < tick;)
        {
            var inCell = at >= servedAt;
            var duration = (inCell ? tick : Math.Min(tick, servedAt)) - at;
            var switchSleep = false;
            if (rest is { Blocked: false })
            {
                var rate = asleep ? inCell ? rest.GainPerEffectiveness * bed!.RestEffectiveness : rest.GroundGainPerTick : rest.FallPerTick;
                DomainException.Require(rate > 0, "prison rest rate");
                var boundary = Math.Max(0, asleep ? (rest.WakeThreshold - level) / rate : (level - rest.SleepThreshold) / rate);
                switchSleep = boundary <= duration;
                duration = Math.Min(duration, boundary);
                if (duration == 0) { level = asleep ? rest.WakeThreshold : rest.SleepThreshold; asleep = !asleep; continue; }
                level += (asleep ? 1 : -1) * rate * duration;
            }
            if (inCell && mood.Comfort is { } c) comfort = Seeker(c, LayoutComfortRules.SeekerGame().Comfort).Advance(comfort,
                asleep ? bed!.Comfort : upper ? maximumComfort : 0, asleep, duration);
            if (inCell && mood.Beauty is { } b)
            {
                if (!b.Blind && beauty.Count == 0) throw new RequiredFactException("prison perceptible beauty");
                var target = b.Blind ? 0.5 : Math.Clamp(b.Base + (upper ? beauty.Max(c => c.Beauty) : beauty.Min(c => c.Beauty)) * 0.1, 0, 1);
                beautiful = Seeker(b, LayoutComfortRules.SeekerGame().Beauty).Advance(beautiful, target, asleep, duration);
            }
            at += duration;
            if (switchSleep) { level = asleep ? rest!.WakeThreshold : rest!.SleepThreshold; asleep = !asleep; }
        }
        return Offset(mood.Comfort, comfort, LayoutComfortRules.SeekerGame().Comfort)
            + Offset(mood.Beauty, beautiful, LayoutComfortRules.SeekerGame().Beauty);
    }

    private static SeekerNeed Seeker(PrisonSeekerRead read, SeekerGameDef game) => new(new(read.Base, read.RisePerHour, read.FallPerHour, read.FreezeAsleep), game);

    private static double Offset(PrisonSeekerRead? read, double level, SeekerGameDef game)
    {
        if (read is null) return 0;
        var stage = game.Bands.Stage(level);
        var rows = read.Stages.Where(r => r.Stage == stage).ToArray();
        if (stage >= 0 && rows.Length == 0) throw new RequiredFactException("prison " + game.Thought + " stage " + stage);
        return rows.Sum(r => r.Offset) - read.ThoughtNow;
    }

    private static bool Expired(Member member, double tick) => member.Memory.Life is MemoryLife.Expires e && tick - member.At > e.RemainingTicks;

    private static NeedMemory Aged(Member member, double tick)
    {
        if (member.Memory.Life is not MemoryLife.Expires e) return member.Memory;
        var remaining = Math.Max(0, e.RemainingTicks - (tick - member.At));
        return member.Memory with
        {
            Offset = member.Memory.Fades && e.RemainingTicks > 0 ? member.Memory.Offset * remaining / e.RemainingTicks : member.Memory.Offset,
            Life = new MemoryLife.Expires(remaining),
        };
    }
}
