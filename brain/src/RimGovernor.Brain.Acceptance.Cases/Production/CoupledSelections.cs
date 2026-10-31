using RimGovernor.Brain.Store;
using RimGovernor.Brain.Telemetry;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>What a <c>coupled_selection</c> outcome vector was ranked by: the urgent-zero floor's excess (hunger-zero ticks), the investment
/// and the harm terms, and the investment rows by key and the goals term (#3655).</summary>
internal sealed record CoupledOutcome(double? UrgentZeroExcess, bool InvestmentLogged, double? Investment = null, IReadOnlyList<string>? Unknown = null, double? Harm = null,
    IReadOnlyList<(string Key, double Value)>? Contributions = null, double? Goals = null, double? Thermal = null);

/// <summary>One <c>coupled_selection</c> row: a review's shared portfolio comparison (#3532), with the logged rejected vectors (#3655)
/// and the search's accounting (#3722).</summary>
internal sealed record CoupledSelectionRow(ulong Sequence, string Verdict, IReadOnlyList<string> Tasks, IReadOnlyList<string> RejectedIds, CoupledOutcome Baseline, CoupledOutcome Selected,
    IReadOnlyList<string> SelectedIds, IReadOnlyList<CoupledOutcome> Rejected,
    IReadOnlyList<double> PlannedRooms, IReadOnlyList<CoupledThermalExposure> BaselineExposure, IReadOnlyList<CoupledThermalExposure> SelectedExposure,
    CoupledSearchAccounting Search);

/// <summary>What a <c>coupled_selection</c> comparison spent (#3722): scheduler expansions against the traversal bound, outcome evaluations
/// against their budget, and the mandatory baseline's share of each; and what it skipped (#3724): the subsets and sweep prefixes a proven
/// bound pruned (<see cref="Pruned"/>) and why a bound was unavailable where it was (<see cref="BoundsUnavailable"/>, as
/// <c>&lt;bound&gt;:&lt;reason&gt;</c>). Null when the row did not carry the field.</summary>
internal sealed record CoupledSearchAccounting(double? Expansions, double? Evaluations, double? BaselineEvaluations, double? BaselineExpansions,
    double? EvaluationBudget, double? TraversalBound, double? Pruned = null, IReadOnlyList<string>? BoundsUnavailable = null);

/// <summary>Compact physiological input evidence; endpoint temperatures do not describe intermediate spans.</summary>
internal sealed record CoupledThermalExposure(string Pawn, double? AssessedTicks, double? UnknownTicks, double? ColdDegreeTicks,
    double? InitialTemperature, double? InitialComfortableMin, double? FinalTemperature);

/// <summary>The acceptance reader of the <see cref="TelemetryKinds.CoupledSelection"/> kind.</summary>
internal static class CoupledSelections
{
    /// <summary>The rows the log ring holds after sequence <paramref name="after"/> (a kept game still holds an earlier case's), oldest first.</summary>
    public static IReadOnlyList<CoupledSelectionRow> Of(TypedSession s, ulong after) => [.. s.Telemetry(TelemetryKinds.CoupledSelection).Where(r => r.Sequence > after).Select(Row)];

    /// <summary>The <see cref="TelemetryKinds.PlannedRoomSelection"/> companions after sequence <paramref name="after"/>, oldest first: a
    /// <c>planned_unplaced</c> selection whose <c>coupled_selection</c> vectors passed the payload cap keeps its planned rooms, the ledger
    /// total, investment and thermal loss of the baseline and the selection, and their thermal exposure inputs here (#3828).</summary>
    public static IReadOnlyList<CoupledSelectionRow> PlannedRoomsOf(TypedSession s, ulong after) =>
        [.. s.Telemetry(TelemetryKinds.PlannedRoomSelection).Where(r => r.Sequence > after).Select(PlannedRoomRow)];

    private static CoupledSelectionRow PlannedRoomRow(LogRow row)
    {
        var fields = row.Fields();
        // The companion's flat ledger values are the outcome's value attrs.
        CoupledOutcome Flat(string key) => Outcome(fields.GetValueOrDefault(key) is Dictionary<string, object?> value ? new() { ["value"] = value } : null);
        return new CoupledSelectionRow(row.Sequence, fields.GetValueOrDefault("verdict") as string ?? "", [], [], Flat("baseline"), Flat("selected"),
            (fields.GetValueOrDefault("selected_ids") as List<object?> ?? []).OfType<string>().ToList(), [],
            fields.GetValueOrDefault("planned_rooms") is List<object?> rooms
                ? rooms.OfType<Dictionary<string, object?>>().Select(r => Number(r.GetValueOrDefault("room"))).OfType<double>().ToList() : [],
            Exposure(fields.GetValueOrDefault("baseline_thermal_exposure")), Exposure(fields.GetValueOrDefault("selected_thermal_exposure")),
            new CoupledSearchAccounting(null, null, null, null, null, null));
    }

    internal static CoupledSelectionRow Row(LogRow row)
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
            Outcome(fields.GetValueOrDefault("selected") as Dictionary<string, object?>),
            (fields.GetValueOrDefault("selected_ids") as List<object?> ?? []).OfType<string>().ToList(),
            fields.GetValueOrDefault("rejected") is List<object?> vectors
                ? vectors.OfType<Dictionary<string, object?>>().Select(r => r.GetValueOrDefault("vector") as Dictionary<string, object?>).Where(v => v is not null).Select(Outcome).ToList()
                : [],
            fields.GetValueOrDefault("planned_rooms") is List<object?> rooms
                ? rooms.OfType<Dictionary<string, object?>>().Select(r => Number(r.GetValueOrDefault("room"))).OfType<double>().ToList() : [],
            Exposure(fields.GetValueOrDefault("baseline_thermal_exposure")), Exposure(fields.GetValueOrDefault("selected_thermal_exposure")),
            new CoupledSearchAccounting(Number(fields.GetValueOrDefault("search_expansions")), Number(fields.GetValueOrDefault("search_evaluations")),
                Number(fields.GetValueOrDefault("baseline_evaluations")), Number(fields.GetValueOrDefault("baseline_expansions")),
                Number(fields.GetValueOrDefault("evaluation_budget")), Number(fields.GetValueOrDefault("traversal_bound")),
                Number(fields.GetValueOrDefault("search_pruned")),
                fields.GetValueOrDefault("bounds_unavailable") is IEnumerable<object?> bounds ? bounds.OfType<string>().ToList() : null));
    }

    private static double? Number(object? value) => value switch { double d => d, long l => l, int i => i, _ => null };

    private static List<CoupledThermalExposure> Exposure(object? value) => value is List<object?> rows
        ? rows.OfType<Dictionary<string, object?>>().Select(r => new CoupledThermalExposure(
            r.GetValueOrDefault("pawn") as string ?? "", Number(r.GetValueOrDefault("assessed_ticks")), Number(r.GetValueOrDefault("unknown_ticks")),
            Number(r.GetValueOrDefault("cold_degree_ticks")), Number(r.GetValueOrDefault("initial_temperature")),
            Number(r.GetValueOrDefault("initial_comfortable_min")), Number(r.GetValueOrDefault("final_temperature")))).ToList() : [];

    private static CoupledOutcome Outcome(Dictionary<string, object?>? vector)
    {
        var value = vector?.GetValueOrDefault("value") as Dictionary<string, object?>;
        var floor = (value?.GetValueOrDefault("floors") as Dictionary<string, object?>)?.GetValueOrDefault("urgent_zero") as Dictionary<string, object?>;
        double? excess = floor?.GetValueOrDefault("assessed") is true
            ? floor.GetValueOrDefault("excess") switch { double d => d, long l => l, _ => null }
            : null;
        double? investment = value?.GetValueOrDefault("investment") switch { double d => d, long l => l, _ => null };
        var unknown = value?.GetValueOrDefault("unknown") is List<object?> list ? list.OfType<string>().ToList() : [];
        double? harm = value?.GetValueOrDefault("harm") switch { double d => d, long l => l, _ => null };
        var contributions = value?.GetValueOrDefault("investment_contributions") is List<object?> rows
            ? rows.OfType<Dictionary<string, object?>>().Select(r => (r.GetValueOrDefault("key") as string ?? "",
                r.GetValueOrDefault("value") switch { double d => d, long l => (double)l, _ => double.NaN })).ToList()
            : [];
        double? goals = value?.GetValueOrDefault("goals") switch { double d => d, long l => l, _ => null };
        return new CoupledOutcome(excess, value?.ContainsKey("investment") == true, investment, unknown, harm, contributions, goals, Number(value?.GetValueOrDefault("thermal")));
    }
}
