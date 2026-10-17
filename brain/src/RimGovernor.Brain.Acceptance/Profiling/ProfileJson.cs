using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance;

/// <summary>One <c>gh run list --json databaseId</c> row.</summary>
internal sealed record GhRun
{
    public long DatabaseId { get; init; }
}

/// <summary>One <c>gh issue list --json number,title</c> row.</summary>
internal sealed record GhIssue
{
    public long Number { get; init; }

    public string Title { get; init; } = "";
}

/// <summary>
/// A record's <c>at</c>: written round-trip ("O", seven fractional digits), read leniently, so an empty or unparsable
/// value reads as the default instant.
/// </summary>
internal sealed class RoundtripInstantConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String
            && reader.GetString() is { Length: > 0 } text
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : default;

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString("O", CultureInfo.InvariantCulture));
    }
}

/// <summary>The profile-capture and profile-compare documents: the -json stats, the nightly record and gh's listings.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ProfileSummary))]
[JsonSerializable(typeof(PerfRecord))]
[JsonSerializable(typeof(Fx.ProfileEquality))]
[JsonSerializable(typeof(List<GhRun>))]
[JsonSerializable(typeof(List<GhIssue>))]
internal sealed partial class ProfileJsonContext : JsonSerializerContext;
