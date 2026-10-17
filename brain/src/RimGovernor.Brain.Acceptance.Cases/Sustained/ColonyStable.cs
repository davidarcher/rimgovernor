using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The nightly fifteen-day colony stability gate (Go <c>sustained/colony_stable.go</c>).</summary>
internal static class SustainedStableCase
{
    // Fixed equivalent of RIMGOVERNOR_ACCEPT_WINDOW_TICKS=900000: a shorter diagnostic environment override must not
    // shorten the nightly gate.
    private const ulong ColonyStableTicks = 900000;

    // The runway an eight-person tribal colony must average over the last StableFoodSpanTicks: the runway swings as
    // the colony eats and restocks, so one end-of-run read is not the gate.
    private const double StableFoodDays = 14.0;
    private const ulong StableFoodSpanTicks = 5 * 60000;

    // At 100 ticks/s the window takes 2.5 hours. The service still enforces its 90-second step stall bound; this
    // ceiling allows the full calendar.
    private static readonly TimeSpan Watch = TimeSpan.FromHours(3);

    internal static AcceptanceCase Case()
    {
        var c = SustainedColonyCase.Colony(
            "sustained/colony-stable",
            QuietMode.Required,
            "Fifteen game days on tribal8 with every routine family and live needs: no colonist lost or dead, no Malnutrition above 0.3, every immunity disease immune or tended, and food days at least FoodTargetDays.");
        return c with
        {
            Tier = CaseTier.Nightly,
            Budget = Watch + TimeSpan.FromMinutes(7),
            Reason = "nightly fifteen-day colony stability gate with live needs and every routine family",
            Run = RunAsync,
        };
    }

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var initial = new List<string>();
        _ = await SustainedFood.ObserveAsync(
            ObservedSession.Of(s),
            new SustainedObservation
            {
                Watch = new WatchConfig
                {
                    Watch = Watch,
                    Window = ColonyStableTicks,
                    Poll = TimeSpan.FromSeconds(10),
                    Concern = PolicyFunctions.FoodSupplyNeed.Value,
                    Extra = SustainedColonyCase.ColonyConcerns,
                    FailFast = new FailFast { Disabled = true },
                },
                Prepare = async (h, report, ct) =>
                {
                    var listed = await Colonists.ListColonistsAsync(h, "stable-initial", false, false, ct).ConfigureAwait(false);
                    foreach (var row in listed)
                    {
                        var id = Colonists.RowID(row);
                        if (id.Length == 0)
                        {
                            throw new AcceptanceException("initial colonist has no id");
                        }

                        initial.Add(id);
                    }

                    report["initial_colonists"] = new JsonArray([.. initial.Select(id => (JsonNode?)JsonValue.Create(id))]);
                    if (initial.Count == 0)
                    {
                        throw new AcceptanceException("no initial colonists observed");
                    }
                },
                Audit = async (h, report, ct) =>
                {
                    var failures = new List<Exception?>();
                    var window = SustainedFoodCase.WindowOf(report);
                    if (window is null || window["reached"].AsBool() != true)
                    {
                        failures.Add(new AcceptanceException($"fifteen-day window not reached: {report["window"].ToJson()}"));
                    }

                    failures.Add(await SustainedFoodCase.CaptureAsync(() =>
                    {
                        SustainedColonyCase.AuditReacquisitions(report);
                        return Task.CompletedTask;
                    }).ConfigureAwait(false));
                    failures.Add(await SustainedFoodCase.CaptureAsync(() => SustainedColonyCase.AuditNutrition(h, report, ct)).ConfigureAwait(false));
                    IReadOnlyList<JsonObject> listed;
                    try
                    {
                        listed = await Colonists.ListColonistsAsync(h, "stable-health", true, true, ct).ConfigureAwait(false);
                    }
                    catch (AcceptanceException e)
                    {
                        failures.Add(e);
                        throw SustainedFoodCase.Join(failures)!;
                    }

                    report["stable_health"] = new JsonArray([.. listed.Select(row => (JsonNode?)row.DeepClone())]);
                    var alive = new Dictionary<string, bool>(StringComparer.Ordinal);
                    foreach (var row in listed)
                    {
                        var id = Colonists.RowID(row);
                        var dead = row["dead"].AsBool();
                        alive[id] = dead is false;
                        if (!alive[id])
                        {
                            failures.Add(new AcceptanceException($"colonist {id} dead or death status unknown"));
                        }

                        if (row["health"].AsMap() is not { } health)
                        {
                            failures.Add(new AcceptanceException($"colonist {id} health missing"));
                            continue;
                        }

                        foreach (var raw in health["hediffs"].AsSlice())
                        {
                            var disease = raw.AsMap() ?? [];
                            var immunizable = disease["immunizable"].AsBool() == true;
                            var immune = disease["fullyImmune"].AsBool() == true;
                            var tended = disease["tended"].AsBool() == true;
                            if (immunizable && !immune && !tended)
                            {
                                failures.Add(new AcceptanceException($"colonist {id} has untreated nonimmune {Colonists.HediffDef(disease)}"));
                            }
                        }
                    }

                    // Corpses can despawn or be buried: a missing initial pawn must fail even when the pawn read no
                    // longer returns the dead pawn.
                    foreach (var id in initial)
                    {
                        if (!alive.GetValueOrDefault(id))
                        {
                            failures.Add(new AcceptanceException($"initial colonist {id} no longer alive on map"));
                        }
                    }

                    failures.Add(AuditStableFood(report));
                    if (SustainedFoodCase.Join(failures) is { } error)
                    {
                        throw new AcceptanceException($"{error.Message}; {StableTimeline(report)}", error);
                    }
                },
            },
            cancellationToken).ConfigureAwait(false);
    }

    // The census locates observed losses and the food trough, not the onset of individual hediffs. Days are one-based
    // relative to the observation window.
    private static string StableTimeline(Report report)
    {
        var timeline = SustainedFoodCase.TimelineOf(report);
        var window = SustainedFoodCase.WindowOf(report);
        if (window is null || window["tick_sampled"].AsBool() != true)
        {
            return "timeline day unavailable (no sampled game tick)";
        }

        var first = window["first_tick"].AsUInt64() ?? 0;
        ulong Day(ulong tick) => ((tick - first) / 60000) + 1;
        var detail = "timeline: no sampled colonist-count loss";
        double previous = 0;
        var known = false;
        foreach (var sample in timeline)
        {
            if (sample["colony"].AsMap() is not { } census || census["error"] is not null || census["colonists"] is null)
            {
                continue;
            }

            var count = census["colonists"].AsNumber();
            var tick = sample["tick"].AsUInt64();
            if (known && count < previous && tick is { } at && at >= first)
            {
                detail = string.Create(CultureInfo.InvariantCulture, $"timeline: first sampled colonist-count loss on day {Day(at)} (tick {at}, {previous} -> {count})");
                break;
            }

            previous = count;
            known = true;
        }

        var outcome = SustainedFood.DeriveColonyOutcome(timeline);
        if (outcome.MinFoodRunwayTick is { } minTick && minTick >= first)
        {
            detail += string.Create(
                CultureInfo.InvariantCulture,
                $"; minimum sampled food runway {outcome.MinFoodRunwayDays} days on day {Day(minTick)} (tick {minTick})");
        }

        return detail;
    }

    private static AcceptanceException? AuditStableFood(Report report)
    {
        var timeline = SustainedFoodCase.TimelineOf(report);
        var mean = SustainedFood.MeanFoodRunway(timeline, StableFoodSpanTicks);
        var meanDays = mean?.Mean ?? 0;
        var samples = mean?.Samples ?? 0;
        report["stable_food"] = new JsonObject { ["mean_food_runway_days"] = meanDays, ["mean_samples"] = samples, ["required_days"] = StableFoodDays };
        if (mean is null || meanDays < StableFoodDays)
        {
            return new AcceptanceException(string.Create(
                CultureInfo.InvariantCulture,
                $"mean food runway {meanDays:F2} days over the last {StableFoodSpanTicks / 60000} days ({samples} samples) below {StableFoodDays} days"));
        }

        return null;
    }
}
