using RimGovernor.Brain.Store;
using RimGovernor.Brain.Telemetry;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>What a <c>coupled_selection</c> outcome vector was ranked by: the urgent-zero floor's excess (hunger-zero ticks) and the investment term.</summary>
internal sealed record CoupledOutcome(double? UrgentZeroExcess, bool InvestmentLogged, double? Investment = null, IReadOnlyList<string>? Unknown = null);

/// <summary>One <c>coupled_selection</c> row: a review's shared portfolio comparison (#3532).</summary>
internal sealed record CoupledSelectionRow(ulong Sequence, string Verdict, IReadOnlyList<string> Tasks, IReadOnlyList<string> RejectedIds, CoupledOutcome Baseline, CoupledOutcome Selected);

/// <summary>The acceptance reader of the <see cref="TelemetryKinds.CoupledSelection"/> kind.</summary>
internal static class CoupledSelections
{
    /// <summary>The rows the log ring holds after sequence <paramref name="after"/> (a kept game still holds an earlier case's), oldest first.</summary>
    public static IReadOnlyList<CoupledSelectionRow> Of(TypedSession s, ulong after) => [.. s.Telemetry(TelemetryKinds.CoupledSelection).Where(r => r.Sequence > after).Select(Row)];

    private static CoupledSelectionRow Row(LogRow row)
    {
        var fields = row.Fields();
        var tasks = fields.GetValueOrDefault("predicted_tasks") is List<object?> predicted
            ? predicted.OfType<Dictionary<string, object?>>().Select(t => t.GetValueOrDefault("task") as string ?? "").ToList()
            : [];
        return new CoupledSelectionRow(
            row.Sequence,
            fields.GetValueOrDefault("verdict") as string ?? "",
            tasks,
            fields.GetValueOrDefault("rejected") is List<object?> rejected
                ? rejected.OfType<Dictionary<string, object?>>().SelectMany(r => r.GetValueOrDefault("ids") as List<object?> ?? []).OfType<string>().ToList()
                : [],
            Outcome(fields.GetValueOrDefault("baseline") as Dictionary<string, object?>),
            Outcome(fields.GetValueOrDefault("selected") as Dictionary<string, object?>));
    }

    private static CoupledOutcome Outcome(Dictionary<string, object?>? vector)
    {
        var value = vector?.GetValueOrDefault("value") as Dictionary<string, object?>;
        var floor = (value?.GetValueOrDefault("floors") as Dictionary<string, object?>)?.GetValueOrDefault("urgent_zero") as Dictionary<string, object?>;
        double? excess = floor?.GetValueOrDefault("assessed") is true
            ? floor.GetValueOrDefault("excess") switch { double d => d, long l => l, _ => null }
            : null;
        double? investment = value?.GetValueOrDefault("investment") switch { double d => d, long l => l, _ => null };
        var unknown = value?.GetValueOrDefault("unknown") is List<object?> list ? list.OfType<string>().ToList() : [];
        return new CoupledOutcome(excess, value?.ContainsKey("investment") == true, investment, unknown);
    }
}
