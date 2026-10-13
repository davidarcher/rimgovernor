namespace RimGovernor.Brain.Policy;

// Comfort and beauty pricing (#2975). Furniture buys mood through two needs the game keeps as one shape, a
// Need_Seeker: a level that climbs toward an "instant" level while something gives it and falls toward it
// otherwise. The planner prices the mood of a better chair or bed by simulating the need over a colonist's day
// with and without it, reading the thought stage the level is in, and pricing the difference of the
// time-averaged mood with the mood currency (PriceMood, #2954: a mood point is break risk) against the
// furniture's cost in sustained watts (BuildW).
//
// What the game says (ilspycmd, Verse.Need, RimWorld.Need_Seeker, Need_Comfort, Need_Beauty,
// PawnUtility.GainComfortFromCellIfPossible, Toils_Recipe, Toils_Ingest, Toils_LayDown,
// ThoughtWorker_NeedComfort / NeedBeauty; NeedDef rows from the catalog):
//   - Every 150 ticks Need_Seeker.NeedInterval moves the level toward the instant level, up by
//     seekerRisePerHour x 0.06 (the hour is 16.67 intervals, so a full hour moves it by seekerRisePerHour) or
//     down by seekerFallPerHour x 0.06, never past the instant level. A need frozen (freezeWhileSleeping and
//     asleep) does not move.
//   - Comfort: the instant level is the comfort stat of the building the pawn stands on while it was used in
//     the last 15 ticks, else 0. It is used when lying in a bed (Toils_LayDown), eating (Toils_Ingest, any
//     edifice with the stat), and doing recipe work or research (chairsOnly: the edifice must be a sittable
//     building, so a bench worked standing up gives nothing). Rise 0.60 an hour, fall 0.04, not frozen by
//     sleep. The thought NeedComfort has the stage <0.1 Uncomfortable (-3), 0.1-0.6 none, then +4, +6, +8, +10
//     from 0.6, 0.7, 0.8, 0.9.
//   - Beauty: the instant level is 0.4 (baseLevel) plus 0.1 of the mean beauty of the cells within 8.9 of the
//     pawn that its room sees (BeautyUtility), clamped; rise 0.32 an hour, fall 0.08, frozen asleep. The
//     thought NeedBeauty has the stages -15 up to 0.01, -10 to 0.15, -5 to 0.35, none to 0.65, then +5, +10,
//     +15 above 0.65, 0.85 and 0.99.
//   - The comfort stat is base (stuff) times quality, plus the offsets of the facilities linked to the
//     building (an end table, a dresser). Quality is the crafter's, not the planner's; it is priced at Normal
//     (factor 1).
//
// The day is the schedule template's (pawn_schedule): the sleep block on the bed, the Anything hours spent at
// the spots in order, then idle at an instant level of 0 (nothing is used), and the Joy hour idle: the order
// matters little (comfort falls 4% an hour) and is fixed. The pawn repeats it until the level at its start
// stops changing; the day read is that steady one.
//
// Dwell is derived, never guessed: sleep from the schedule, a bench's hours from the station graph (the node's
// DailyWork over the colonists, who share the work evenly at work speed 1), and a spot with no dwell model
// (dining, which needs the ingest time, and recreation, which has no model yet) is left out: the day without it
// is a lower bound of the level. The furnisher does not read the advice (StationAdvice.Comfort) yet.

/// <summary>
/// Maps a need level to a thought stage (-1 for no stage). Level is in the band k when it is below Edges[k]
/// (at most it, when UpperClosed), and in the last band above every edge. Comfort's category test is
/// level &lt; edge (Need_Comfort.CurCategory), beauty's level &gt; edge (Need_Beauty.CurCategory), which is the
/// band closed above.
/// </summary>
public sealed record SeekerBands(EquatableArray<double> Edges, EquatableArray<int> Stages, bool UpperClosed = false)
{
    /// <summary>The thought stage of a need level, -1 when the level gives none.</summary>
    public int Stage(double level)
    {
        for (var k = 0; k < Edges.Count; k++)
        {
            if (level < Edges[k] || (UpperClosed && level == Edges[k]))
            {
                return Stages[k];
            }
        }
        return Stages[Stages.Count - 1];
    }

    /// <summary>The highest stage index.</summary>
    internal int MaxStage() => Stages.Aggregate(-1, (top, s) => Math.Max(top, s));
}

/// <summary>One need's game numbers that its NeedDef row does not carry: the thought the level is read through and its category thresholds.</summary>
public sealed record SeekerGameDef(string Need, string Thought, SeekerBands Bands);

/// <summary>
/// The environment needs' game table, registered in DefTables so a renamed def fails the dangling-reference
/// gate. IntervalTicks is Pawn_NeedsTracker's need interval (IsHashIntervalTick(150)); BeautyImpact is
/// Need_Beauty.BeautyImpactFactor, the level a point of mean beauty moves. The rise, fall, base level and sleep
/// freeze are the NeedDef rows', read by the bridge.
/// </summary>
public sealed record SeekerTable(double IntervalTicks, double BeautyImpact, SeekerGameDef Comfort, SeekerGameDef Beauty);

/// <summary>
/// A seeker need's NeedDef numbers: the level it starts from, how fast it rises and falls an hour, and whether
/// sleep freezes it. The default value is unknown.
/// </summary>
public readonly record struct NeedCurve(double Base, double RisePerHour, double FallPerHour, bool FreezeAsleep)
{
    /// <summary>Reports a curve with both rates and a base level in 0 to 1.</summary>
    public bool Valid() => FinitePositive(RisePerHour) && FinitePositive(FallPerHour) && Base >= 0 && Base <= 1;
}

/// <summary>Hours of a pawn's day spent seeking Target (the instant level), asleep or awake.</summary>
public readonly record struct EnvSegment(double Hours, double Target, bool Asleep = false);

/// <summary>
/// The steady day of a need: the hours spent in each thought stage, StageHours[s+1] for stage s and
/// StageHours[0] for no stage, and the level the day starts from.
/// </summary>
public sealed record EnvRun(EquatableArray<double> StageHours, double Start)
{
    /// <summary>The time-averaged mood the run's stages give, in points; false when the thought or a stage is unknown.</summary>
    public (double Mood, bool Ok) MeanMood(MoodModel m, string thought)
    {
        if (!m.Stages.TryGetValue(thought, out var stages) || stages.Count == 0)
        {
            return (0, false);
        }
        var sum = 0.0;
        for (var i = 0; i < StageHours.Count; i++)
        {
            var h = StageHours[i];
            if (h == 0 || i == 0)
            {
                continue;
            }
            var (effect, _) = m.StageEffect(thought, i - 1);
            sum += h * effect;
        }
        return (sum / 24, true);
    }
}

/// <summary>One environment need: its curve and its game numbers. Both needs are the same model, so a third (impressiveness, cleanliness) plugs in here.</summary>
public sealed record SeekerNeed(NeedCurve Curve, SeekerGameDef Game)
{
    /// <summary>Simulates the need over the day, repeated until it settles. False for an invalid curve or a day that is not 24 hours of segments.</summary>
    public (EnvRun? Run, bool Ok) Run(IReadOnlyList<EnvSegment> day)
    {
        var steps = PolicyFunctions.EnvDaySteps();
        if (!Curve.Valid())
        {
            return (null, false);
        }
        var total = 0.0;
        foreach (var s in day)
        {
            if (s.Hours < 0 || double.IsNaN(s.Hours) || double.IsNaN(s.Target))
            {
                return (null, false);
            }
            total += s.Hours;
        }
        if (Math.Abs(total - 24) > 1e-9)
        {
            return (null, false);
        }

        // The segment each interval falls in, by its midpoint.
        var targets = new double[steps];
        var asleep = new bool[steps];
        double hoursPerStep = steps;
        var at = 0.0;
        var k = 0;
        for (var i = 0; i < steps; i++)
        {
            var mid = (i + 0.5) * 24 / hoursPerStep;
            while (k < day.Count - 1 && at + day[k].Hours <= mid)
            {
                at += day[k].Hours;
                k++;
            }
            targets[i] = Math.Min(Math.Max(day[k].Target, 0), 1);
            asleep[i] = day[k].Asleep;
        }
        var level = Curve.Base;
        for (var d = 0; d < PolicyConstants.EnvMaxDays; d++)
        {
            var start = level;
            var hours = new double[Game.Bands.MaxStage() + 2];
            for (var i = 0; i < steps; i++)
            {
                level = Advance(level, targets[i], asleep[i]);
                hours[Game.Bands.Stage(level) + 1] += 24 / hoursPerStep;
            }
            if (Math.Abs(level - start) < 1e-12)
            {
                return (new EnvRun(EquatableArray.From(hours), start), true);
            }
        }
        return (null, false);
    }

    /// <summary>
    /// Need_Seeker.NeedInterval: one 150-tick interval toward the instant level, up at the rise rate and down
    /// at the fall rate, never past it, and not at all while the need is frozen asleep.
    /// </summary>
    private double Advance(double level, double target, bool asleep)
    {
        if (Curve.FreezeAsleep && asleep)
        {
            return level;
        }
        var perStep = PolicyFunctions.SeekerGame().IntervalTicks / GameTime.TicksPerHour;
        if (target > level)
        {
            level = Math.Min(level + (Curve.RisePerHour * perStep), target);
        }
        if (target < level)
        {
            level = Math.Max(level - (Curve.FallPerHour * perStep), target);
        }
        return level;
    }

    /// <summary>Prices the alt day against the base day. False when any number is unknown: the curve, the thought's stages, the break model or the work-day.</summary>
    public (EnvChange Change, bool Ok) Price(IReadOnlyList<EnvSegment> baseDay, IReadOnlyList<EnvSegment> alt, EnvPrice input)
    {
        var (b, ok) = Run(baseDay);
        if (!ok)
        {
            return (default, false);
        }
        var (a, aok) = Run(alt);
        if (!aok)
        {
            return (default, false);
        }
        var (mb, mbok) = b!.MeanMood(input.Mood, Game.Thought);
        if (!mbok)
        {
            return (default, false);
        }
        var (ma, maok) = a!.MeanMood(input.Mood, Game.Thought);
        if (!maok)
        {
            return (default, false);
        }
        var (price, pok) = PolicyFunctions.PriceMood(new MoodPriceInput { Model = input.Mood.Break, WorkDayW = input.WorkDayW, Points = mb - ma, Pawns = input.Pawns, ExpectedMood = input.ExpectedMood });
        if (!pok)
        {
            return (default, false);
        }
        return (new EnvChange(ma - mb, -price.W), true);
    }
}

public static partial class PolicyConstants
{
    /// <summary>
    /// Bounds the steady-state search; a day's start level that has not settled by then leaves the run
    /// unknown. Rates are at least a few percent an hour, so a settled level takes days.
    /// </summary>
    internal const int EnvMaxDays = 200;
}

/// <summary>Hours a day awake at a spot whose instant level is Target.</summary>
public readonly record struct EnvSpot(double Hours, double Target);

/// <summary>
/// What the price of a change in a need reads: the colony's mood model, a work-day in watts, the colonists the
/// change reaches and their mean mood level without it (0 to 1).
/// </summary>
public sealed record EnvPrice(MoodModel Mood, double WorkDayW, int Pawns, double ExpectedMood);

/// <summary>
/// A change of the need's day priced as mood. Gain is the change of the time-averaged mood in points a colonist
/// (negative when the change loses mood) and BenefitW what the mood is worth in sustained watts for all Pawns:
/// the break risk it removes, a step function that is zero while the mood stays inside a break tier.
/// </summary>
public readonly record struct EnvChange(double Gain, double BenefitW);

/// <summary>
/// One furniture piece a spot could hold: its def and stuff, the comfort stat the pawn using it gets at Normal
/// quality and its cost, the market value of its adjusted costs. The default option is no furniture.
/// </summary>
public sealed record ComfortOption
{
    public string Def { get; init; } = string.Empty;

    public string Stuff { get; init; } = string.Empty;

    public double Comfort { get; init; }

    public double Build { get; init; }
}

/// <summary>
/// The catalog's furniture and needs for the comfort pricing; the default value is unknown. Chairs are the
/// buildable sittable defs at each stuff and Beds the single beds an adult sleeps in. BedOffset is the comfort
/// the bed's linked facilities add (the end table and dresser every bedroom template places, so a sunk cost of
/// every bed alike). Comfort and Beauty are the NeedDef curves.
/// </summary>
public sealed record ComfortModel
{
    public NeedCurve Comfort { get; init; }

    public NeedCurve Beauty { get; init; }

    public IReadOnlyList<ComfortOption> Chairs { get; init; } = [];

    public IReadOnlyList<ComfortOption> Beds { get; init; } = [];

    public double BedOffset { get; init; }

    /// <summary>The comfort need over the model.</summary>
    public SeekerNeed ComfortNeed() => new(Comfort, PolicyFunctions.SeekerGame().Comfort);

    /// <summary>
    /// The beauty need over the model. Beauty is fed by the cells around a spot, not by furniture comfort; a
    /// caller with decor candidates prices them through SeekerNeed.Price with BeautyTarget, the same shape as a
    /// chair.
    /// </summary>
    public SeekerNeed BeautyNeed() => new(Beauty, PolicyFunctions.SeekerGame().Beauty);

    /// <summary>The instant level of beauty at a spot whose visible cells have the given mean beauty (Need_Beauty.LevelFromBeauty).</summary>
    public double BeautyTarget(double meanBeauty) =>
        Math.Min(Math.Max(Beauty.Base + (PolicyFunctions.SeekerGame().BeautyImpact * meanBeauty), 0), 1);
}

/// <summary>
/// The comfort tier advised for a station node. Option is the piece (default for none); Priced is false when
/// the choice could not be priced and is the current behaviour: the planner's own bed at a sleep node, no chair
/// at a bench. Gain is the mood points a colonist gains over the current behaviour, BenefitW and CostW the mood
/// and the pieces' cost in sustained watts for the node.
/// </summary>
public sealed record ComfortChoice
{
    public string Node { get; init; } = string.Empty;

    public ComfortOption Option { get; init; } = new();

    public bool Priced { get; init; }

    public double Gain { get; init; }

    public double BenefitW { get; init; }

    public double CostW { get; init; }
}

/// <summary>What the comfort choice reads. Baseline is the piece the planner would otherwise build for the bed; Pawns the colonists, who share the work; Steel is steel's market value for BuildW.</summary>
public sealed record ComfortInput
{
    public ComfortModel Model { get; init; } = new();

    public required MoodModel Mood { get; init; }

    public required StationGraph Graph { get; init; }

    public string Baseline { get; init; } = string.Empty;

    public int Pawns { get; init; }

    public double WorkDayW { get; init; }

    public double ExpectedMood { get; init; }

    public double Steel { get; init; }
}

public static partial class PolicyFunctions
{
    private static readonly SeekerTable SeekerGameTable = new(
        IntervalTicks: 150,
        BeautyImpact: 0.1,
        Comfort: new SeekerGameDef("Comfort", "NeedComfort", new SeekerBands(new EquatableArray<double>([0.1, 0.6, 0.7, 0.8, 0.9]), new EquatableArray<int>([0, -1, 1, 2, 3, 4]))),
        Beauty: new SeekerGameDef("Beauty", "NeedBeauty", new SeekerBands(new EquatableArray<double>([0.01, 0.15, 0.35, 0.65, 0.85, 0.99]), new EquatableArray<int>([0, 1, 2, -1, 3, 4, 5]), UpperClosed: true)));

    /// <summary>The table the bridge reads the needs with.</summary>
    public static SeekerTable SeekerGame() => SeekerGameTable;

    internal static IReadOnlyList<string> SeekerNeedDefs() => [SeekerGameTable.Beauty.Need, SeekerGameTable.Comfort.Need];

    internal static IReadOnlyList<string> SeekerThoughts() => [SeekerGameTable.Beauty.Thought, SeekerGameTable.Comfort.Thought];

    /// <summary>The need intervals in a day.</summary>
    internal static int EnvDaySteps() => (int)Math.Round((double)GameTime.TicksPerDay / SeekerGameTable.IntervalTicks, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Lays a colonist's day out from the schedule template: the sleep block on the bed (sleepTarget), the
    /// spots in order over the Anything hours (scaled down together when they ask for more than the day has),
    /// the rest idle. It starts at waking, so the day is one cycle.
    /// </summary>
    internal static IReadOnlyList<EnvSegment> EnvDay(double sleepTarget, IReadOnlyList<EnvSpot> spots)
    {
        var slots = ScheduleTemplate(new TraitEffects());
        var wake = 0;
        for (var h = 0; h < slots.Length; h++)
        {
            if (slots[(h + 23) % 24] == PolicyConstants.ScheduleSleep && slots[h] != PolicyConstants.ScheduleSleep)
            {
                wake = h;
                break;
            }
        }
        var free = slots.Count(s => s == PolicyConstants.ScheduleAnything);
        var asked = 0.0;
        foreach (var s in spots)
        {
            asked += Math.Max(s.Hours, 0);
        }
        var scale = 1.0;
        if (asked > free)
        {
            scale = free / asked;
        }
        var output = new List<EnvSegment>();
        var queue = spots.Select(s => Math.Max(s.Hours, 0) * scale).ToArray();
        for (var i = 0; i < 24; i++)
        {
            switch (slots[(wake + i) % 24])
            {
                case PolicyConstants.ScheduleSleep:
                    output.Add(new EnvSegment(1, sleepTarget, true));
                    break;
                case PolicyConstants.ScheduleAnything:
                    var room = 1.0;
                    for (var j = 0; j < queue.Length; j++)
                    {
                        if (room <= 0)
                        {
                            break;
                        }
                        var take = Math.Min(room, queue[j]);
                        if (take <= 0)
                        {
                            continue;
                        }
                        output.Add(new EnvSegment(take, spots[j].Target));
                        queue[j] -= take;
                        room -= take;
                    }
                    if (room > 1e-12)
                    {
                        output.Add(new EnvSegment(room, 0));
                    }
                    break;
                default:
                    output.Add(new EnvSegment(1, 0));
                    break;
            }
        }
        return output;
    }

    /// <summary>Keeps the options no other option beats on both comfort and cost, cheapest first: a dearer piece with no more comfort never wins.</summary>
    internal static List<ComfortOption> Undominated(IEnumerable<ComfortOption> options)
    {
        var sorted = options.ToList();
        sorted.Sort((a, b) =>
        {
            if (a.Build != b.Build)
            {
                return a.Build < b.Build ? -1 : 1;
            }
            if (a.Comfort != b.Comfort)
            {
                return a.Comfort > b.Comfort ? -1 : 1;
            }
            if (a.Def != b.Def)
            {
                return string.CompareOrdinal(a.Def, b.Def) < 0 ? -1 : 1;
            }
            return string.CompareOrdinal(a.Stuff, b.Stuff);
        });
        var output = new List<ComfortOption>();
        foreach (var o in sorted)
        {
            if (output.Count == 0 || o.Comfort > output[^1].Comfort)
            {
                output.Add(o);
            }
        }
        return output;
    }
}
