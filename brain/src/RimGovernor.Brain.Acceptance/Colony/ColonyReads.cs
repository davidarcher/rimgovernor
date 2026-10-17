using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RimGovernor.Brain.Acceptance;

// What acceptance colony reads back from JSON it did not build (#3410): the config's game section and the keep-alive's
// stop record in the report. Each record carries only the fields the colony reads.

/// <summary>The launcher config's <c>games.&lt;id&gt;</c> section: the game copy the sidecars stamp.</summary>
internal sealed record ColonyGameSection
{
    [JsonPropertyName("workingDir")]
    public string? WorkingDir { get; init; }
}

/// <summary>The keep-alive's stop record (the report's <c>authority_reacquisitions</c>, written by the service process).</summary>
internal sealed record ColonyReacquisitions
{
    [JsonPropertyName("attempts")]
    public long Attempts { get; init; }

    [JsonPropertyName("reacquired")]
    public long Reacquired { get; init; }

    [JsonPropertyName("generation_advance")]
    public ulong GenerationAdvance { get; init; }

    [JsonPropertyName("generations_over_one")]
    public long GenerationsOverOne { get; init; }
}

[JsonSerializable(typeof(ColonyGameSection))]
[JsonSerializable(typeof(ColonyReacquisitions))]
internal sealed partial class ColonyJsonContext : JsonSerializerContext
{
    /// <summary>The game section's working directory; "" when absent or not a string.</summary>
    internal static string WorkingDir(JsonObject game)
    {
        try
        {
            return game.Deserialize(Default.ColonyGameSection)?.WorkingDir ?? "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    /// <summary>The report's stop record; null when absent or not one.</summary>
    internal static ColonyReacquisitions? Reacquisitions(JsonNode? keep)
    {
        if (keep is not JsonObject record)
        {
            return null;
        }

        try
        {
            return record.Deserialize(Default.ColonyReacquisitions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
