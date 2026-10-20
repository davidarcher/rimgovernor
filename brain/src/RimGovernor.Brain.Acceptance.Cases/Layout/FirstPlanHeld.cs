using RimGovernor.Brain.Telemetry;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>layout/first-plan-held</c> (#3652): a new colony's first layout plan waits for every station fact with the clock
/// held. The first clock_run row is refused under first_plan, no clock_run row before the layout_plan row that records the
/// plan lets the clock run, and the derive's layout_stations row is the full graph's (no seed_only attr).
/// </summary>
internal static class LayoutFirstPlanHeld
{
    private const string FirstPlan = "first_plan";

    internal static TypedCase Case() => new()
    {
        Name = "layout/first-plan-held",
        Scope = "A served new colony: the first clock_run row is refused under first_plan naming the fact (first_plan_fact), "
            + "no clock_run row lets the clock run before the layout_plan row that records the first plan, and that plan's "
            + "layout_stations row carries no seed_only (#3652).",
        Ticks = 2_500,
        Tier = CaseTier.OnDemand,
        Serve = new ServeSpec { Families = ["work"], Prefix = "first-plan" },
        Run = RunAsync,
    };

    private static string Text(IReadOnlyDictionary<string, object?> fields, string key) => fields.GetValueOrDefault(key) as string ?? "";

    // A row that let the clock run: a Run that resumed it or adjusted a running one.
    private static bool Ran(LogRow row) => Text(row.Fields(), Decision.VerdictKey) is "run" or "adjust";

    private static LogRow? Planned(TypedSession s) =>
        s.Telemetry(TelemetryKinds.LayoutPlan).FirstOrDefault(r => Text(r.Fields(), Decision.VerdictKey) == "planned" && Text(r.Fields(), Decision.TargetKey) == "plan");

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        _ = await s.ServeUntilAsync("first-plan", s.TicksLeft, _ =>
        {
            var plan = Planned(s);
            var ran = plan is not null && s.Telemetry(TelemetryKinds.ClockRun).Any(r => r.Sequence > plan.Sequence && Ran(r));
            return Task.FromResult((plan is null ? "no plan" : $"planned at {plan.Sequence}", ran));
        }, cancellationToken).ConfigureAwait(false);

        var planned = Planned(s) ?? throw new AcceptanceException("no layout_plan row recorded the first plan");
        var runs = s.Telemetry(TelemetryKinds.ClockRun).OrderBy(r => r.Sequence).ToList();
        if (runs is not [var first, ..])
        {
            throw new AcceptanceException("no clock_run row");
        }

        var refused = first.Fields().GetValueOrDefault("refused") as IEnumerable<object?> ?? [];
        if (!refused.Any(r => r as string == FirstPlan) || Text(first.Fields(), TelemetryKinds.ClockRunFirstPlanFactKey).Length == 0)
        {
            throw new AcceptanceException($"the first clock_run row is not held under {FirstPlan} naming its fact: {string.Join(",", first.Fields().Select(kv => $"{kv.Key}={kv.Value}"))}");
        }

        if (runs.FirstOrDefault(r => r.Sequence < planned.Sequence && Ran(r)) is { } early)
        {
            throw new AcceptanceException($"the clock ran (clock_run seq {early.Sequence}, {Text(early.Fields(), Decision.VerdictKey)}) before the first plan was recorded (seq {planned.Sequence})");
        }

        var derive = s.Telemetry(TelemetryKinds.LayoutStations).LastOrDefault(r => r.Sequence < planned.Sequence && r.Fields().GetValueOrDefault("derive") is true)
            ?? throw new AcceptanceException("no derive layout_stations row before the first plan");
        if (derive.Fields().ContainsKey("seed_only"))
        {
            throw new AcceptanceException("the first plan's layout_stations row still carries seed_only");
        }
    }
}
