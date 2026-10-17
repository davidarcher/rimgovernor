using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// Summarizes the timeline's colony blocks so a sustained run can be judged from result.json alone: the worst food
/// position seen, the colonist count at both ends, how many colonists the count lost along the way (every drop
/// between consecutive readable samples, so a death and a departure both count), the worst downed count and mood
/// mean, and how many samples had no readable census (Go <c>sustainedfood.ColonyOutcome</c>).
/// </summary>
public sealed record ColonyOutcome
{
    public int Samples { get; init; }

    public int Unreadable { get; init; }

    public double? MinFoodRunwayDays { get; init; }

    public double? MinFoodNutrition { get; init; }

    public double? FinalFoodRunwayDays { get; init; }

    public int? FirstColonists { get; init; }

    public int? FinalColonists { get; init; }

    public int ColonistsLost { get; init; }

    public int MaxDowned { get; init; }

    public int FinalDowned { get; init; }

    public double? MinMoodMean { get; init; }

    public double? FinalMoodMean { get; init; }

    /// <summary>The live game tick of the sample that recorded <see cref="MinFoodRunwayDays"/>, for finding the collapse in the OTLP log ring.</summary>
    public ulong? MinFoodRunwayTick { get; init; }

    /// <summary>The outcome in the report's JSON shape (the Go struct's tags).</summary>
    public JsonObject ToJson() => new()
    {
        { "samples", Samples },
        { "unreadable", Unreadable },
        { "min_food_runway_days", MinFoodRunwayDays },
        { "min_food_nutrition", MinFoodNutrition },
        { "final_food_runway_days", FinalFoodRunwayDays },
        { "first_colonists", FirstColonists },
        { "final_colonists", FinalColonists },
        { "colonists_lost", ColonistsLost },
        { "max_downed", MaxDowned },
        { "final_downed", FinalDowned },
        { "min_mood_mean", MinMoodMean },
        { "final_mood_mean", FinalMoodMean },
        { "min_food_runway_tick", MinFoodRunwayTick },
    };
}

public static partial class SustainedFood
{
    /// <summary>
    /// Reads the service's live colony census (GET /api/player/colony: food nutrition and runway, colonists, downed,
    /// mood mean, the roster) for one timeline sample, so a sustained run's timeline carries the colony facts the run
    /// is judged by beside the goal state. A failed read is recorded as an error block, never dropped.
    /// </summary>
    internal static async Task<ColonyRead> SampleColonyAsync(ServiceApiCall apiCall, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(apiCall);
        ServiceApiReply reply;
        try
        {
            reply = await apiCall("GET", "/api/player/colony", null, "", cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            return new ColonyRead { Error = e.Message };
        }

        if (reply.Status != 200)
        {
            return new ColonyRead { Error = string.Create(CultureInfo.InvariantCulture, $"status {reply.Status}: {reply.Body.ToJson()}") };
        }

        PlayerColony? census;
        try
        {
            census = ServiceJson.Colony(reply.Body);
        }
        catch (AcceptanceException)
        {
            // Recorded as the body it is; its facts are unknown.
            census = null;
        }

        return new ColonyRead { Body = reply.Body ?? [], Census = census };
    }

    /// <summary>
    /// Folds every sample's colony block into a <see cref="ColonyOutcome"/>; it never errors, a timeline without
    /// colony blocks yields zero samples.
    /// </summary>
    internal static ColonyOutcome DeriveColonyOutcome(IEnumerable<WatchSample> timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        var samples = 0;
        var unreadable = 0;
        double? minRunway = null;
        ulong? minRunwayTick = null;
        double? finalRunway = null;
        double? minNutrition = null;
        int? firstColonists = null;
        int? finalColonists = null;
        int? lastColonists = null;
        var lost = 0;
        var maxDowned = 0;
        var finalDowned = 0;
        double? minMood = null;
        double? finalMood = null;
        foreach (var sample in timeline)
        {
            samples++;
            if (sample.Colony.Error is not null)
            {
                unreadable++;
                continue;
            }

            if (sample.Colony.Census is not { } census)
            {
                continue;
            }

            if (census.FoodRunwayDays is { } runway)
            {
                if (minRunway is null || runway < minRunway)
                {
                    minRunway = runway;
                    minRunwayTick = sample.Tick;
                }

                finalRunway = runway;
            }

            if (census.FoodNutrition is { } nutrition && (minNutrition is null || nutrition < minNutrition))
            {
                minNutrition = nutrition;
            }

            if (census.Colonists is { } count)
            {
                var colonists = (int)count;
                firstColonists ??= colonists;
                if (lastColonists is { } last && colonists < last)
                {
                    lost += last - colonists;
                }

                lastColonists = colonists;
                finalColonists = colonists;
            }

            if (census.Downed is { } downed)
            {
                finalDowned = downed;
                maxDowned = Math.Max(maxDowned, finalDowned);
            }

            if (census.MoodMean is { } mood)
            {
                if (minMood is null || mood < minMood)
                {
                    minMood = mood;
                }

                finalMood = mood;
            }
        }

        return new ColonyOutcome
        {
            Samples = samples,
            Unreadable = unreadable,
            MinFoodRunwayDays = minRunway,
            MinFoodNutrition = minNutrition,
            FinalFoodRunwayDays = finalRunway,
            FirstColonists = firstColonists,
            FinalColonists = finalColonists,
            ColonistsLost = lost,
            MaxDowned = maxDowned,
            FinalDowned = finalDowned,
            MinMoodMean = minMood,
            FinalMoodMean = finalMood,
            MinFoodRunwayTick = minRunwayTick,
        };
    }
}
