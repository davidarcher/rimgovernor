using System.Globalization;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using static RimGovernor.Brain.Domain.Fact;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>environment/hypothermia</c> (#2797): the native probe of the dash estimator. A colonist held
/// <see cref="Deficit"/> degrees under its own comfortable minimum gains hypothermia, over the budget
/// <see cref="DashEstimateRules.OutdoorBudgetFor"/> gives it to shivering, at the per-interval growth
/// <see cref="DashEstimateRules.PublishedHypothermiaTuning"/> prices; and that tuning equals the loaded game's
/// Verse.HealthTuning constants and the pawn's own safe-range onset. test/environment_temperature holds the sealed room;
/// test/hypothermia_tuning reads the constants.
/// </summary>
internal static class HypothermiaProbe
{
    private const string TuningTool = "test/hypothermia_tuning";

    // Far enough under the comfortable minimum that the per-degree term, not the floor, sets the growth.
    private const double Deficit = 40;

    // The room must sit within this many degrees of the asked deficit at each read.
    private const double DeficitTolerance = 1;

    // The giver steps on the pawn's hash interval, so a window of n intervals holds floor(n) or ceil(n) steps; a pawn
    // ticking at a reduced rate may also apply its last step one read late. Hence one and a half steps of slack.
    private const double StepTolerance = 1.5;

    // The game's constants are floats; the published tuning is their decimal value.
    private const double ConstantTolerance = 1e-6;

    private static readonly FixtureOp<NoArgs, Fx.HypothermiaTuningRead> Tuning = new(
        TuningTool,
        _ => ServedOps.Args(),
        ServedOps.Reply<Fx.HypothermiaTuningRead>(TuningTool));

    internal static TypedCase Case() => new()
    {
        Name = "environment/hypothermia",
        Scope = "Issue #2797, the dash estimator's native probe (epic #2745): a colonist sealed in a roofed room held 40 C under "
            + "its own ComfyTemperatureMin gains hypothermia, over OutdoorBudgetFor's budget to shivering, at "
            + "PublishedHypothermiaTuning's growth per 60-tick interval within 1.5 steps; and that tuning equals Verse.HealthTuning "
            + "(HypothermiaGrowthPerDegreeUnder, MinHypothermiaProgress, HarmfulTemperatureOffset, HediffGiverUpdateInterval) and "
            + "the pawn's own comfortable-to-safe minimum offset.",

        // The budget to shivering at a 40-degree deficit is 1200 ticks; the rest covers the wait's overshoot.
        Ticks = 2400,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = 1 },
        Ops = [TemperatureProbe.HoldTool, TuningTool],
        QuietWorld = true,
        Crew = new Crew { Size = 1 },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var published = DashEstimateRules.PublishedHypothermiaTuning;
        var native = await s.FixtureAsync("tuning", Tuning, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        s.Record("tuning", ServedOps.Json(native));
        Same("HypothermiaGrowthPerDegreeUnder", native.GrowthPerDegreeUnder, published.GrowthPerDegreeUnder);
        Same("MinHypothermiaProgress", native.MinProgress, published.MinProgress);
        Same("HarmfulTemperatureOffset", native.HarmfulOffsetC, published.HarmfulOffsetC);
        Same("the pawn's comfortable-to-safe minimum offset", native.SafeMinOffsetC, published.HarmfulOffsetC);
        if (native.IntervalTicks != DashEstimateRules.HypothermiaInterval)
        {
            throw new AcceptanceException($"HediffGiverUpdateInterval is {native.IntervalTicks}, the estimator steps every {DashEstimateRules.HypothermiaInterval}");
        }

        var held = await s.FixtureAsync("hold", TemperatureProbe.Held, new TemperatureProbe.Hold("cold", Deficit, false), cancellationToken).ConfigureAwait(false);
        var start = await ReadAsync(s, "start", held.Pawn, cancellationToken).ConfigureAwait(false);
        if (!DashEstimateRules.OutdoorBudgetFor(HazardKind.ExtremeCold, Known(start.CellC), Known(start.ComfyMinC), Known(start.Severity),
                DashEstimateRules.HypothermiaShivering, published).TryGetValue(out var budget) || budget.Unbounded || budget.Ticks <= 0)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"OutdoorBudgetFor gives no finite budget at {start.CellC:F2} C (comfy min {start.ComfyMinC:F2}, severity {start.Severity})"));
        }

        await s.RunTicksAsync("cold", budget.Ticks, cancellationToken).ConfigureAwait(false);
        var end = await ReadAsync(s, "end", held.Pawn, cancellationToken).ConfigureAwait(false);

        var growth = published.Growth(start.CellC, start.ComfyMinC);
        var intervals = (double)(end.Tick - start.Tick) / DashEstimateRules.HypothermiaInterval;
        var gained = end.Severity - start.Severity;
        var expected = growth * intervals;
        s.Record("probe", ServedOps.Args(
            ("deficit", Deficit),
            ("cell_c", start.CellC),
            ("comfy_min_c", start.ComfyMinC),
            ("budget_ticks", budget.Ticks),
            ("start_tick", start.Tick),
            ("end_tick", end.Tick),
            ("start_severity", start.Severity),
            ("end_severity", end.Severity),
            ("growth_per_interval", growth),
            ("measured_per_interval", gained / intervals),
            ("expected_gain", expected)));
        if (Math.Abs(gained - expected) > StepTolerance * growth)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture,
                $"hypothermia gained {gained:F5} over {intervals:F2} intervals ({gained / intervals:F6} each) at {start.CellC:F2} C, comfy min {start.ComfyMinC:F2}; the estimator prices {growth:F6} each ({expected:F5})"));
        }
    }

    private sealed record Body(long Tick, double CellC, double ComfyMinC, double Severity);

    // The production pawn read's body stats (work detail): the ambient temperature, comfortable minimum and hypothermia severity the dash
    // planner reads, at the read tick. The room must hold the asked deficit.
    private static async Task<Body> ReadAsync(TypedSession s, string label, string pawn, CancellationToken cancellationToken)
    {
        var read = "read-" + label;
        var reply = await s.CallAsync(read, NativeOps.ListPawns, new ListPawnsRequest { Scope = s.Scope, Filter = new() { Ids = [pawn] } }, cancellationToken).ConfigureAwait(false);
        var observed = reply;
        var state = observed.Pawns.SingleOrDefault(p => p.Pawn.Id == pawn) ?? throw new AcceptanceException($"{read}: pawn {pawn} missing from the pawn read");
        var body = state.Settings.Body;
        if (observed.Context is not { } context)
        {
            throw new AcceptanceException($"{read}: the pawn read carried no tick");
        }
        var (cell, comfyMin, severity) = (body.CellTemperatureC, body.ComfortableMinC, body.Hypothermia);

        if (Math.Abs(comfyMin - cell - Deficit) > DeficitTolerance)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"{read}: the room did not hold: deficit {comfyMin - cell:F2}, asked {Deficit}"));
        }

        return new Body(context.Tick, cell, comfyMin, severity);
    }

    private static void Same(string name, double native, double published)
    {
        if (Math.Abs(native - published) > ConstantTolerance * Math.Max(1, Math.Abs(published)))
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"{name} is {native} in the game, the published tuning has {published}"));
        }
    }
}
