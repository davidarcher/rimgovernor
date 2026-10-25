using System.Globalization;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Telemetry.Otlp;

public sealed record ForecastHandoffRead(double Day, double EndDay, bool Available, IReadOnlyList<string> Gaps);
/// <summary>Selected-plan coverage on the existing coupled-selection decision; never a search trajectory.</summary>
public static class ForecastHandoffReader
{
    public const string Key = "handoff";
    public static Dictionary<string, object?>? Fields(ForecastHandoff? handoff) => handoff is null ? null : new()
    {
        ["day"] = handoff.Day, ["end_day"] = handoff.EndDay,
        ["available"] = handoff.Gaps.Count == 0, ["gaps"] = handoff.Gaps.ToArray(),
        ["nutrition"] = handoff.State?.Nutrition,
        ["state_read"] = handoff.State is not null,
        ["graph_nodes"] = handoff.Graph.Nodes.Count,
        ["active_outfits"] = handoff.ActiveEffects.Sum(c => c.Effect.OutfitChanges.Count),
        ["shelters"] = handoff.Shelters.Count(s => s.CompleteAt.IsKnown),
        ["shelter_mechanics"] = handoff.ShelterPlans.Count,
        ["served_rooms"] = handoff.ServedRooms.Count,
        ["needs_known"] = handoff.Needs.Count(n => n.Level.IsKnown), ["needs"] = handoff.Needs.Count,
        ["completed"] = handoff.Completed.Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.Project}@{c.Day:R}")).ToArray(),
    };

    public static ForecastHandoffRead? Decode(TimelineRecord row)
    {
        if (row.Kind != TelemetryKinds.CoupledSelection.Name
            || row.Payload.GetValueOrDefault(Decision.AttrsKey) is not IReadOnlyDictionary<string, object?> attrs
            || attrs.GetValueOrDefault(Key) is not IReadOnlyDictionary<string, object?> h) return null;
        if (!Number(h.GetValueOrDefault("day"), out var day) || !Number(h.GetValueOrDefault("end_day"), out var end)
            || day < 0 || end < day || h.GetValueOrDefault("available") is not bool available
            || h.GetValueOrDefault("gaps") is not IEnumerable<object?> gaps) return null;
        return new(day, end, available, gaps.OfType<string>().ToArray());
    }
    private static bool Number(object? value, out double number)
    {
        number = value switch { double d => d, long l => l, int i => i, _ => double.NaN };
        return double.IsFinite(number);
    }}
