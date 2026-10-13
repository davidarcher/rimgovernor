using System.Globalization;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// One shared population, regardless of how many zones or shallow cells offer access to it. Catch rates come
/// from native definitions and the available fishers; they must not be inferred from zone area. (Go <c>FishingRegion</c>.)
/// </summary>
public sealed record FishingRegion
{
    public string Id { get; init; } = "";

    public Fact<double> Population { get; init; }

    public Fact<double> MaxPopulation { get; init; }

    public Fact<double> NutritionPerFish { get; init; }

    public Fact<double> FishPerBatch { get; init; }

    public Fact<double> WorkTicksPerBatch { get; init; }

    /// <summary>Raw nutrition/day.</summary>
    public Fact<double> PawnFishWorkCapacity { get; init; }

    public Fact<bool> Reachable { get; init; }

    public Fact<bool> Frozen { get; init; }

    /// <summary>A zone is set to fish it.</summary>
    public Fact<bool> Designated { get; init; }

    /// <summary>Ledger counter group of its deliveries.</summary>
    public string Source { get; init; } = "";

    /// <summary>Nearest cell to the colony centre.</summary>
    public Fact<double> DistanceSquared { get; init; }
}

public sealed record FishingRequest
{
    public IReadOnlyList<FishingRegion> Regions { get; init; } = [];

    public Fact<bool> Researched { get; init; }

    public Fact<double> ResearchLeadDays { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>The ordinary game's fraction of a water body's maximum population restored per day. It is a fish count, not nutrition.</summary>
    public const double FishingRegenerationPerDay = 0.025;

    /// <summary>
    /// Go <c>FishingChannels</c>: estimates sustainable raw nutrition. Cooking conversion is a separate ledger
    /// contribution. Unknown facts never certify a delivery rate; malformed known facts throw the same
    /// <see cref="FoodPlanFactsException"/> as SupplyFoodPlan. An empty region set (including Core without
    /// Odyssey) produces no fishing channel.
    /// </summary>
    public static IReadOnlyList<SupplyCandidate> FishingChannels(FishingRequest r)
    {
        if (r.ResearchLeadDays.TryGetValue(out var leadDays) && !FoodNumber(leadDays))
        {
            throw new FoodPlanFactsException();
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var channels = new List<SupplyCandidate>();
        foreach (var region in r.Regions)
        {
            if (!FoodId(region.Id) || !seen.Add(region.Id))
            {
                throw new FoodPlanFactsException();
            }
            foreach (var f in (ReadOnlySpan<Fact<double>>)[region.Population, region.MaxPopulation, region.NutritionPerFish, region.FishPerBatch, region.WorkTicksPerBatch, region.PawnFishWorkCapacity])
            {
                if (f.TryGetValue(out var v) && !FoodNumber(v))
                {
                    throw new FoodPlanFactsException();
                }
            }
            var pk = region.Population.TryGetValue(out var population);
            var mk = region.MaxPopulation.TryGetValue(out var maximum);
            var nk = region.NutritionPerFish.TryGetValue(out var nutrition);
            var bk = region.FishPerBatch.TryGetValue(out var batch);
            var wk = region.WorkTicksPerBatch.TryGetValue(out var work);
            var capKnown = region.PawnFishWorkCapacity.TryGetValue(out var capacity);
            if ((pk && mk && population > maximum) || (nk && nutrition == 0) || (bk && batch == 0) || (wk && work == 0))
            {
                throw new FoodPlanFactsException();
            }
            var open = NotDelivering(region.Designated);
            var designated = region.Designated;
            var channel = FoodCandidate(CandidateKind.Fishing, region.Id, Unknown<double>());
            var terms = new List<CandidateTerm> { new("fish_regeneration_fraction", FishingRegenerationPerDay) };
            channel = channel with { Source = region.Source, DistanceSquared = region.DistanceSquared };
            if (r.Researched.TryGetValue(out var researched))
            {
                channel = channel with { LeadDays = r.ResearchLeadDays };
                if (researched)
                {
                    channel = channel with { LeadDays = Known(0.0) };
                }
                else
                {
                    open = Known(false);
                    designated = Known(false);
                    terms.Add(new CandidateTerm("fishing_research_required", 1));
                }
            }
            var rk = region.Reachable.TryGetValue(out var reachable);
            var fk = region.Frozen.TryGetValue(out var frozen);
            if ((rk && !reachable) || (fk && frozen) || (pk && population == 0) || (mk && maximum == 0))
            {
                // Preserve an explicit explanation without presenting an unusable region as an observed-open source of food.
                open = Known(false);
                designated = Known(false);
                channel = WithFirstYield(channel, Known(0.0), Known(0.0));
                if (rk && !reachable)
                {
                    terms.Add(new CandidateTerm("fishing_unreachable", 1));
                }
                if (fk && frozen)
                {
                    terms.Add(new CandidateTerm("fishing_frozen", 1));
                }
            }
            else if (pk && mk && population <= maximum * DomainConstants.FishingPopulationFloor)
            {
                // Native pauses fishing at or below the floor until the lake regrows: no catch, so the zone is not delivering meanwhile.
                channel = WithFirstYield(channel, Known(0.0), Known(0.0));
                terms.Add(new CandidateTerm("fishing_below_floor", population / maximum));
            }
            else if (pk && mk && nk && bk && wk && rk && fk && capKnown)
            {
                // The sustainable average, not today's catch quota. Native population-floor control permits bursts and subsequent recovery.
                var regen = maximum * FishingRegenerationPerDay * nutrition;
                if (!FoodNumber(regen))
                {
                    throw new FoodPlanFactsException();
                }
                var dailyNutrition = Math.Min(regen, capacity);
                var draw = dailyNutrition / nutrition;
                var dailyWork = draw / batch * work;
                if (!FoodNumber(dailyNutrition) || !FoodNumber(dailyWork))
                {
                    throw new FoodPlanFactsException();
                }
                channel = WithFirstYield(channel, Known(dailyNutrition), Known(dailyWork));
                terms.Add(new CandidateTerm("fish_draw_per_day", draw));
                terms.Add(new CandidateTerm("fish_per_batch", batch));
            }
            channels.Add(channel with { Terms = terms, State = FoodState(open, designated) });
        }
        return channels;
    }

    /// <summary>Sets the first yield's per-day rate and the channel's daily labor (Go: <c>channel.Yields[0].PerDay, channel.LaborPerDay = ...</c>).</summary>
    private static SupplyCandidate WithFirstYield(SupplyCandidate channel, Fact<double> perDay, Fact<double> laborPerDay) =>
        channel with { Yields = [channel.Yields[0] with { PerDay = perDay }, .. channel.Yields.Skip(1)], LaborPerDay = laborPerDay };

    /// <summary>A channel's Open before the ledger: not delivering, unknown when its designation is.</summary>
    private static Fact<bool> NotDelivering(Fact<bool> designated) => designated.IsKnown ? Known(false) : designated;

    /// <summary>
    /// Go <c>FishingResearchRequest</c>: asks the existing research goal to open a selected source. A deferred,
    /// closed or unknown channel cannot change research intent. Empty when nothing is asked.
    /// </summary>
    public static string FishingResearchRequest(FoodPlan plan, Fact<bool> researched)
    {
        if (!researched.TryGetValue(out var known) || known)
        {
            return "";
        }
        return plan.Portfolio.Any(row => row.Channel.Kind == CandidateKind.Fishing && row.Decision == FoodPlanDecision.Open) ? "Fishing" : "";
    }

    /// <summary>
    /// Go <c>FishingRegionID</c>: independent of zone identity: multiple zones in the same body must be
    /// deduplicated before they reach the nutrition ledger.
    /// </summary>
    public static string FishingRegionId(Cell root) => string.Create(CultureInfo.InvariantCulture, $"water-{root.X}-{root.Z}");
}
