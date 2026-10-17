using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// Winter survival (#251, the acceptance #229 named): the food runway across the first non-growing day of the
/// baseline tile (Go <c>sustained/winter.go</c>).
/// </summary>
internal static class SustainedWinterCase
{
    /// <summary>
    /// The sample window in game ticks: half a game day, opening six hours before the tile's first non-growing day so
    /// the reviews straddle the seasonal flip (the harvest gap changes from the phased-in coming winter to the wait
    /// until growth resumes) and run six hours into the non-growing stretch (about 100 ticks/s under peer load fits
    /// the wall ceiling).
    /// </summary>
    internal const ulong WinterWindowTicks = 30000;

    /// <summary>The environment variable that overrides <see cref="WinterWindowTicks"/> with a tick count (the whole 30-day winter of the baseline tile is 1800000).</summary>
    internal const string WinterWindowTicksEnv = "RIMGOVERNOR_ACCEPT_WINTER_TICKS";

    // Where the fixture parks the calendar: inside the growing season with the frost this many hours on, so one day's
    // window covers both sides of it.
    private const int WinterHoursBeforeFrost = 6;

    /// <summary><see cref="WinterWindowTicksEnv"/> when set and valid, else <see cref="WinterWindowTicks"/>.</summary>
    internal static ulong WinterWindow()
    {
        var raw = Environment.GetEnvironmentVariable(WinterWindowTicksEnv);
        if (!string.IsNullOrEmpty(raw) && ulong.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0)
        {
            return n;
        }

        return WinterWindowTicks;
    }

    internal static AcceptanceCase Case() => new()
    {
        Name = "sustained/winter",
        Tier = CaseTier.Nightly,
        Scope = "Winter survival (#251, the acceptance #229 named): on the " + SustainedFoodCase.BaselineSave + " save moved to the last hours of its growing " +
            "season (test/winter_prepare) with a larder stocked to the seasonal food target the calendar derives (test/winter_stock), " +
            "the food/supply Need's families run without a player target and every rounds through the window reads a food " +
            "runway at or above the seasonal FoodMinDays, on both sides of the tile's first non-growing day.",
        Start = new FixtureStart
        {
            Op = "test/winter_prepare",
            Args = new JsonObject { ["hoursBeforeFrost"] = WinterHoursBeforeFrost },
            On = new SaveStart { Name = SustainedFoodCase.BaselineSave },
        },
        Keep = [NeedDef.Food.Value],
        Serve = SustainedFoodCase.Spec("sustained-winter"),
        Budget = SustainedFoodCase.Window() + TimeSpan.FromMinutes(7),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        report["fixture"] = s.Prepared?.DeepClone();
        _ = await SustainedFood.ObserveAsync(
            ObservedSession.Of(s),
            new SustainedObservation
            {
                Watch = new WatchConfig { Watch = SustainedFoodCase.Window(), Window = WinterWindow(), Poll = TimeSpan.FromSeconds(10) },
                Prepare = (h, r, ct) => StockWinterLarder(h, s.Identity, s.Prepared ?? [], r, ct),
                Audit = (_, r, _) =>
                {
                    AuditWinter(s.Config.Output, r);
                    return Task.CompletedTask;
                },
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static string Fixed2(double value) => value.ToString("F2", CultureInfo.InvariantCulture);

    private static string Num(double value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads the shifted calendar off the typed colony facts, derives the seasonal food policy the reviews will hold
    /// the colony to, and stocks the larder up to its FoodTargetDays at the colony's own nutrition demand: the
    /// precondition of a colony that filled its larder before the frost, so the window tests the winter and not the
    /// summer. The census's non-growing stretch must be the fixture's own walk to re-entry, and the last growing day's
    /// thresholds must already be the winter's.
    /// </summary>
    private static async Task StockWinterLarder(Harness h, JsonObject identity, JsonObject prepared, Report report, CancellationToken cancellationToken)
    {
        async Task<JsonObject> ReadFacts(string label)
        {
            var facts = await h.WireAsync(
                label,
                "observations_read_colony_facts",
                new JsonObject { ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() } },
                cancellationToken).ConfigureAwait(false);
            return Harness.Outcome(facts, "observed").Value;
        }

        var observed = await ReadFacts("colony-facts").ConfigureAwait(false);
        if (observed["foodClimate"].AsMap() is not { } climate)
        {
            throw new AcceptanceException("colony facts carry no foodClimate");
        }

        var calendar = new RimGovernor.Brain.Policy.Calendar
        {
            Season = climate["season"].AsString(),
            DayOfYear = (long)climate["dayOfYear"].AsNumber(),
            GrowingDays = climate["growingDays"].AsNumber(),
            GrowingDaysRemaining = climate["growingDaysRemaining"].AsNumber(),
            GrowingDaysUntil = climate["growingDaysUntil"].AsNumber(),
            NonGrowingDays = climate["nonGrowingDays"].AsNumber(),
            Sowing = climate["sowingNow"].AsBool() == true,
        };
        report["calendar"] = new JsonObject
        {
            ["season"] = calendar.Season,
            ["day_of_year"] = calendar.DayOfYear,
            ["growing_days"] = calendar.GrowingDays,
            ["growing_days_remaining"] = calendar.GrowingDaysRemaining,
            ["growing_days_until"] = calendar.GrowingDaysUntil,
            ["non_growing_days"] = calendar.NonGrowingDays,
            ["sowing"] = calendar.Sowing,
        };
        if (!calendar.Valid() || calendar.GrowingDaysRemaining != 1 || calendar.GrowingDaysUntil != 0)
        {
            throw new AcceptanceException($"fixture calendar is not the last growing day: {calendar}");
        }

        var nonGrowing = prepared["nonGrowingDays"].AsNumber();
        if (nonGrowing <= 0 || nonGrowing >= PolicyFunctions.YearDays)
        {
            throw new AcceptanceException($"fixture reports no non-growing stretch: {prepared["nonGrowingDays"].ToJson()}");
        }

        if (calendar.NonGrowingDays != nonGrowing)
        {
            throw new AcceptanceException($"the census's non-growing stretch {Num(calendar.NonGrowingDays)} is not the fixture's walk to re-entry {Num(nonGrowing)}");
        }

        // The larder is sized on the thresholds the winter reviews will hold it to: with the frost in, the harvest gap
        // is the wait to re-entry plus a harvest cycle, and the last growing day already phases the same stretch in
        // fully, so the two agree.
        var winter = calendar with { GrowingDaysRemaining = 0, GrowingDaysUntil = nonGrowing, Sowing = false };
        var noConditions = Fact.Unknown<EquatableArray<DisasterCondition>>();
        var before = PolicyFunctions.DefaultRoundsPolicy().Seasonal(Fact.Known(calendar), noConditions);
        var seasonal = PolicyFunctions.DefaultRoundsPolicy().Seasonal(Fact.Known(winter), noConditions);
        if (Math.Abs(before.FoodMinDays - seasonal.FoodMinDays) > 1e-9 || Math.Abs(before.FoodTargetDays - seasonal.FoodTargetDays) > 1e-9)
        {
            throw new AcceptanceException(
                $"seasonal food thresholds jump at the frost: {Fixed2(before.FoodMinDays)}/{Fixed2(before.FoodTargetDays)} on the last growing day, {Fixed2(seasonal.FoodMinDays)}/{Fixed2(seasonal.FoodTargetDays)} on the first non-growing day");
        }

        // The review's FoodDays is policy.ForecastFood over the combined supply: the colony's pets compete for the
        // same stock, so the larder is sized on every consumer's demand, not the colonists' alone.
        var human = observed["nutritionPerDay"].AsNumber();
        var demand = 0.0;
        foreach (var raw in observed["forecast"].Member("observed").Member("combinedFoodSupply").Member("consumers").AsSlice())
        {
            demand += raw.Member("nutritionPerDay").AsNumber();
        }

        if (demand < human)
        {
            throw new AcceptanceException($"the forecast's combined food supply names less demand ({Num(demand)}/day) than the colonists ({Num(human)}/day)");
        }

        if (demand <= 0)
        {
            throw new AcceptanceException($"colony facts report no nutrition demand: {observed["nutritionPerDay"].ToJson()}");
        }

        var held = observed["foodNutrition"].AsNumber();
        var nutrition = Math.Max(0, (seasonal.FoodTargetDays * demand) - held);
        var larder = new JsonObject
        {
            ["food_min_days"] = seasonal.FoodMinDays,
            ["food_target_days"] = seasonal.FoodTargetDays,
            ["last_growing_day_food_min_days"] = before.FoodMinDays,
            ["last_growing_day_food_target_days"] = before.FoodTargetDays,
            ["non_growing_days"] = nonGrowing,
            ["nutrition_per_day"] = demand,
            ["colonist_nutrition_per_day"] = human,
            ["nutrition_held_before"] = held,
            ["nutrition_stocked"] = nutrition,
        };
        report["larder"] = larder.DeepClone();
        if (nutrition <= 0)
        {
            return;
        }

        var stocked = await h.CallAsync("winter-stock", "test/winter_stock", new JsonObject { ["nutrition"] = Math.Ceiling(nutrition) }, cancellationToken).ConfigureAwait(false);
        if (stocked["success"].AsBool() != true)
        {
            throw new AcceptanceException($"test/winter_stock refused: {stocked["reason"].ToJson()}");
        }

        larder["stock"] = stocked.DeepClone();
        var after = await ReadFacts("colony-facts-after").ConfigureAwait(false);
        larder["runway_days_after"] = after["foodRunwayDays"]?.DeepClone();
        report["larder"] = larder.DeepClone();
    }

    /// <summary>One rounds's stored-food reading off the OTLP log ring's rounds_review row.</summary>
    private sealed record WinterSample
    {
        public ulong Revision { get; init; }

        public long Tick { get; init; }

        public double FoodDays { get; init; }

        public bool Known { get; init; }

        public double MinDays { get; init; }

        public double TargetDays { get; init; }

        public double Until { get; init; }

        public double Remaining { get; init; }

        public double NonGrowing { get; init; }

        public JsonObject ToJson() => new()
        {
            ["revision"] = Revision,
            ["tick"] = Tick,
            ["food_days"] = FoodDays,
            ["food_days_known"] = Known,
            ["food_min_days"] = MinDays,
            ["food_target_days"] = TargetDays,
            ["growing_days_until"] = Until,
            ["growing_days_remaining"] = Remaining,
            ["non_growing_days"] = NonGrowing,
        };
    }

    /// <summary>
    /// Reads every rounds the service committed off the OTLP log ring and holds each one's food runway to the
    /// seasonal minimum it reviewed under; the window must have reached the first non-growing day so the seasonal
    /// flip is covered.
    /// </summary>
    private static void AuditWinter(string output, Report report)
    {
        IReadOnlyList<LogRow> rows;
        try
        {
            rows = ServiceLogs.ReadLogs(ServiceLogs.ServiceLogsPath(output));
        }
        catch (IOException e)
        {
            throw new AcceptanceException($"read OTLP log ring: {e.Message}", e);
        }

        var samples = new List<WinterSample>();
        foreach (var row in rows)
        {
            if (row.Kind != "rounds_review")
            {
                continue;
            }

            var payload = row.Payload;
            var sample = new WinterSample
            {
                Revision = (ulong)SustainedRows.Number(payload.GetValueOrDefault("revision")),
                Tick = (long)SustainedRows.Number(payload.GetValueOrDefault("tick")),
                MinDays = SustainedRows.Number(payload.GetValueOrDefault("food_min_days")),
                TargetDays = SustainedRows.Number(payload.GetValueOrDefault("food_target_days")),
                Until = SustainedRows.Number(payload.GetValueOrDefault("growing_days_until")),
                Remaining = SustainedRows.Number(payload.GetValueOrDefault("growing_days_remaining")),
                NonGrowing = SustainedRows.Number(payload.GetValueOrDefault("non_growing_days")),
            };
            if (payload.TryGetValue("food_days", out var days))
            {
                sample = sample with { FoodDays = SustainedRows.Number(days), Known = true };
            }

            samples.Add(sample);
        }

        report["review_samples"] = new JsonArray([.. samples.Select(sample => (JsonNode?)sample.ToJson())]);
        if (samples.Count == 0)
        {
            throw new AcceptanceException("the OTLP log ring holds no rounds_review rows");
        }

        int known = 0, growing = 0, waiting = 0;
        var lowest = double.PositiveInfinity;
        WinterSample? lastGrowing = null;
        WinterSample? firstWaiting = null;
        foreach (var sample in samples)
        {
            if (sample.Until > 0)
            {
                waiting++;
                firstWaiting ??= sample;
            }
            else
            {
                growing++;
                if (firstWaiting is null)
                {
                    lastGrowing = sample;
                }
            }

            if (!sample.Known)
            {
                continue;
            }

            known++;
            lowest = Math.Min(lowest, sample.FoodDays - sample.MinDays);
            if (sample.FoodDays < sample.MinDays)
            {
                throw new AcceptanceException(
                    $"review {sample.Revision} at tick {sample.Tick} read food days {Fixed2(sample.FoodDays)} under the seasonal minimum {Fixed2(sample.MinDays)} (target {Fixed2(sample.TargetDays)}, growing days until {Num(sample.Until)})");
            }
        }

        report["review_summary"] = new JsonObject
        {
            ["reviews"] = samples.Count,
            ["food_days_known"] = known,
            ["growing"] = growing,
            ["waiting"] = waiting,
            ["lowest_margin_days"] = double.IsInfinity(lowest) ? null : lowest,
        };
        if (known == 0)
        {
            throw new AcceptanceException($"no rounds read a food runway across {samples.Count} reviews");
        }

        if (growing == 0 || waiting == 0)
        {
            throw new AcceptanceException(
                $"the reviews did not straddle the first non-growing day (growing {growing}, waiting {waiting} over {samples.Count} reviews; window {report["window"].ToJson()})");
        }

        // The thresholds do not jump at the flip: the last growing review and the first waiting one hold the colony to
        // the same figures.
        if (lastGrowing is not null && firstWaiting is not null)
        {
            report["review_flip"] = new JsonObject { ["last_growing"] = lastGrowing.ToJson(), ["first_waiting"] = firstWaiting.ToJson() };
            if (Math.Abs(lastGrowing.MinDays - firstWaiting.MinDays) > 1e-6 || Math.Abs(lastGrowing.TargetDays - firstWaiting.TargetDays) > 1e-6)
            {
                throw new AcceptanceException(
                    $"seasonal thresholds jumped at the frost: review {lastGrowing.Revision} held {Fixed2(lastGrowing.MinDays)}/{Fixed2(lastGrowing.TargetDays)}, review {firstWaiting.Revision} {Fixed2(firstWaiting.MinDays)}/{Fixed2(firstWaiting.TargetDays)}");
            }
        }
    }
}
