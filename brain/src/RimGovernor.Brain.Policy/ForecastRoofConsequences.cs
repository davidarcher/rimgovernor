using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// One closed-room prediction of a planned room (#3339): the room's interior cells, its ring sites in the prediction's
/// stuff (WallIn and DoorIn, a stuff-less one in every stuff) and its RoofIn sites (planned site ids), and native's
/// prediction of the room once those ring sites stand.
/// </summary>
public sealed record ForecastPlannedClosure(int Room, string Stuff, EquatableArray<Cell> Interior,
    EquatableArray<string> Ring, EquatableArray<string> Roof, PlannedRoomPrediction Prediction);

/// <summary>A planned room closed inside one schedule: the earliest of its stuffs' closures, and when its roof is done (infinity: not).</summary>
internal sealed record ForecastClosedRoom(EquatableArray<Cell> Interior, PlannedRoomPrediction Prediction, double ClosedAt, double RoofedAt);

/// <summary>A cell's roof and room state at one tick: the observed NeedCell, changed where a planned room closes or is roofed over it.</summary>
internal readonly record struct ForecastCellState(Fact<bool> Roofed, Fact<bool> ThickRoof, Fact<bool> NaturalRoof,
    Fact<bool> OutdoorTemperature, Fact<bool> PsychologicallyOutdoors);

/// <summary>The roof and room thoughts of one pawn over one schedule (#3339); Unassessed names why the pawn's mood is unknown ("" assessed).</summary>
internal sealed record ForecastRoofMood(IReadOnlyList<ForecastMemoryRenewal> Renewals,
    IReadOnlyList<(string Thought, IReadOnlyList<(double Start, double End, double OffsetDelta)> Spans)> Banded,
    IReadOnlyList<string> Findings, string Unassessed);

/// <summary>Mirrored 1.7 literals the roof and room consequences use (#3339); each cites its member. See docs/developers/contracts/kept-constants.md, "Mirrored game literals".</summary>
public static partial class PolicyConstants
{
    /// <summary>Room.PsychologicallyOutdoors: OpenRoofCountStopAt(300) &gt;= 300.</summary>
    public const int PsychologicallyOutdoorsOpenRoofCells = 300;
    /// <summary>Room.PsychologicallyOutdoors: TouchesMapEdge and OpenRoofCount / CellCount &gt;= 0.5.</summary>
    public const double PsychologicallyOutdoorsEdgeOpenFraction = 0.5;
    /// <summary>Room.UsesOutdoorTemperature: OpenRoofCount &gt;= CeilToInt(CellCount * 0.25).</summary>
    public const double OutdoorTemperatureOpenFraction = 0.25;
    /// <summary>Toils_LayDown.LayDown: applyBedThoughtsTick = now + Rand.Range(2500, 10000); a shorter sleep gets no bed thoughts (2500 is the game hour, GameTime.TicksPerHour).</summary>
    public const double BedThoughtsEarliestTicks = GameTime.TicksPerHour;
    /// <summary>Toils_LayDown.LayDown: the far end of Rand.Range(2500, 10000); a sleep this long always gets them at the wake.</summary>
    public const double BedThoughtsLatestTicks = 10000;
    /// <summary>Pawn_NeedsTracker.NeedsTrackerTick: NeedInterval every 150 ticks.</summary>
    public const double NeedIntervalTicks = 150;
    /// <summary>Need_Outdoors.NeedInterval: the delta is scaled by 0.0025 per interval.</summary>
    public const double OutdoorsDeltaScale = 0.0025;
    /// <summary>Need_Outdoors.Delta_IndoorsThickRoof, Delta_OutdoorsThickRoof, Delta_IndoorsThinRoof, Delta_OutdoorsThinRoof, Delta_IndoorsNoRoof, Delta_OutdoorsNoRoof.</summary>
    public const double OutdoorsIndoorsThickRoof = -0.45, OutdoorsOutdoorsThickRoof = -0.4, OutdoorsIndoorsThinRoof = -0.32,
        OutdoorsOutdoorsThinRoof = 1, OutdoorsIndoorsNoRoof = 5, OutdoorsOutdoorsNoRoof = 8;
    /// <summary>Need_Outdoors.Minimum_IndoorsThinRoof (a falling level stops at 0.2; 0 under a thick roof indoors) and DeltaFactor_InBed.</summary>
    public const double OutdoorsFallFloor = 0.2, OutdoorsInBedFactor = 0.2;
    /// <summary>Need_Outdoors.CurCategory thresholds: Free above 0.8, NeedFreshAir above 0.6, CabinFeverLight above 0.4, CabinFeverSevere at 0.2 or more, Trapped above 0.05.</summary>
    public const double OutdoorsFree = 0.8, OutdoorsFreshAir = 0.6, OutdoorsLight = 0.4, OutdoorsSevere = 0.2, OutdoorsTrapped = 0.05;
    /// <summary>SteadyEnvironmentEffects.MapFractionCheckPerTick: CeilToInt(Area * 0.0006) cells a tick.</summary>
    public const double SteadyCellsFractionPerTick = 0.0006;
    /// <summary>SteadyEnvironmentEffects.TryDoDeteriorate: no damage below a rate of 0.001; else Rand.Chance(rate / 36) of 1 damage a visit.</summary>
    public const double DeteriorationMinimumRate = 0.001, DeteriorationChanceDivisor = 36;
    /// <summary>StatPart_EnvironmentalEffects.TransformValue: an unroofed factor is scaled by Lerp(1, 5, RainRate).</summary>
    public const double DeteriorationRainLow = 1, DeteriorationRainHigh = 5;
    /// <summary>StatPart_WorkTableOutdoors.WorkRateFactor: a bench in a psychologically outdoors room works at 0.8.</summary>
    public const double WorkTableOutdoorsFactor = 0.8;
}

public static class ForecastRoofConsequences
{
    private const string BedroomRole = "Bedroom", BarracksRole = "Barracks";

    /// <summary>
    /// The closed-room predictions of a round's planned rooms (#3339): one per prediction row, with the room's region
    /// cells and its ring (WallIn and DoorIn in the row's stuff, or stuff-less) and RoofIn site ids. None when the
    /// predictions are unknown.
    /// </summary>
    internal static EquatableArray<ForecastPlannedClosure> PlannedClosuresOf(IEnumerable<PlannedBuildSite> sites, IEnumerable<PlannedRoom> rooms,
        Fact<EquatableArray<PlannedRoomPrediction>> predictions)
    {
        if (!predictions.TryGetValue(out var rows)) return [];
        var byRoom = sites.Where(s => s.Room > 0 && s.Zone.Length == 0).ToLookup(s => s.Room);
        var shapes = rooms.Where(r => r.Id > 0).GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
        var result = new List<ForecastPlannedClosure>();
        foreach (var row in rows)
        {
            if (!int.TryParse(row.Room, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var id)
                || !shapes.TryGetValue(id, out var room)) continue;
            var ring = byRoom[id].Where(s => s.Op is OpKind.WallIn or OpKind.DoorIn && (s.Stuff.Length == 0 || s.Stuff == row.Stuff))
                .Select(s => s.Id).Order(StringComparer.Ordinal);
            var roof = byRoom[id].Where(s => s.Op == OpKind.RoofIn).Select(s => s.Id).Order(StringComparer.Ordinal);
            result.Add(new(id, row.Stuff, EquatableArray.From(room.Region.Cells().OrderBy(c => c.X).ThenBy(c => c.Z)),
                EquatableArray.From(ring), EquatableArray.From(roof), row));
        }
        return EquatableArray.From(result);
    }

    /// <summary>
    /// The planned rooms one schedule closes (#3339), from the activities completed so far (id to completion tick): a
    /// room closes when every ring site of one stuff has a completed finish, at the latest of them, the earliest stuff
    /// winning. Its roof is done when every RoofIn site has, or every per-cell roof of its planned shelter has.
    /// </summary>
    internal static IReadOnlyList<ForecastClosedRoom> PlannedClosedRooms(ForecastCoupledContext context, IReadOnlyDictionary<string, double> finishedAt)
    {
        if (context.Closures.Count == 0) return [];
        var siteAt = context.PlannedSites.GroupBy(s => s.Site, StringComparer.Ordinal).ToDictionary(g => g.Key,
            g => g.SelectMany(s => s.Finishes).Where(finishedAt.ContainsKey).Select(f => finishedAt[f]).DefaultIfEmpty(double.PositiveInfinity).Min(),
            StringComparer.Ordinal);
        double SiteAt(string site) => siteAt.GetValueOrDefault(site, double.PositiveInfinity);
        double AllAt(IEnumerable<string> ids) => ids.Select(SiteAt).DefaultIfEmpty(double.PositiveInfinity).Max();
        var closed = new List<ForecastClosedRoom>();
        foreach (var room in context.Closures.GroupBy(c => c.Room).OrderBy(g => g.Key))
        {
            var best = room.Where(c => c.Ring.Count > 0).Select(c => (Closure: c, At: AllAt(c.Ring))).OrderBy(c => c.At).FirstOrDefault();
            if (best.Closure is null || !double.IsFinite(best.At)) continue;
            var roofed = best.Closure.Roof.Count > 0 ? AllAt(best.Closure.Roof) : double.PositiveInfinity;
            var key = room.Key.ToString(System.Globalization.CultureInfo.InvariantCulture);
            foreach (var shelter in context.Shelters.Where(s => s.Room.Source == ForecastShelterSource.Planned && s.Room.Value == key && s.RoofCells.Count > 0))
                roofed = Math.Min(roofed, shelter.RoofCells.Select(cell => cell.Where(finishedAt.ContainsKey).Select(f => finishedAt[f])
                    .DefaultIfEmpty(double.PositiveInfinity).Min()).Max());
            closed.Add(new(best.Closure.Interior, best.Closure.Prediction, best.At, roofed));
        }
        return closed;
    }

    /// <summary>
    /// A cell's state at <paramref name="time"/>: inside a closed planned room the room's own (open roof cells from the
    /// prediction, none once its roof is done; Room.PsychologicallyOutdoors and UsesOutdoorTemperature mirrored), and a
    /// roof done over it is a constructed roof. Elsewhere, or before closure, the observed cell. Unknown when unread.
    /// </summary>
    internal static ForecastCellState CellStateAt(NeedCell? observed, Cell? cell, double time, IReadOnlyList<ForecastClosedRoom> rooms)
    {
        var state = observed is null ? default : new ForecastCellState(observed.Roofed, observed.ThickRoof, observed.NaturalRoof,
            observed.OutdoorTemperature, observed.PsychologicallyOutdoors);
        if (cell is not { } at || rooms.FirstOrDefault(r => r.Interior.Contains(at)) is not { } room) return state;
        var roofDone = room.RoofedAt <= time;
        if (roofDone) state = state with { Roofed = Known(true), ThickRoof = Known(false), NaturalRoof = Known(false) };
        if (room.ClosedAt > time) return state;
        var prediction = room.Prediction;
        if (prediction.Cells <= 0) return state with { OutdoorTemperature = default, PsychologicallyOutdoors = default };
        var open = roofDone ? 0 : prediction.OpenRoofCells;
        return state with
        {
            PsychologicallyOutdoors = Known(ClosedPsychologicallyOutdoors(prediction, open)),
            OutdoorTemperature = Known(prediction.TouchesMapEdge
                || open >= Math.Ceiling(prediction.Cells * PolicyConstants.OutdoorTemperatureOpenFraction)),
        };
    }

    private static bool ClosedPsychologicallyOutdoors(PlannedRoomPrediction prediction, int open)
        => open >= PolicyConstants.PsychologicallyOutdoorsOpenRoofCells || prediction.TouchesMapEdge && prediction.Cells > 0
            && open / (double)prediction.Cells >= PolicyConstants.PsychologicallyOutdoorsEdgeOpenFraction;

    /// <summary>
    /// One pawn's roof and room thoughts along a schedule (#3339):
    /// the bed thoughts ApplyBedThoughts renews at each completed sleep's wake (SleptOutside when the bed's room is
    /// psychologically outdoors; SleptInBedroom or SleptInBarracks at the room's Impressiveness stage for an owned,
    /// non-prisoner bed of a non-ascetic, from the closed-room prediction once the room is closed by then), the weather
    /// thought renewed over each known-location span, and the outdoors need and undergrounder thoughts as banded spans.
    /// A pawn's location is known only at its selected joy and rest targets; elsewhere these hold their observed value.
    /// </summary>
    internal static ForecastRoofMood RoofMoodOf(NeedsForecastPawn pawn, NeedsForecastRead read, IReadOnlyList<ForecastNeedChoice> choices,
        IReadOnlyList<ForecastClosedRoom> rooms, Fact<EquatableArray<MoodThought>> observed, double horizon)
    {
        var renewals = new List<ForecastMemoryRenewal>();
        var banded = new List<(string Thought, IReadOnlyList<(double Start, double End, double OffsetDelta)> Spans)>();
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        var unassessed = "";
        void Unknown(string reason) { if (unassessed.Length == 0) unassessed = reason; }
        NeedMemory Memory(NeedThoughtStage row) => new(row.Thought, row.Offset, row.DurationTicks, row.Fades);
        var targets = pawn.Targets.GroupBy(t => t.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var stages = pawn.ThoughtStages;
        var wakes = ForecastNeeds.SleepServiceEnds(pawn, choices, null, new SortedSet<string>(StringComparer.Ordinal));

        // Bed thoughts at the wake (Toils_LayDown.ApplyBedThoughts).
        var bedDefs = stages.Where(r => r.Role is "slept_outside" or "slept_in_bedroom" or "slept_in_barracks").Select(r => r.Thought)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        foreach (var rest in choices.Where(c => c.Kind == "rest").OrderBy(c => c.Start))
        {
            if (wakes is null || !wakes.TryGetValue(rest.Id, out var wake)) break;
            if (!targets.TryGetValue(rest.Target, out var bed) || bed.Bed is null || bed.Cell is null || bed.Location is null || bedDefs.Length == 0)
            { findings.Add("sleep_room_thoughts_unread:" + rest.Target); continue; }
            var continuing = rest.Start == 0 && pawn.Awake == Known(false);
            var slept = wake - rest.Start;
            if (!continuing && slept < PolicyConstants.BedThoughtsEarliestTicks) continue;
            if (slept < PolicyConstants.BedThoughtsLatestTicks) { Unknown("sleep_bed_thoughts_onset_unassessed:" + rest.Id); continue; }
            var closed = rooms.FirstOrDefault(r => r.ClosedAt <= wake && r.Prediction.Beds.Contains(bed.Id));
            var (role, stage) = closed is null ? (bed.Bed.Role, bed.Bed.Stage) : (closed.Prediction.Role, closed.Prediction.Stage);
            var outside = CellStateAt(bed.Cell, bed.Location, wake, rooms).PsychologicallyOutdoors;
            var gains = new List<NeedMemory>();
            if (!outside.TryGetValue(out var isOutside)) { Unknown("sleep_room_outdoors_unknown:" + rest.Id); continue; }
            if (isOutside)
            {
                if (stages.FirstOrDefault(r => r.Role == "slept_outside" && r.Stage == 0) is not { } row) { Unknown("sleep_outside_unpriced"); continue; }
                gains.Add(Memory(row));
            }
            if (bed.Id == pawn.OwnedBed && !pawn.Ascetic && !bed.Bed.ForPrisoners)
            {
                if (role.Length == 0) { Unknown("sleep_room_role_unknown:" + rest.Id); continue; }
                var thought = role == BedroomRole ? "slept_in_bedroom" : role == BarracksRole ? "slept_in_barracks" : "";
                if (thought.Length != 0)
                {
                    if (!stage.TryGetValue(out var index)) { Unknown("sleep_room_stage_unknown:" + rest.Id); continue; }
                    if (stages.FirstOrDefault(r => r.Role == thought && r.Stage == index) is not { } row) { Unknown("sleep_room_stage_unpriced:" + rest.Id); continue; }
                    gains.Add(Memory(row));
                }
            }
            renewals.Add(new(wake, EquatableArray.From(bedDefs), EquatableArray.From(gains)));
            findings.Add("sleep_ground_and_temperature_memories_constant");
        }

        // Spans at a known cell: joy at its target; rest at its bed, asleep until the wake and awake in bed after it.
        var spans = new List<(double Start, double End, NeedTarget Target, bool Asleep, bool InBed)>();
        foreach (var choice in choices.Where(c => c.Kind is "joy" or "rest"))
        {
            if (!targets.TryGetValue(choice.Target, out var target) || target.Cell is null || target.Location is null) continue;
            if (choice.Kind == "joy") { spans.Add((choice.Start, choice.End, target, false, false)); continue; }
            if (wakes is null || !wakes.TryGetValue(choice.Id, out var wake)) continue;
            if (wake > choice.Start) spans.Add((choice.Start, wake, target, true, true));
            if (choice.End > wake) spans.Add((wake, choice.End, target, false, true));
        }
        spans.Sort((a, b) => a.Start.CompareTo(b.Start));

        // The weather thought (Pawn_MindState: every 120 ticks while the weather has one, roofed or not).
        if (read.WeatherThought.Length != 0 && stages.Any(r => r.Role == "weather") && spans.Count > 0)
        {
            findings.Add("weather_held_constant");
            var single = read.WeatherThoughtStages == 1;
            foreach (var span in spans)
            {
                if (!CellStateAt(span.Target.Cell, span.Target.Location, span.Start, rooms).Roofed.TryGetValue(out var roofed))
                { Unknown("weather_roof_unknown"); break; }
                if (roofed && single) continue;
                var index = single || roofed ? 0 : 1;
                if (stages.FirstOrDefault(r => r.Role == "weather" && r.Thought == read.WeatherThought && r.Stage == index) is not { } row)
                { Unknown("weather_thought_unpriced"); break; }
                // Renewed through the span: one renewal where it starts and one where it ends (adjacent spans share one).
                foreach (var at in new[] { span.Start, span.End }.Where(t => t < horizon))
                    if (!renewals.Any(r => r.At == at && r.Ends.Contains(row.Thought))) renewals.Add(new(at, [row.Thought], [Memory(row)]));
            }
        }

        var awake = spans.Where(s => !s.Asleep).ToArray();
        var thoughts = observed.TryGetValue(out var rows) ? rows : default;
        double Current(string def) => thoughts.Where(t => t.Def == def).Sum(t => t.Offset);

        // The outdoors need along the known spans (Need_Outdoors.NeedInterval), its category held between them.
        if (pawn.OutdoorsLevel.TryGetValue(out var level) && !pawn.OutdoorsDisabled && stages.Any(r => r.Role == "need_outdoors" && r.Offset != 0))
        {
            findings.Add("outdoors_need_constant_outside_known_location");
            var changes = new List<(double At, int Stage)>();
            var initial = OutdoorsStage(level);
            var last = initial;
            foreach (var span in awake)
            {
                var state = CellStateAt(span.Target.Cell, span.Target.Location, span.Start, rooms);
                if (!state.OutdoorTemperature.TryGetValue(out var outdoors) || !state.Roofed.TryGetValue(out var roofed)
                    || roofed && !state.ThickRoof.IsKnown) { Unknown("outdoors_need_cell_unknown"); break; }
                var thick = roofed && state.ThickRoof.Value;
                var delta = outdoors ? !roofed ? PolicyConstants.OutdoorsOutdoorsNoRoof : thick ? PolicyConstants.OutdoorsOutdoorsThickRoof : PolicyConstants.OutdoorsOutdoorsThinRoof
                    : !roofed ? PolicyConstants.OutdoorsIndoorsNoRoof : thick ? PolicyConstants.OutdoorsIndoorsThickRoof : PolicyConstants.OutdoorsIndoorsThinRoof;
                var floor = !outdoors && thick ? 0 : PolicyConstants.OutdoorsFallFloor;
                if (span.InBed && delta < 0) delta *= PolicyConstants.OutdoorsInBedFactor;
                var rate = delta * PolicyConstants.OutdoorsDeltaScale / PolicyConstants.NeedIntervalTicks;
                var from = level;
                double LevelAt(double t) => rate < 0 ? (from < floor ? from : Math.Max(floor, from + rate * (t - span.Start))) : Math.Min(1, from + rate * (t - span.Start));
                // The stage changes where the level crosses a category threshold inside the span, each piece read at its midpoint.
                var cuts = new SortedSet<double> { span.Start, span.End };
                if (rate != 0)
                    foreach (var bound in new[] { PolicyConstants.OutdoorsFree, PolicyConstants.OutdoorsFreshAir, PolicyConstants.OutdoorsLight,
                        PolicyConstants.OutdoorsSevere, PolicyConstants.OutdoorsTrapped })
                        if (span.Start + (bound - from) / rate is var t && t > span.Start && t < span.End) cuts.Add(t);
                var points = cuts.ToArray();
                for (var i = 0; i < points.Length - 1; i++)
                {
                    var stage = OutdoorsStage(LevelAt((points[i] + points[i + 1]) / 2));
                    if (stage != last) { changes.Add((points[i], stage)); last = stage; }
                }
                level = LevelAt(span.End);
            }
            if (changes.Count > 0 && unassessed.Length == 0)
            {
                if (!observed.IsKnown) Unknown("outdoors_need_observed_thought_unknown");
                else foreach (var def in stages.Where(r => r.Role == "need_outdoors").Select(r => r.Thought).Distinct(StringComparer.Ordinal))
                {
                    var current = Current(def);
                    var rowsOut = new List<(double Start, double End, double OffsetDelta)>();
                    for (var i = 0; i < changes.Count; i++)
                    {
                        var to = i + 1 < changes.Count ? changes[i + 1].At : horizon;
                        if (changes[i].Stage == initial || to <= changes[i].At) continue;
                        var offset = changes[i].Stage < 0 ? 0 : stages.FirstOrDefault(r => r.Thought == def && r.Stage == changes[i].Stage)?.Offset;
                        if (offset is null) { Unknown("outdoors_need_stage_unpriced"); break; }
                        rowsOut.Add((changes[i].At, Math.Min(to, horizon), offset.Value - current));
                    }
                    banded.Add((def, rowsOut));
                }
            }
        }

        // The undergrounder thoughts (ThoughtWorker_Is{Indoors,Underground,Outdoors}ForUndergrounder): awake, at a known cell.
        foreach (var role in new[] { "undergrounder_indoors", "undergrounder_underground", "undergrounder_outdoors" })
            foreach (var def in stages.Where(r => r.Role == role && r.Offset != 0).Select(r => r.Thought).Distinct(StringComparer.Ordinal))
            {
                if (awake.Length == 0) continue;
                if (!observed.IsKnown) { Unknown("undergrounder_observed_thought_unknown"); continue; }
                var offset = stages.First(r => r.Thought == def && r.Stage == stages.Where(s => s.Thought == def).Min(s => s.Stage)).Offset;
                var rowsOut = new List<(double Start, double End, double OffsetDelta)>();
                foreach (var span in awake)
                {
                    var state = CellStateAt(span.Target.Cell, span.Target.Location, span.Start, rooms);
                    if (!state.OutdoorTemperature.TryGetValue(out var outdoors) || !state.Roofed.TryGetValue(out var roofed)
                        || !state.NaturalRoof.TryGetValue(out var natural)) { Unknown("undergrounder_cell_unknown"); break; }
                    var active = role switch
                    {
                        "undergrounder_indoors" => !outdoors && roofed && !natural,
                        "undergrounder_underground" => !outdoors && roofed && natural,
                        _ => outdoors || !roofed,
                    };
                    rowsOut.Add((span.Start, span.End, (active ? offset : 0) - Current(def)));
                }
                banded.Add((def, rowsOut));
            }
        return new(renewals, banded, findings.ToArray(), unassessed);
    }

    /// <summary>ThoughtWorker_NeedOutdoors' stage of an outdoors level (Need_Outdoors.CurCategory): -1 when Free (inactive).</summary>
    private static int OutdoorsStage(double level) => level > PolicyConstants.OutdoorsFree ? -1 : level > PolicyConstants.OutdoorsFreshAir ? 4
        : level > PolicyConstants.OutdoorsLight ? 3 : level >= PolicyConstants.OutdoorsSevere ? 2 : level > PolicyConstants.OutdoorsTrapped ? 1 : 0;

    /// <summary>
    /// The tick a food stack is destroyed by expected deterioration before <paramref name="until"/> (#3339), or infinity.
    /// Expected hit points lost a tick are FinalDeteriorationRate / 36 times the cells SteadyEnvironmentEffects visits a
    /// tick over the map area (none below a rate of 0.001). The observed rate holds while the cell does; where a planned
    /// room closes or is roofed over it, the StatPart_EnvironmentalEffects factor is rebuilt at the new state and the rate
    /// rescaled by it (the other parts, multipliers, held). Rain is held at its observed rate. Null when an input is unknown.
    /// </summary>
    internal static double? DeterioratedAt(NeedTarget stack, NeedsForecastRead read, IReadOnlyList<ForecastClosedRoom> rooms, double until)
    {
        if (stack.Deterioration is not { } d || stack.Location is not { } cell) return double.PositiveInfinity;
        if (!d.RatePerDay.TryGetValue(out var observedRate) || !double.IsFinite(observedRate) || !read.MapArea.TryGetValue(out var area) || area <= 0) return null;
        if (observedRate <= 0) return double.PositiveInfinity;
        var visits = Math.Ceiling(area * PolicyConstants.SteadyCellsFractionPerTick) / area;
        var room = rooms.FirstOrDefault(r => r.Interior.Contains(cell));
        var times = new SortedSet<double> { 0 };
        if (room is not null)
            foreach (var at in new[] { room.ClosedAt, room.RoofedAt }) if (at > 0 && at < until) times.Add(at);
        var points = times.Append(until).ToArray();
        double? Factor(double time)
        {
            var state = CellStateAt(stack.Cell, cell, time, rooms);
            if (read.Deterioration is not { } part || !read.RainRate.TryGetValue(out var rain)
                || !state.Roofed.TryGetValue(out var roofed) || !state.OutdoorTemperature.TryGetValue(out var outdoors)) return null;
            var factor = (roofed ? 0 : part.UnroofedOffset) + (outdoors ? part.OutdoorsOffset : 0) + d.TerrainFactor;
            if (!roofed) factor *= PolicyConstants.DeteriorationRainLow + (PolicyConstants.DeteriorationRainHigh - PolicyConstants.DeteriorationRainLow) * Math.Clamp(rain, 0, 1);
            if (d.Protected) factor *= part.ProtectedFactor;
            return factor;
        }
        var now = d.Environmental && room is not null ? Factor(0) : 1;
        if (now is null) return null;
        double lost = 0;
        for (var i = 0; i < points.Length - 1; i++)
        {
            var rate = observedRate;
            if (d.Environmental && room is not null && points[i] > 0)
            {
                if (Factor(points[i]) is not { } factor) return null;
                rate = now.Value > 0 ? observedRate * factor / now.Value : 0;
            }
            if (rate < PolicyConstants.DeteriorationMinimumRate) continue;
            var perTick = rate / PolicyConstants.DeteriorationChanceDivisor * visits;
            var span = points[i + 1] - points[i];
            if (lost + perTick * span >= d.HitPoints) return points[i] + (d.HitPoints - lost) / perTick;
            lost += perTick * span;
        }
        return double.PositiveInfinity;
    }

    /// <summary>
    /// Closure-conditional bench variants (#3339): a root whose one completion task works at a bench native reads as in a
    /// psychologically outdoors room now, in a planned room that closes indoors, gets a variant <c>{task}/closed/{room}[/{stuff}]</c>
    /// after the first realization of each of that stuff's ring finishes, its bench options' work ticks at
    /// StatPart_WorkTableOutdoors.WorkRateFactor (the observed ticks already carry the outdoor penalty). The variant root
    /// excludes the room's other-stuff planned roots, as ring sites of two stuffs exclude each other.
    /// </summary>
    internal static void AddClosedBenchVariants(IReadOnlyList<ForecastPlannedClosure> closures, IReadOnlyList<ForecastCommitment> roots,
        IReadOnlyList<ForecastPlannedSite> realized, List<ForecastActivity> tasks, List<ForecastActivityOption> options,
        Dictionary<(string, string), Methods.ActivityTaskCandidate> bindings, List<ForecastCommitment> alternatives,
        IReadOnlyDictionary<string, (int Room, string Stuff)> plannedStuff)
    {
        static string Base(string id) => id.IndexOf("/composition/", StringComparison.Ordinal) is var i and >= 0 ? id[..i] : id;
        foreach (var closure in closures.OrderBy(c => c.Room).ThenBy(c => c.Stuff, StringComparer.Ordinal))
        {
            var benches = closure.Prediction.Benches.Where(b => b.PsychologicallyOutdoors).Select(b => b.Bench).ToHashSet(StringComparer.Ordinal);
            if (benches.Count == 0 || closure.Ring.Count == 0 || closure.Prediction.Cells <= 0
                || ClosedPsychologicallyOutdoors(closure.Prediction, closure.Prediction.OpenRoofCells)) continue;
            var firsts = closure.Ring.Select(site => realized.FirstOrDefault(p => p.Site == site)?.Finishes is { Count: > 0 } f ? f[0] : null).ToArray();
            if (firsts.Any(f => f is null)) continue;
            var finishes = firsts.Select(f => f!).ToArray();
            var suffix = "/closed/" + closure.Room.ToString(System.Globalization.CultureInfo.InvariantCulture) + (closure.Stuff.Length == 0 ? "" : "/" + closure.Stuff);
            var excludes = alternatives.Where(a => plannedStuff.TryGetValue(Base(a.Id), out var of) && of.Room == closure.Room && of.Stuff.Length != 0 && of.Stuff != closure.Stuff)
                .Select(a => a.Id).ToArray();
            foreach (var root in roots.Where(r => r.ActivityCompletions.Count == 1))
            {
                var original = tasks.FirstOrDefault(t => t.Id == root.ActivityCompletions[0]);
                var at = options.Where(o => original is not null && o.Activity == original.Id).ToArray();
                if (original is null || !at.Any(o => o.Equipment.Any(benches.Contains))) continue;
                var id = original.Id + suffix;
                if (tasks.All(t => t.Id != id))
                {
                    tasks.Add(original with { Id = id, Prerequisites = [.. original.Prerequisites, .. finishes] });
                    foreach (var option in at)
                    {
                        options.Add(option with
                        {
                            Activity = id,
                            WorkTicks = option.Equipment.Any(benches.Contains) && option.WorkTicks.TryGetValue(out var ticks)
                                ? Known(ticks * PolicyConstants.WorkTableOutdoorsFactor) : option.WorkTicks,
                        });
                        if (bindings.TryGetValue((original.Id, option.Pawn), out var binding))
                            bindings[(id, option.Pawn)] = binding with { Id = id, Prerequisites = EquatableArray.From(original.Prerequisites.Concat(finishes)) };
                    }
                }
                alternatives.Add(root with
                {
                    Id = root.Id + suffix, ActivityCompletions = [id],
                    Outcome = root.Outcome is { } outcome && excludes.Length > 0
                        ? outcome with { Excludes = EquatableArray.From(outcome.Excludes.Concat(excludes).Distinct().Order(StringComparer.Ordinal)) } : root.Outcome,
                });
            }
        }
    }
}
