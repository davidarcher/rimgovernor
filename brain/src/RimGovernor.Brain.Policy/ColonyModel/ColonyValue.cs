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
/// Prices trajectories under one review's context. The value is the apparel harm avoided (<see cref="ApparelGain"/>: each garment
/// worn from the day it is made to the horizon, credited with its gain over what its wearer wore before) less the capability lost
/// to starvation: the colony's starvation spans walk each pawn's malnutrition severity through the game's stage table from the
/// observed severity, and a pawn whose severity reaches lethal costs the rest of the horizon plus
/// <see cref="ForecastValuePolicy.DeathTerminalValue"/>, as the ledger's thermal term does.
/// </summary>
public sealed class ColonyValue(ForecastCoupledContext context, IReadOnlyList<GearOption> options, double horizonDays) : ITrajectoryValue
{
    /// <summary>The finding of apparel the model cannot price: the harm model gave no number, or no gear option matches the product.</summary>
    public const string ApparelHarmUnassessed = "apparel_harm_unassessed";

    // The stub outcome the harm ledger prices a single pawn's outfit with: it reads the pawn set and the support only.
    private static readonly ForecastNeedsResult NoNeeds = new([], default, default, new ForecastCapacityResult(default!, []), []);

    private readonly ConcurrentDictionary<(string, string, EquatableArray<GearOption>, ForecastSupportConsequences, HitRange), HarmDistribution> harmCache = new();
    private readonly Dictionary<string, NeedsForecastPawn> rows = context.Needs.Pawns.GroupBy(p => p.Pawn, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    // The perturbation runs walk the same outfits: one review's losses are priced once.
    private readonly Dictionary<(string, EquatableArray<GearOption>), Fact<double>> losses = [];

    public ColonyValueResult Price(ColonyTrajectory trajectory)
    {
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        var hunger = HungerLoss(trajectory, findings);
        var apparel = ApparelGain(trajectory, findings);
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

    /// <summary>
    /// The medicine raid tending uses up a day (#3634): the treatments the harm model's raid wounds take at each pawn's worn outfit
    /// (<see cref="ColonyModelPolicy.MedicineTendsPerDay"/>). Unknown when the harm model cannot say.
    /// </summary>
    public Fact<double> RaidTreatmentsPerDay() => HarmLedger.TreatmentsPerDay(context, rows);

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
    /// Capability-days the run's apparel avoids in harm (#3622). The garments are walked in the order they are made; each unit goes
    /// to the pawn it helps most (the loss per day of the pawn's outfit then less the loss with the unit in its slot) and is credited
    /// with that gain for the days from its making to the horizon. The pawn wears it from then on, so a later garment in that slot is
    /// credited only with its gain over this one, and a unit no pawn gains from is not worn. A garment the harm model or the gear
    /// options cannot price is named (<see cref="ApparelHarmUnassessed"/>) and credited nothing.
    /// </summary>
    private double ApparelGain(ColonyTrajectory trajectory, SortedSet<string> findings, List<(string Pawn, GearOption Garment, double Day)>? wearers = null)
    {
        var outfits = new SortedDictionary<string, EquatableArray<GearOption>>(StringComparer.Ordinal);
        foreach (var (pawn, worn) in context.Outfits)
        {
            if (rows.ContainsKey(pawn))
            {
                outfits[pawn] = worn;
            }
        }

        var total = 0.0;
        foreach (var made in trajectory.Apparel.OrderBy(m => m.Day).ThenBy(m => m.Node, StringComparer.Ordinal))
        {
            var worn = new SortedDictionary<string, EquatableArray<GearOption>>(outfits, StringComparer.Ordinal);
            var units = new List<(string, GearOption)>();
            if (Wear(made, worn, units) is { } gain)
            {
                total += gain * Math.Max(0, horizonDays - made.Day);
                outfits = worn;
                wearers?.AddRange(units.Select(u => (u.Item1, u.Item2, made.Day)));
            }
            else
            {
                findings.Add(ApparelHarmUnassessed);
            }
        }

        return total;
    }

    /// <summary>
    /// The stuff of the garment each pawn ends up wearing in each slot by the apparel walk (<see cref="ApparelGain"/>), among the units
    /// whose stuff arrives within <paramref name="withinDays"/> (#3623): the clothing runway demands it. A unit's stuff arrives with the
    /// run's last arrival of it at or before the unit is made, now when none (it was in stock). A pawn and slot no such unit goes to is
    /// absent: tier 2 does not model wear, so a like-for-like replacement gains nothing and wins nothing.
    /// </summary>
    public IReadOnlyDictionary<(PawnId Pawn, GearSlot Slot), Resource> ClothingStuff(ColonyTrajectory trajectory, double withinDays)
    {
        var wearers = new List<(string Pawn, GearOption Garment, double Day)>();
        ApparelGain(trajectory, new SortedSet<string>(StringComparer.Ordinal), wearers);
        var chosen = new Dictionary<(PawnId Pawn, GearSlot Slot), Resource>();
        foreach (var (pawn, garment, day) in wearers)
        {
            var arrived = trajectory.Arrivals.Where(a => a.Resource == garment.Stuff && a.Day <= day).Select(a => a.Day).DefaultIfEmpty(0).Max();
            if (!garment.Stuff.IsEmpty && arrived <= withinDays)
            {
                chosen[(new PawnId(pawn), garment.Slot)] = garment.Stuff;
            }
        }

        return chosen;
    }

    /// <summary>
    /// Puts each unit of the garment on the pawn it helps most, in <paramref name="outfits"/>, each wearer and garment added to
    /// <paramref name="wearers"/>; the gain a day, or null while unpriced.
    /// </summary>
    private double? Wear(ApparelMade made, SortedDictionary<string, EquatableArray<GearOption>> outfits, List<(string, GearOption)> wearers)
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

            for (var unit = 0; unit < product.Count; unit++)
            {
                var (wearer, best) = ((string?)null, 0.0);
                foreach (var (pawn, worn) in outfits)
                {
                    if (!DailyLoss(pawn, worn).TryGetValue(out var b) || !DailyLoss(pawn, Wearing(worn, option)).TryGetValue(out var a))
                    {
                        return null;
                    }

                    if (b - a > best)
                    {
                        (wearer, best) = (pawn, b - a);
                    }
                }

                if (wearer is null)
                {
                    break;
                }

                sum += best;
                outfits[wearer] = Wearing(outfits[wearer], option);
                wearers.Add((wearer, option));
            }
        }

        return sum;
    }

    private static EquatableArray<GearOption> Wearing(EquatableArray<GearOption> worn, GearOption option) =>
        EquatableArray.From(worn.Where(w => w.Slot != option.Slot).Append(option));

    /// <summary>One pawn's harm in capability-days for a day of raids at the outfit; the horizon is a day so the loss is per day (the cache keys omit it).</summary>
    private Fact<double> DailyLoss(string pawn, EquatableArray<GearOption> outfit)
    {
        if (losses.TryGetValue((pawn, outfit), out var known))
        {
            return known;
        }

        var outcome = new ForecastCoupledOutcome(new([], 0, 0), Known(true), new Dictionary<string, ForecastNeedsResult> { [pawn] = NoNeeds }, []);
        return losses[(pawn, outfit)] = HarmLedger.Of(outcome, context, GameTime.TicksPerDay, harmCache, rows,
            [new ForecastCommitment("apparel", default) { OutfitChanges = [new(pawn, outfit)] }]).Loss;
    }
}
