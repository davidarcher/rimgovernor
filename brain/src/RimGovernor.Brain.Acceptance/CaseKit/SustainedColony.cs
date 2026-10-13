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
        ["samples"] = Samples,
        ["unreadable"] = Unreadable,
        ["min_food_runway_days"] = SampleJson.Num(MinFoodRunwayDays),
        ["min_food_nutrition"] = SampleJson.Num(MinFoodNutrition),
        ["final_food_runway_days"] = SampleJson.Num(FinalFoodRunwayDays),
        ["first_colonists"] = SampleJson.Num(FirstColonists),
        ["final_colonists"] = SampleJson.Num(FinalColonists),
        ["colonists_lost"] = ColonistsLost,
        ["max_downed"] = MaxDowned,
        ["final_downed"] = FinalDowned,
        ["min_mood_mean"] = SampleJson.Num(MinMoodMean),
        ["final_mood_mean"] = SampleJson.Num(FinalMoodMean),
        ["min_food_runway_tick"] = SampleJson.Num(MinFoodRunwayTick),
    };
}

public static partial class SustainedFood
{
    /// <summary>
    /// Reads the service's live colony census (GET /api/player/colony: food nutrition and runway, colonists, downed,
    /// mood mean, the roster) for one timeline sample, so a sustained run's timeline carries the colony facts the run
    /// is judged by beside the goal state. A failed read is recorded as an error block, never dropped.
    /// </summary>
    public static async Task<JsonObject> SampleColonyAsync(ServiceApiCall apiCall, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(apiCall);
        ServiceApiReply reply;
        try
        {
            reply = await apiCall("GET", "/api/player/colony", null, "", cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            return new JsonObject { ["error"] = e.Message };
        }

        if (reply.Status != 200)
        {
            return new JsonObject { ["error"] = string.Create(CultureInfo.InvariantCulture, $"status {reply.Status}: {reply.Body.ToJson()}") };
        }

        return reply.Body ?? [];
    }

    /// <summary>
    /// Folds every sample's colony block into a <see cref="ColonyOutcome"/>; it never errors, a timeline without
    /// colony blocks yields zero samples.
    /// </summary>
    public static ColonyOutcome DeriveColonyOutcome(IEnumerable<JsonObject> timeline)
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
            if (sample["colony"] is not JsonObject colony)
            {
                continue;
            }

            samples++;
            if (colony.ContainsKey("error"))
            {
                unreadable++;
                continue;
            }

            if (SampleJson.TryFloat(colony["foodRunwayDays"], out var runway))
            {
                if (minRunway is null || runway < minRunway)
                {
                    minRunway = runway;
                    minRunwayTick = SampleJson.Tick(sample["tick"]);
                }

                finalRunway = runway;
            }

            if (SampleJson.TryFloat(colony["foodNutrition"], out var nutrition) && (minNutrition is null || nutrition < minNutrition))
            {
                minNutrition = nutrition;
            }

            if (SampleJson.TryFloat(colony["colonists"], out var count))
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

            if (SampleJson.TryFloat(colony["downed"], out var downed))
            {
                finalDowned = (int)downed;
                maxDowned = Math.Max(maxDowned, finalDowned);
            }

            if (SampleJson.TryFloat(colony["moodMean"], out var mood))
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

    /// <summary>
    /// The mean census food runway over the samples taken in the last <paramref name="span"/> ticks before the final
    /// sample, and how many samples that is. The runway swings as the colony eats and restocks, so a gate that judges
    /// food reads the mean, not one instant. Null when no readable sample carries both a tick and a runway.
    /// </summary>
    public static (double Mean, int Samples)? MeanFoodRunway(IEnumerable<JsonObject> timeline, ulong span)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        var points = new List<(ulong Tick, double Runway)>();
        foreach (var sample in timeline)
        {
            if (sample["colony"] is not JsonObject colony || colony["error"] is not null)
            {
                continue;
            }

            if (SampleJson.TryFloat(sample["tick"], out var tick) && SampleJson.TryFloat(colony["foodRunwayDays"], out var runway))
            {
                points.Add(((ulong)tick, runway));
            }
        }

        if (points.Count == 0)
        {
            return null;
        }

        var lastTick = points[^1].Tick;
        double sum = 0;
        var count = 0;
        foreach (var (tick, runway) in points)
        {
            if (tick + span >= lastTick)
            {
                sum += runway;
                count++;
            }
        }

        return (sum / count, count);
    }
}
