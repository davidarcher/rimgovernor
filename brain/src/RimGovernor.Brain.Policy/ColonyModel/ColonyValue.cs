using System.Collections.Concurrent;
using RimGovernor.Contracts.Wire.Observations;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Policy;

// The value of a tier-2 trajectory in capability-days (#3531 of #3470), priced by the same tables the tier-1 ledger uses: the
// malnutrition stage table for starvation, the harm model for the apparel the chain makes. Higher is better. The marginal value
// of a project is the difference of two of these (ProjectMarginalValue), so a part that is unassessed in both runs cancels.

/// <summary>A trajectory's value in capability-days (higher is better), unknown when its hunger cannot be priced, and what was left unassessed.</summary>
public sealed record ColonyValueResult(Fact<double> Value, EquatableArray<string> Findings);

/// <summary>
/// Prices trajectories under one review's context. The value is the apparel harm avoided (per day worn, from the day the craft
/// finishes to the horizon) less the capability lost to starvation: the colony's starvation spans walk each pawn's malnutrition
/// severity through the game's stage table from the observed severity, and a pawn whose severity reaches lethal costs the
/// rest of the horizon plus <see cref="ForecastValuePolicy.DeathTerminalValue"/>, as the ledger's thermal term does.
/// </summary>
public sealed class ColonyValue(ForecastCoupledContext context, IReadOnlyList<GearOption> options, double horizonDays)
{
    /// <summary>The finding of apparel the model cannot price: the harm model gave no number, or no gear option matches the product.</summary>
    public const string ApparelHarmUnassessed = "apparel_harm_unassessed";

    // The stub outcome the harm ledger prices a single pawn's outfit with: it reads the pawn set and the support only.
    private static readonly ForecastNeedsResult NoNeeds = new([], default, default, new ForecastCapacityResult(default!, []), []);

    private readonly ConcurrentDictionary<(string, string, EquatableArray<GearOption>, ForecastSupportConsequences, HitRange), HarmDistribution> harmCache = new();
    private readonly Dictionary<string, NeedsForecastPawn> rows = context.Needs.Pawns.GroupBy(p => p.Pawn, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    private readonly Dictionary<string, double?> perDay = new(StringComparer.Ordinal);

    public ColonyValueResult Of(ColonyTrajectory trajectory)
    {
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        var hunger = HungerLoss(trajectory, findings);
        double apparel = 0;
        foreach (var made in trajectory.Apparel)
        {
            if (ApparelPerDay(made) is { } gain)
            {
                apparel += gain * Math.Max(0, horizonDays - made.Day);
            }
            else
            {
                findings.Add(ApparelHarmUnassessed);
            }
        }

        return new(hunger is { } lost ? Known(apparel - lost) : default, EquatableArray.From(findings));
    }

    /// <summary>
    /// Capability-days a goal is worth if it is completed on day <paramref name="completionDay"/> from now (#3505): its worth at that day
    /// (importance, with the deadline's decay) for the part of the horizon it then stands, as the apparel is worth its harm avoided for
    /// the days it is worn. Zero for a goal not completed inside the horizon. Advancing the completion is what an hour of its work earns.
    /// </summary>
    public double GoalValue(GoalTerminal goal, double? completionDay)
    {
        if (completionDay is not { } day || day >= horizonDays)
        {
            return 0;
        }

        var gameDay = ((double)context.Needs.Tick / GameTime.TicksPerDay) + day;
        return context.Value.GoalWorthOn(goal.Importance, goal.DeadlineDay, gameDay) * (horizonDays - day) / horizonDays;
    }

    /// <summary>The colony's capability-days lost to malnutrition over the horizon; null when a table or a pawn's capacities are unobserved.</summary>
    private double? HungerLoss(ColonyTrajectory trajectory, SortedSet<string> findings)
    {
        var tables = context.CapacityPenalties;
        if (!tables.Malnutrition.Valid() || tables.MovingMinForCapable is not { } movingMin)
        {
            findings.Add("capacity_penalty_table_unknown:" + nameof(tables.Malnutrition));
            return null;
        }

        var table = tables.Malnutrition;
        var rate = ForecastValue.MalnutritionSeverityPerDay;
        var total = 0.0;
        foreach (var row in rows.Values.OrderBy(r => r.Pawn, StringComparer.Ordinal))
        {
            if (!row.MalnutritionSeverity.TryGetValue(out var severity) || ForecastValue.Levels(row) is not { } levels)
            {
                findings.Add("capacities_unobserved:" + row.Pawn);
                return null;
            }

            var course = new ForecastValue.Course(table, levels, movingMin, severity);
            var clock = 0.0;
            double? died = null;

            // Moves the severity for length days; a rising stretch that reaches the lethal severity stops the walk there.
            void Walk(double length, double perDayRate)
            {
                if (died is not null || length <= 0)
                {
                    return;
                }

                if (perDayRate > 0 && table.LethalSeverity is { } top)
                {
                    var need = Math.Max(0, (top - course.Severity) / perDayRate);
                    if (need <= length)
                    {
                        course.Advance(need * GameTime.TicksPerDay, perDayRate);
                        course.MarkLethal();
                        died = clock + need;
                        return;
                    }
                }

                course.Advance(length * GameTime.TicksPerDay, perDayRate);
                clock += length;
            }

            foreach (var span in trajectory.Starvation.OrderBy(s => s.Start))
            {
                Walk(span.Start - clock, -rate);
                Walk(span.End - Math.Max(span.Start, clock), rate);
            }

            Walk(horizonDays - clock, -rate);
            course.Report(row.Pawn, findings);
            total += course.LostTicks / GameTime.TicksPerDay;
            if (died is { } day)
            {
                total += Math.Max(0, horizonDays - day) + context.Value.DeathTerminalValue;
            }
        }

        return total;
    }

    /// <summary>
    /// Capability-days a day the apparel is worn avoids in harm, over the pawns that wear the best of each unit made (the loss per
    /// day of its outfit now less the loss with the product in its slot); null while the harm model or the product's gear option is unassessed.
    /// </summary>
    private double? ApparelPerDay(ApparelMade made)
    {
        if (perDay.TryGetValue(made.Node, out var known))
        {
            return known;
        }

        return perDay[made.Node] = Price(made);
    }

    private double? Price(ApparelMade made)
    {
        var sum = 0.0;
        foreach (var product in made.Products.Where(p => p.Count > 0))
        {
            var option = options.FirstOrDefault(o => o.Definition == product.Resource
                && (string.IsNullOrEmpty(o.Stuff.Value) || made.Ingredients.Any(i => i.Resource == o.Stuff)));
            if (option is null)
            {
                return null;
            }

            var gains = new List<double>();
            foreach (var (pawn, worn) in context.Outfits.OrderBy(o => o.Key, StringComparer.Ordinal))
            {
                if (!rows.ContainsKey(pawn))
                {
                    continue;
                }

                var before = DailyLoss(pawn, worn);
                var after = DailyLoss(pawn, EquatableArray.From(worn.Where(w => w.Slot != option.Slot).Append(option)));
                if (!before.TryGetValue(out var b) || !after.TryGetValue(out var a))
                {
                    return null;
                }

                gains.Add(b - a);
            }

            sum += gains.Where(g => g > 0).OrderByDescending(g => g).Take((int)product.Count).Sum();
        }

        return sum;
    }

    /// <summary>One pawn's harm in capability-days for a day of raids at the outfit; the horizon is a day so the loss is per day (the cache keys omit it).</summary>
    private Fact<double> DailyLoss(string pawn, EquatableArray<GearOption> outfit)
    {
        var outcome = new ForecastCoupledOutcome(new([], 0, 0), Known(true), new Dictionary<string, ForecastNeedsResult> { [pawn] = NoNeeds }, []);
        return HarmLedger.Of(outcome, context, GameTime.TicksPerDay, harmCache, rows,
            [new ForecastCommitment("apparel", default) { OutfitChanges = [new(pawn, outfit)] }]).Loss;
    }
}
