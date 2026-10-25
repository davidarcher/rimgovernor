using System.Globalization;

namespace RimGovernor.Brain.Policy;

public sealed record FishingRequest
{
    public IReadOnlyList<FishingRegionReview> Regions { get; init; } = [];

    public FishingResearch Research { get; init; } = new FishingResearch.Researched();
}

public static class FishingRules
{
    /// <summary>The ordinary game's fraction of a water body's maximum population restored per day. It is a fish count, not nutrition.</summary>
    public const double FishingRegenerationPerDay = 0.025;

    /// <summary>
    /// Estimates sustainable raw nutrition. Cooking conversion is a separate ledger
    /// contribution. A body without a working fisher never certifies a delivery rate; malformed facts throw the same
    /// <see cref="FoodPlanFactsException"/> as SupplyFoodPlan. An empty region set (including Core without
    /// Odyssey) produces no fishing channel.
    /// </summary>
    public static IReadOnlyList<SupplyCandidate> FishingChannels(FishingRequest r)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var channels = new List<SupplyCandidate>();
        foreach (var region in r.Regions)
        {
            var id = FishingRegionId(region.Observed.Root);
            if (!seen.Add(id))
            {
                throw new FoodPlanFactsException();
            }
            var population = region.Observed.Population;
            var maximum = region.Observed.MaxPopulation;
            var nutrition = region.Observed.NutritionPerFish;
            if (!FoodForecastRules.FoodNumber(population) || !FoodForecastRules.FoodNumber(maximum) || !FoodForecastRules.FoodNumber(nutrition) || population > maximum || nutrition == 0)
            {
                throw new FoodPlanFactsException();
            }
            if (region.Crew is FishingCrew.Fishers crew
                && (!FoodForecastRules.FoodNumber(crew.FishPerBatch) || !FoodForecastRules.FoodNumber(crew.WorkTicksPerBatch) || !FoodForecastRules.FoodNumber(crew.CapacityPerDay) || crew.FishPerBatch == 0 || crew.WorkTicksPerBatch == 0))
            {
                throw new FoodPlanFactsException();
            }
            var designated = region.Delivering;
            // With no fisher the catch and its work are unpriced until a crew is read.
            var channel = CandidateRules.FoodCandidate(CandidateKind.Fishing, id, new YieldSupply.Unpriced(), CandidateState.Closed, new CandidateLead.Unscheduled(), new CandidateWork.Unpriced());
            var terms = new List<CandidateTerm> { new("fish_regeneration_fraction", FishingRegenerationPerDay) };
            channel = channel with { Source = $"fish:{region.Observed.Root.X},{region.Observed.Root.Z}", Nearness = new SourceNearness.Measured(region.Observed.NearestDistanceSquared) };
            switch (r.Research)
            {
                case FishingResearch.Researched:
                    channel = channel with { Lead = new CandidateLead.Days(0) };
                    break;
                case FishingResearch.Pending pending:
                    channel = channel with { Lead = new CandidateLead.Days(pending.LeadDays) };
                    designated = false;
                    terms.Add(new CandidateTerm("fishing_research_required", 1));
                    break;
                case FishingResearch.NoProgress:
                    designated = false;
                    terms.Add(new CandidateTerm("fishing_research_required", 1));
                    break;
            }
            if (!region.Reachable || region.Observed.Frozen || population == 0 || maximum == 0)
            {
                // Preserve an explicit explanation without presenting an unusable region as an observed-open source of food.
                designated = false;
                channel = WithFirstYield(channel, 0, 0);
                if (!region.Reachable)
                {
                    terms.Add(new CandidateTerm("fishing_unreachable", 1));
                }
                if (region.Observed.Frozen)
                {
                    terms.Add(new CandidateTerm("fishing_frozen", 1));
                }
            }
            else if (population <= maximum * ZoneCreateConstants.FishingPopulationFloor)
            {
                // Native pauses fishing at or below the floor until the lake regrows: no catch, so the zone is not delivering meanwhile.
                channel = WithFirstYield(channel, 0, 0);
                terms.Add(new CandidateTerm("fishing_below_floor", population / maximum));
            }
            else if (region.Crew is FishingCrew.Fishers fishers)
            {
                // The sustainable average, not today's catch quota. Native population-floor control permits bursts and subsequent recovery.
                var regen = maximum * FishingRegenerationPerDay * nutrition;
                if (!FoodForecastRules.FoodNumber(regen))
                {
                    throw new FoodPlanFactsException();
                }
                var dailyNutrition = Math.Min(regen, fishers.CapacityPerDay);
                var draw = dailyNutrition / nutrition;
                var dailyWork = draw / fishers.FishPerBatch * fishers.WorkTicksPerBatch;
                if (!FoodForecastRules.FoodNumber(dailyNutrition) || !FoodForecastRules.FoodNumber(dailyWork))
                {
                    throw new FoodPlanFactsException();
                }
                channel = WithFirstYield(channel, dailyNutrition, dailyWork);
                terms.Add(new CandidateTerm("fish_draw_per_day", draw));
                terms.Add(new CandidateTerm("fish_per_batch", fishers.FishPerBatch));
            }
            channels.Add(channel with { Terms = [.. terms], State = CandidateRules.FoodState(false, designated) });
        }
        return channels;
    }
    /// <summary>Sets the first yield's per-day rate and the channel's daily labor.</summary>
    private static SupplyCandidate WithFirstYield(SupplyCandidate channel, double perDay, double laborPerDay) =>
        channel.WithNutritionSupply(new YieldSupply.Steady(perDay)) with { Work = new CandidateWork.PerDay(laborPerDay) };


    /// <summary>
    /// Asks the existing research goal to open a selected source. A deferred,
    /// closed or unknown channel cannot change research intent. Empty when nothing is asked.
    /// </summary>
    public static string FishingResearchRequest(FoodPlan plan, FishingResearch research)
    {
        if (research is FishingResearch.Researched)
        {
            return "";
        }
        return plan.Portfolio.Any(row => row.Channel.Kind == CandidateKind.Fishing && row.Decision == FoodPlanDecision.Open) ? "Fishing" : "";
    }

    /// <summary>
    /// Independent of zone identity: multiple zones in the same body must be
    /// deduplicated before they reach the nutrition ledger.
    /// </summary>
    public static string FishingRegionId(Cell root) => string.Create(CultureInfo.InvariantCulture, $"water-{root.X}-{root.Z}");
}
