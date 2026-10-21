using RimGovernor.Brain.Store;
using RimGovernor.Brain.Telemetry;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>One <c>colony_model</c> row: a review's tier-2 run (#3531), with each priced skill stock's value per XP (#3663).</summary>
internal sealed record ColonyModelRow(double HorizonDays, double Events, bool Truncated, double? RunwayDay, string Dvdt, string Findings, IReadOnlyList<string> SkillPerXp);

/// <summary>The acceptance reader of the <see cref="TelemetryKinds.ColonyModel"/> kind.</summary>
internal static class ColonyModelReviews
{
    /// <summary>The rows the log ring holds, oldest first.</summary>
    public static IReadOnlyList<ColonyModelRow> Of(TypedSession s) => [.. s.Telemetry(TelemetryKinds.ColonyModel).Select(Row)];

    private static ColonyModelRow Row(LogRow row)
    {
        var fields = row.Fields();
        double? Number(string key) => fields.GetValueOrDefault(key) is double number ? number : null;
        return new ColonyModelRow(
            Number(ColonyModelTelemetry.HorizonKey) ?? -1,
            Number(ColonyModelTelemetry.EventsKey) ?? -1,
            fields.GetValueOrDefault(ColonyModelTelemetry.TruncatedKey) is true,
            Number(ColonyModelTelemetry.RunwayKey),
            fields.GetValueOrDefault(ColonyModelTelemetry.ProjectsKey) as string ?? "",
            fields.GetValueOrDefault(ColonyModelTelemetry.FindingsKey) is List<object?> findings ? string.Join(';', findings) : "",
            fields.GetValueOrDefault(ColonyModelTelemetry.SkillsKey) is List<object?> skills ? [.. skills.OfType<string>()] : []);
    }
}
