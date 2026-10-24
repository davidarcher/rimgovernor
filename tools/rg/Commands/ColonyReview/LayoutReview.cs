using System.Globalization;
using System.Text.Json.Serialization;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Telemetry;
using RimGovernor.Brain.Telemetry.Otlp;
using Decision = RimGovernor.Brain.Telemetry.Decision;

namespace RimGovernor.Tools.Commands.ColonyReview;

// The colony review's layout section (#3751): what the run's layout plans (the journal's colony_layout_plans), its
// replan, progress and bedroom-step rows (the service's OTLP log ring) and the timeline's housing census say about the
// layout, per game day and over the run, and the thresholds that fail the run. Pure: the loader hands it the rows.

/// <summary>One plan the journal recorded, at its tick.</summary>
internal sealed record PlanRow(int Tick, LayoutPlan Plan);

/// <summary>One timeline sample's housing census: the live colonists and those owning a bed in an enclosed Bedroom or Barracks.</summary>
internal sealed record HousingRow(int Tick, int Colonists, int Housed);

/// <summary>What the layout section is computed from. A source the run did not leave is null (its metrics are unknown).</summary>
internal sealed record LayoutInputs
{
    public IReadOnlyList<PlanRow>? Plans { get; init; }

    public IReadOnlyList<TimelineRecord>? Records { get; init; }

    public IReadOnlyList<HousingRow> Housing { get; init; } = [];

    /// <summary>The start's biome: the housed-by threshold holds on <see cref="LayoutReview.TemperateBiome"/> only.</summary>
    public string Biome { get; init; } = "";

    /// <summary>Why a source is missing, shown on the page.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>One game day of the layout section; a value the day has no row for is null.</summary>
internal sealed record LayoutDay
{
    [JsonPropertyName("day")]
    public int Day { get; init; }

    [JsonPropertyName("replans")]
    public int? Replans { get; init; }

    [JsonPropertyName("resited_started")]
    public int? ResitedStarted { get; init; }

    [JsonPropertyName("resited_unstarted")]
    public int? ResitedUnstarted { get; init; }

    [JsonPropertyName("planned")]
    public int? Planned { get; init; }

    [JsonPropertyName("built")]
    public int? Built { get; init; }

    /// <summary>The most open rings any review of the day held.</summary>
    [JsonPropertyName("open_max")]
    public int? OpenMax { get; init; }

    [JsonPropertyName("builders")]
    public int? Builders { get; init; }

    /// <summary>The day's last plan: links over walled rooms.</summary>
    [JsonPropertyName("links_per_room")]
    public double? LinksPerRoom { get; init; }

    [JsonPropertyName("bedroom_area")]
    public double? BedroomArea { get; init; }

    [JsonPropertyName("colonists")]
    public int? Colonists { get; init; }

    [JsonPropertyName("housed")]
    public int? Housed { get; init; }

    [JsonPropertyName("refusals")]
    public IReadOnlyDictionary<string, int> Refusals { get; init; } = new SortedDictionary<string, int>(StringComparer.Ordinal);
}

/// <summary>One plan tick: the plan's walled rooms, their links and the mean bedroom/barracks interior.</summary>
internal sealed record LayoutPlanPoint
{
    [JsonPropertyName("tick")]
    public int Tick { get; init; }

    [JsonPropertyName("rooms")]
    public int Rooms { get; init; }

    [JsonPropertyName("links")]
    public int Links { get; init; }

    [JsonPropertyName("bedroom_area")]
    public double? BedroomArea { get; init; }
}

/// <summary>One review's progress row: planned, started, built and open rooms against the builders.</summary>
internal sealed record LayoutProgressPoint
{
    [JsonPropertyName("tick")]
    public int Tick { get; init; }

    [JsonPropertyName("planned")]
    public int Planned { get; init; }

    [JsonPropertyName("started")]
    public int Started { get; init; }

    [JsonPropertyName("built")]
    public int Built { get; init; }

    [JsonPropertyName("open")]
    public int Open { get; init; }

    [JsonPropertyName("builders")]
    public int Builders { get; init; }
}

/// <summary>The layout section of run.json: the run's totals, the daily series and the series per plan tick and per review.</summary>
internal sealed record LayoutSection
{
    /// <summary>Replans the run adopted (verdict replanned or kept; skipped and failed runs are not replans).</summary>
    [JsonPropertyName("replans")]
    public int? Replans { get; init; }

    [JsonPropertyName("replans_per_day")]
    public double? ReplansPerDay { get; init; }

    [JsonPropertyName("resited_started")]
    public int? ResitedStarted { get; init; }

    [JsonPropertyName("resited_unstarted")]
    public int? ResitedUnstarted { get; init; }

    /// <summary>Rooms re-sited (tile_sited_again) per adopted replan.</summary>
    [JsonPropertyName("resites_per_replan")]
    public double? ResitesPerReplan { get; init; }

    [JsonPropertyName("max_open")]
    public int? MaxOpen { get; init; }

    [JsonPropertyName("bedroom_area")]
    public double? BedroomArea { get; init; }

    /// <summary>The first game day every colonist owned a bed in an enclosed Bedroom or Barracks.</summary>
    [JsonPropertyName("housed_day")]
    public int? HousedDay { get; init; }

    /// <summary>The bedroom step's refusals over the run, by reason.</summary>
    [JsonPropertyName("refusals")]
    public IReadOnlyDictionary<string, int> Refusals { get; init; } = new SortedDictionary<string, int>(StringComparer.Ordinal);

    [JsonPropertyName("days")]
    public IReadOnlyList<LayoutDay> Days { get; init; } = [];

    [JsonPropertyName("plans")]
    public IReadOnlyList<LayoutPlanPoint> Plans { get; init; } = [];

    [JsonPropertyName("progress")]
    public IReadOnlyList<LayoutProgressPoint> Progress { get; init; } = [];

    [JsonPropertyName("notes")]
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>Computes the layout section and its thresholds.</summary>
internal static class LayoutReview
{
    /// <summary>The temperate start the housed-by day is held on (Decisions of #3751): the review's temperate biome.</summary>
    public const string TemperateBiome = RimGovernor.Brain.Acceptance.ColonyReviewCommand.TemperateBiome;

    /// <summary>The last game day a temperate start may house everyone on.</summary>
    public const int HousedByDay = 6;

    /// <summary>The bedroom step's refusal reasons the section always lists, even at zero.</summary>
    public static readonly IReadOnlyList<string> NamedRefusals = [Cause.NoSpace.Value, Cause.AwaitingPlan.Value, Cause.FieldUnavailable.Value];

    // Threshold names and their fail reasons.
    public const string StartedResites = "layout_started_resites";
    public const string OpenRings = "layout_open_rings";
    public const string LinksPerRoom = "layout_links_per_room";
    public const string HousedBy = "layout_housed_by_day";
    public const string ReasonStartedResited = "started_room_resited";
    public const string ReasonOpenOverBuilders = "open_rings_over_builders";
    public const string ReasonUnlinked = "rooms_unlinked";
    public const string ReasonNotHoused = "not_housed_by_day_6";

    private const int TicksPerDay = 60000;
    private const string Refused = "refused";
    private const string Skipped = "skipped";
    private const string Failed = "failed";
    private const string ConcernKey = "concern";
    private const string RoomsKey = "rooms";
    private const string RoomReasonKey = "reason";
    private const string RoomStartedKey = "started";
    private const string TickKey = "tick";

    private sealed record Replan(int Tick, int Started, int Unstarted, bool StartedKnown);

    private sealed record Refusal(int Tick, string Reason);

    /// <summary>The game day of a tick (day 1 is ticks 0..59999).</summary>
    public static int DayOf(int tick) => (tick / TicksPerDay) + 1;

    /// <summary>The section over <paramref name="inputs"/>.</summary>
    public static LayoutSection Compute(LayoutInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var plans = (inputs.Plans ?? []).OrderBy(p => p.Tick).Select(Point).ToList();
        var replans = inputs.Records is null ? null : ReplansOf(inputs.Records);
        var progress = inputs.Records is null ? null : ProgressOf(inputs.Records);
        var refusals = inputs.Records is null ? null : RefusalsOf(inputs.Records);
        var housing = inputs.Housing.OrderBy(h => h.Tick).ToList();

        var ticks = plans.Select(p => p.Tick)
            .Concat(replans?.Select(r => r.Tick) ?? [])
            .Concat(progress?.Select(p => p.Tick) ?? [])
            .Concat(refusals?.Select(r => r.Tick) ?? [])
            .Concat(housing.Select(h => h.Tick))
            .ToList();
        var lastDay = ticks.Count == 0 ? 0 : DayOf(ticks.Max());
        var days = new List<LayoutDay>();
        for (var day = 1; day <= lastDay; day++)
        {
            bool On(int tick) => DayOf(tick) == day;
            var plan = plans.LastOrDefault(p => DayOf(p.Tick) <= day);
            var dayReplans = replans?.Where(r => On(r.Tick)).ToList();
            var dayProgress = progress?.Where(p => On(p.Tick)).ToList();
            var lastProgress = dayProgress?.LastOrDefault();
            var dayHousing = housing.LastOrDefault(h => On(h.Tick));
            days.Add(new LayoutDay
            {
                Day = day,
                Replans = dayReplans?.Count,
                ResitedStarted = dayReplans?.Sum(r => r.Started),
                ResitedUnstarted = dayReplans?.Sum(r => r.Unstarted),
                Planned = lastProgress?.Planned,
                Built = lastProgress?.Built,
                OpenMax = dayProgress is { Count: > 0 } ? dayProgress.Max(p => p.Open) : null,
                Builders = lastProgress?.Builders,
                LinksPerRoom = plan is { Rooms: > 0 } ? (double)plan.Links / plan.Rooms : null,
                BedroomArea = plan?.BedroomArea,
                Colonists = dayHousing?.Colonists,
                Housed = dayHousing?.Housed,
                Refusals = Count(refusals?.Where(r => On(r.Tick)).Select(r => r.Reason) ?? []),
            });
        }

        var runDays = Math.Max(lastDay, 1);
        int? resites = replans?.Sum(r => r.Started + r.Unstarted);
        return new LayoutSection
        {
            Replans = replans?.Count,
            ReplansPerDay = replans is null ? null : (double)replans.Count / runDays,
            ResitedStarted = replans?.Sum(r => r.Started),
            ResitedUnstarted = replans?.Sum(r => r.Unstarted),
            ResitesPerReplan = replans is { Count: > 0 } ? (double)resites!.Value / replans.Count : null,
            MaxOpen = progress is { Count: > 0 } ? progress.Max(p => p.Open) : null,
            BedroomArea = plans.LastOrDefault()?.BedroomArea,
            HousedDay = housing.FirstOrDefault(h => h.Colonists > 0 && h.Housed >= h.Colonists) is { } housed ? DayOf(housed.Tick) : null,
            Refusals = Count(refusals?.Select(r => r.Reason) ?? []),
            Days = days,
            Plans = plans,
            Progress = progress ?? [],
            Notes = inputs.Notes,
        };
    }

    /// <summary>
    /// The section's thresholds; each fails the run (Decisions of #3751). A threshold whose rows the run did not leave is
    /// unknown, never a pass.
    /// </summary>
    public static IReadOnlyList<ScoreThreshold> Thresholds(LayoutSection s, LayoutInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(inputs);
        var output = new List<ScoreThreshold>();

        // Rooms re-sited by a replan that had started: must be none.
        var startedKnown = inputs.Records is not null && ReplansOf(inputs.Records) is var rs && (rs.Count == 0 || rs.Any(r => r.StartedKnown));
        output.Add(startedKnown && s.ResitedStarted is { } started
            ? Threshold(StartedResites, started, 0, started == 0, ReasonStartedResited, "started rooms a replan re-sited (tile_sited_again)")
            : Unknown(StartedResites, 0, "started rooms a replan re-sited: the log ring has no replan rows with the started attr"));

        // Open rings at every review: at most max(1, builders).
        var worst = s.Progress.OrderByDescending(p => p.Open - Math.Max(1, p.Builders)).FirstOrDefault();
        output.Add(worst is null
            ? Unknown(OpenRings, 1, "open rings against max(1, builders): the log ring has no layout_progress rows")
            : Threshold(OpenRings, worst.Open, Math.Max(1, worst.Builders), worst.Open <= Math.Max(1, worst.Builders), ReasonOpenOverBuilders,
                string.Create(CultureInfo.InvariantCulture, $"the most open rings over max(1, builders) at a review (tick {worst.Tick})")));

        // Links per walled room at every plan tick of two or more rooms: at least (rooms-1)/rooms.
        var linked = s.Plans.Where(p => p.Rooms >= 2).OrderBy(p => ((double)p.Links / p.Rooms) - ((p.Rooms - 1.0) / p.Rooms)).FirstOrDefault();
        output.Add(linked is null
            ? Unknown(LinksPerRoom, 0, "links per room: no plan of two or more walled rooms")
            : Threshold(LinksPerRoom, (double)linked.Links / linked.Rooms, (linked.Rooms - 1.0) / linked.Rooms, linked.Links >= linked.Rooms - 1, ReasonUnlinked,
                string.Create(CultureInfo.InvariantCulture, $"the least-linked plan's links per walled room (tick {linked.Tick}, {linked.Links} links, {linked.Rooms} rooms)")));

        // Everyone housed by day 6 on a temperate start.
        const string housedNote = "the first day every colonist owns a bed in an enclosed Bedroom or Barracks";
        var lastSample = inputs.Housing.Count == 0 ? (int?)null : DayOf(inputs.Housing.Max(h => h.Tick));
        if (inputs.Biome != TemperateBiome)
        {
            output.Add(Unknown(HousedBy, HousedByDay, housedNote + ": held on a temperate start only, this is " + (inputs.Biome.Length == 0 ? "an unknown biome" : inputs.Biome)));
        }
        else if (s.HousedDay is { } day)
        {
            output.Add(Threshold(HousedBy, day, HousedByDay, day <= HousedByDay, ReasonNotHoused, housedNote));
        }
        else if (lastSample is { } last && last > HousedByDay)
        {
            output.Add(Threshold(HousedBy, null, HousedByDay, false, ReasonNotHoused, housedNote + ": never, through day " + last.ToString(CultureInfo.InvariantCulture)));
        }
        else
        {
            output.Add(Unknown(HousedBy, HousedByDay, housedNote + (lastSample is null ? ": the timeline has no housing census" : ": not yet, and the run ended before day 7")));
        }
        return output;
    }

    /// <summary>The adopted replans (not skipped or failed) with their re-sited rooms split by whether they had started; a room without the started attr counts unstarted.</summary>
    private static List<Replan> ReplansOf(IReadOnlyList<TimelineRecord> records)
    {
        var output = new List<Replan>();
        foreach (var r in records.Where(r => r.Kind == TelemetryKinds.LayoutReplanTiled.Name))
        {
            var verdict = Text(r.Payload, Decision.VerdictKey);
            if (verdict is Skipped or Failed || TickOf(r) is not { } tick)
            {
                continue;
            }
            int started = 0, unstarted = 0;
            var known = false;
            foreach (var room in (Field(r, RoomsKey) as List<object?> ?? []).OfType<Dictionary<string, object?>>())
            {
                if (room.TryGetValue(RoomStartedKey, out var flag) && flag is bool)
                {
                    known = true;
                }
                if (Text(room, RoomReasonKey) != LayoutReplanTiledRules.ReasonDiffSited)
                {
                    continue;
                }
                if (flag is true)
                {
                    started++;
                }
                else
                {
                    unstarted++;
                }
            }
            output.Add(new Replan(tick, started, unstarted, known));
        }
        return output;
    }

    private static List<LayoutProgressPoint> ProgressOf(IReadOnlyList<TimelineRecord> records) =>
        [.. records.Where(r => r.Kind == TelemetryKinds.LayoutProgress.Name && TickOf(r) is not null)
            .Select(r => new LayoutProgressPoint
            {
                Tick = TickOf(r)!.Value,
                Planned = Int(Field(r, LayoutProgressTelemetry.PlannedKey)),
                Started = Int(Field(r, LayoutProgressTelemetry.StartedKey)),
                Built = Int(Field(r, LayoutProgressTelemetry.BuiltKey)),
                Open = (Field(r, LayoutProgressTelemetry.OpenKey) as List<object?>)?.Count ?? 0,
                Builders = Int(Field(r, LayoutProgressTelemetry.BuildersKey)),
            })];

    // The bedroom step's refused planner_step rows (concern shelter/sleeping).
    private static List<Refusal> RefusalsOf(IReadOnlyList<TimelineRecord> records) =>
        [.. records.Where(r => r.Kind == TelemetryKinds.PlannerStep.Name
                && Text(r.Payload, Decision.VerdictKey) == Refused
                && Field(r, ConcernKey) as string == ShelterFamily.Sleeping.Value
                && TickOf(r) is not null)
            .Select(r => new Refusal(TickOf(r)!.Value, Text(r.Payload, Decision.ReasonKey)))];

    private static LayoutPlanPoint Point(PlanRow row)
    {
        var walled = row.Plan.Rooms.Where(r => !r.Outdoor && r.Parent == 0).Select(r => r.Id).ToHashSet();
        var links = row.Plan.Links.Where(l => walled.Contains(l.A) && walled.Contains(l.B)).Select(l => (Math.Min(l.A, l.B), Math.Max(l.A, l.B))).Distinct().Count();
        var beds = row.Plan.Rooms.Where(r => r.Role is PlannedRole.Bedroom or PlannedRole.Barracks).Select(r => r.Region.Area()).ToList();
        return new LayoutPlanPoint { Tick = row.Tick, Rooms = walled.Count, Links = links, BedroomArea = beds.Count == 0 ? null : beds.Average() };
    }

    private static SortedDictionary<string, int> Count(IEnumerable<string> reasons)
    {
        var output = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var named in NamedRefusals)
        {
            output[named] = 0;
        }
        foreach (var reason in reasons)
        {
            output[reason] = output.GetValueOrDefault(reason) + 1;
        }
        return output;
    }

    private static ScoreThreshold Threshold(string name, double? value, double bound, bool pass, string reason, string note) =>
        new() { Name = name, Value = value, Bound = bound, Status = pass ? ScoreThreshold.Pass : ScoreThreshold.Fail, Reason = pass ? null : reason, Note = note };

    private static ScoreThreshold Unknown(string name, double bound, string note) =>
        new() { Name = name, Bound = bound, Status = ScoreThreshold.Unknown, Note = note };

    private static int? TickOf(TimelineRecord r) => r.Context.GetValueOrDefault(TickKey) is double t ? (int)t : null;

    // A row's field: an event carries it in the payload, a decision under attrs.
    private static object? Field(TimelineRecord r, string key) =>
        r.Payload.TryGetValue(key, out var v) ? v
        : r.Payload.GetValueOrDefault(Decision.AttrsKey) is Dictionary<string, object?> attrs ? attrs.GetValueOrDefault(key)
        : null;

    private static string Text(IReadOnlyDictionary<string, object?> map, string key) => map.GetValueOrDefault(key) as string ?? "";

    private static int Int(object? v) => v is double d ? (int)d : 0;
}
