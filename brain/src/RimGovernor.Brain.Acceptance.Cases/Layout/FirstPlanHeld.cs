using RimGovernor.Brain.Telemetry;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>layout/first-plan-held</c> (#3652): a new colony's first layout plan waits for every station fact with the clock
/// held. The serve's first clock_run row is refused under first_plan, no clock_run row before the layout_plan row that
/// records the plan lets the clock run, and the derive's layout_stations row is the full graph's (no seed_only attr). The
/// game's log ring outlives the game, so only rows written after the serve began are this colony's.
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

    private static readonly TelemetryKind[] Read = [TelemetryKinds.ClockRun, TelemetryKinds.LayoutPlan, TelemetryKinds.LayoutStations];

    private static string Text(IReadOnlyDictionary<string, object?> fields, string key) => fields.GetValueOrDefault(key) as string ?? "";

    // A row that let the clock run: a Run that resumed it or adjusted a running one.
    private static bool Ran(LogRow row) => Text(row.Fields(), Decision.VerdictKey) is "run" or "adjust";

    // The rows of kind the serve wrote: those past the last row of a read kind before it began (earlier games' rows stay).
    private static IEnumerable<LogRow> Since(TypedSession s, TelemetryKind kind, ulong floor) => s.Telemetry(kind).Where(r => r.Sequence > floor);

    private static LogRow? Planned(TypedSession s, ulong floor) =>
        Since(s, TelemetryKinds.LayoutPlan, floor).FirstOrDefault(r => Text(r.Fields(), Decision.VerdictKey) == "planned" && Text(r.Fields(), Decision.TargetKey) == "plan");

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var floor = Read.SelectMany(s.Telemetry).Select(r => r.Sequence).DefaultIfEmpty(0UL).Max();
        _ = await s.ServeUntilAsync("first-plan", s.TicksLeft, _ =>
        {
            var plan = Planned(s, floor);
            var ran = plan is not null && Since(s, TelemetryKinds.ClockRun, floor).Any(r => r.Sequence > plan.Sequence && Ran(r));
            return Task.FromResult((plan is null ? "no plan" : $"planned at {plan.Sequence}", ran));
        }, cancellationToken).ConfigureAwait(false);

        var planned = Planned(s, floor) ?? throw new AcceptanceException("no layout_plan row recorded the first plan");
        var runs = Since(s, TelemetryKinds.ClockRun, floor).OrderBy(r => r.Sequence).ToList();
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

        var derive = Since(s, TelemetryKinds.LayoutStations, floor).LastOrDefault(r => r.Sequence < planned.Sequence && r.Fields().GetValueOrDefault("derive") is true)
            ?? throw new AcceptanceException("no derive layout_stations row before the first plan");
        if (derive.Fields().ContainsKey("seed_only"))
        {
            throw new AcceptanceException("the first plan's layout_stations row still carries seed_only");
        }

        // The bed merge's heat and sleeper prices are required inputs (#3747): the plan recorded is the chain's advice
        // once they are read, never one made over a skipped (required_fact) chain.
        if (Text(derive.Fields(), Decision.VerdictKey) != "advised")
        {
            throw new AcceptanceException($"the first plan's derive layout_stations row is {Text(derive.Fields(), Decision.VerdictKey)}/{Text(derive.Fields(), Decision.ReasonKey)}, not advised");
        }
    }
}
