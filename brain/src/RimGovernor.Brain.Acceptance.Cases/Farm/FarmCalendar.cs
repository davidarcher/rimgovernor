using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using PolicyCalendar = RimGovernor.Brain.Policy.Calendar;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>farm/calendar</c> (Go <c>calendar.go</c>): reads the typed colony facts' growing calendar and holds its
/// seasonal-temperature walks (days remaining, days until, the non-growing stretch) to agree with each other and at the
/// frost: the harvest gap the last growing day phases in is the gap the first non-growing day reads. The report records
/// the seasonal food and wood thresholds a review would derive from it. Read-only: no game orders and no clock advance.
/// </summary>
internal static class FarmCalendar
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "farm/calendar",
        Scope = "The typed colony read's growing calendar (season, day of year, growing period, days until the crop range is left "
            + "and re-entered, the non-growing stretch) is in range and self-consistent, and the harvest gap it phases in meets the gap the first frost day reads; read-only (#229, #317).",
        Start = new LabStart(),
        Quiet = QuietMode.Required,
        Budget = TimeSpan.FromMinutes(5),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static string Describe(PolicyCalendar c) => string.Create(
        CultureInfo.InvariantCulture,
        $"{{Season:{c.Season} DayOfYear:{c.DayOfYear} GrowingDays:{c.GrowingDays} GrowingDaysRemaining:{c.GrowingDaysRemaining} GrowingDaysUntil:{c.GrowingDaysUntil} NonGrowingDays:{c.NonGrowingDays} Sowing:{c.Sowing}}}");

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var h = s.Harness;
        var identityReply = await h.WireAsync("identity", "lifecycle_read_identity", new JsonObject(), cancellationToken).ConfigureAwait(false);
        var (_, loaded) = Harness.Outcome(identityReply, "loaded");
        var identity = loaded["context"].AsMap()?["identity"].AsMap();
        var facts = await h.WireAsync(
            "colony-facts",
            "observations_read_colony_facts",
            new JsonObject { ["scope"] = new JsonObject { ["expectedIdentity"] = identity?.DeepClone() } },
            cancellationToken).ConfigureAwait(false);
        var (_, observed) = Harness.Outcome(facts, "observed");
        foreach (var raw in observed["issues"].AsSlice())
        {
            var issue = raw.AsMap();
            if (issue?["field"].AsString() == "food_climate")
            {
                throw new AcceptanceException($"food_climate unavailable: {issue.ToJson()}");
            }
        }

        var climate = observed["foodClimate"].AsMap()
            ?? throw new AcceptanceException("colony facts carry no foodClimate");
        foreach (var field in new[] { "growingDays", "growingDaysRemaining", "growingDaysUntil", "nonGrowingDays", "sowingNow", "season", "dayOfYear" })
        {
            if (!climate.ContainsKey(field))
            {
                throw new AcceptanceException($"foodClimate lacks {field}: {climate.ToJson()}");
            }
        }

        var calendar = new PolicyCalendar
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
            ["Season"] = calendar.Season,
            ["DayOfYear"] = calendar.DayOfYear,
            ["GrowingDays"] = calendar.GrowingDays,
            ["GrowingDaysRemaining"] = calendar.GrowingDaysRemaining,
            ["GrowingDaysUntil"] = calendar.GrowingDaysUntil,
            ["NonGrowingDays"] = calendar.NonGrowingDays,
            ["Sowing"] = calendar.Sowing,
        };
        if (!calendar.Valid())
        {
            throw new AcceptanceException($"calendar out of range: {Describe(calendar)}");
        }

        // The two walks sample the same daily seasonal temperature: exactly one of them is zero unless the tile never
        // leaves (60/0) or never enters (0/60) the crop range.
        var remaining = calendar.GrowingDaysRemaining;
        var until = calendar.GrowingDaysUntil;
        var year = PolicyFunctions.YearDays;
        var walksAgree = (remaining == year && until == 0)
            || (remaining == 0 && until == year)
            || (remaining > 0 && until == 0)
            || (remaining == 0 && until > 0);
        if (!walksAgree)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"growing walks disagree: remaining={remaining} until={until}"));
        }

        // The non-growing stretch is the same walk: the wait while crops do not grow, a positive stretch while they do
        // unless the tile never leaves the range. The gap the last growing day phases in must be the gap the first
        // non-growing day reads, so the seasonal thresholds do not jump at the frost.
        var stretch = calendar.NonGrowingDays;
        if (until > 0 && stretch != until)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"non-growing stretch {stretch} disagrees with the wait until re-entry {until}"));
        }

        if (until == 0 && remaining < year && stretch <= 0)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"a tile that leaves the crop range in {remaining} days reports no non-growing stretch"));
        }

        if (until == 0 && remaining == year && stretch != 0)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"a tile that never leaves the crop range reports a {stretch}-day non-growing stretch"));
        }

        var noConditions = Fact.Unknown<EquatableArray<DisasterCondition>>();
        var lastGrowing = calendar with { GrowingDaysRemaining = 1, GrowingDaysUntil = 0, Sowing = true };
        var firstFrost = calendar with { GrowingDaysRemaining = 0, GrowingDaysUntil = stretch, Sowing = false };
        var before = PolicyFunctions.HarvestGapDays(Fact.Known(lastGrowing), noConditions).ValueOrDefault;
        var after = PolicyFunctions.HarvestGapDays(Fact.Known(firstFrost), noConditions).ValueOrDefault;
        report["harvest_gap_days_at_frost"] = new JsonObject { ["last_growing_day"] = before, ["first_non_growing_day"] = after };
        if (stretch > 0 && Math.Abs(before - after) > 1e-9)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"harvest gap jumps at the frost: {before} on the last growing day, {after} on the first non-growing day"));
        }

        var gap = PolicyFunctions.HarvestGapDays(Fact.Known(calendar), noConditions).ValueOrDefault;
        var seasonal = PolicyFunctions.DefaultRoundsPolicy().Seasonal(Fact.Known(calendar), noConditions);
        report["harvest_gap_days"] = Math.Round(gap * 100) / 100;
        report["seasonal_thresholds"] = new JsonObject
        {
            ["food_min_days"] = seasonal.FoodMinDays,
            ["food_target_days"] = seasonal.FoodTargetDays,
            ["wood_min"] = seasonal.WoodMin,
            ["wood_target"] = seasonal.WoodTarget,
            ["wood_max"] = seasonal.WoodMax,
        };
        try
        {
            seasonal.Validate();
        }
        catch (DomainException e)
        {
            throw new AcceptanceException($"seasonal policy: {e.Message}", e);
        }
    }
}
