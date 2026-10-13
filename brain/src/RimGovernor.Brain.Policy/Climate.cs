namespace RimGovernor.Brain.Policy;

// Go: climate.go
// Climate model (#2940, replacing #2880's vent count): the sustained extra heater or cooler power, in watts,
// that holds a room in its temperature band given its openings (vent, door held open, door closed, airlock),
// the outdoor curve and the heat the openings move. Every number is the game's: the equalize interval and rate
// of a vent and of a door come from the defs and the game's constants (ClimateModel, read by the bridge), the
// heater's and cooler's heat per second and power draw from the catalog, and the one judgment (the sun cycle's
// phase, which no def carries) from climateTable, a DefTables entry. The tiler prices vents and airlocks with
// it and the climate envelope round builds what it chose.
//
// The heat given off inside the room (#2915) is in: each station with a CompProperties_HeatPusher (stoves,
// smelters, torches) pushes heatPerSecond into its room whenever the room's temperature lies between its
// heatPushMinTemperature and heatPushMaxTemperature (CompHeatPusher.ShouldPushHeatNow; the powered subclass
// adds only "has power and fuel", always true of a station the colony runs). That load is what a vent sheds
// faster than a closed door: it wins where the load pushes the room out of its band. A cooler's hot side
// (Building_Cooler pushes 1.25 times its output) stands on the far side of the wall, so it loads the
// neighbour, not the room it cools.
//
// Left out, because every option pays it alike: heat leaking through walls and roofs, weather offsets and
// drift inside a twelfth of the year. The room has no thermal inertia: at each step of a day resolved year it
// is held where the openings and the heat inside would float it, clamped into the band, and the power is the
// heat that holding it costs.

/// <summary>A model, room or curve the model cannot price (Go <c>ErrClimate</c>).</summary>
public sealed class ClimateException(string message) : DomainException("climate model: " + message);

/// <summary>
/// The model's one judgment table, registered in DefTables so a renamed def fails the dangling-reference gate.
/// The defs are the buildings the options are priced in; SunPhase is the phase of
/// GenTemperature.OffsetFromSunCycle, cos(2pi(dayPct+SunPhase)) times
/// TemperatureTuning.DailyTempVariationAmplitude: a literal in the game's code that no def or constant states.
/// RaceDef and its two stats are where a room people live in takes its band (the race's comfortable range; the
/// stat def's own base is the unclothed 0..40 and not the range a colonist is comfortable in). A neighbouring
/// room is held at the default target of the catalog's heater (ClimateModel.HeldC): the game's own default for
/// a heated room.
/// </summary>
public sealed record ClimateTable
{
    public string VentDef { get; init; } = "";

    public string DoorDef { get; init; } = "";

    public string WallDef { get; init; } = "";

    public string RaceDef { get; init; } = "";

    public string ComfyMinStat { get; init; } = "";

    public string ComfyMaxStat { get; init; } = "";

    public double SunPhase { get; init; }

    /// <summary>
    /// The ticks a pawn takes to walk one cell, the move cost the pathfinder charges (pathMoveCardinal): a
    /// pawn walks two cells in a door's window, into the door's cell and out of it. Pawns differ in speed and
    /// no def states one, so it is a judgment.
    /// </summary>
    public double CellMoveTicks { get; init; }
}

/// <summary>
/// How a building equalizes the rooms either side of it: GenTemperature.EqualizeTemperaturesThroughBuilding
/// every <see cref="IntervalTicks"/>, each room moving Rate/cells of the way to the pair's mean, the cells
/// capped at Rate (the game's num3).
/// </summary>
public sealed record Exchange
{
    public int IntervalTicks { get; init; }

    public double Rate { get; init; }

    /// <summary>
    /// The heat (cell-degrees C) one tick moves per degree of difference between the sides, when the smaller
    /// room has <paramref name="cells"/> floor cells: an event moves (difference/2)*min(Rate, cells).
    /// </summary>
    internal double Conductance(int cells) => Math.Min(Rate, cells) / 2 / IntervalTicks;

    internal bool Valid() => IntervalTicks > 0 && PolicyFunctions.FinitePositive(Rate);
}

/// <summary>
/// How long a door pawns use stays open for one passage, from the door's def and the game's constants.
/// <see cref="OpenTicks"/> is Building_Door.TicksToOpenNow (OpenTicks 45 over the def's DoorOpenSpeed, times
/// the unpowered factor): the pawn waits that long at a closed door, which exchanges heat from the first tick.
/// <see cref="CloseDelayTicks"/> is the delay after the last pawn leaves (CloseDelayTicks 110 times the def's
/// unpowered close factor). <see cref="MoveTicks"/> is the walk through the door's cell, two cell moves
/// (climateTable.CellMoveTicks).
/// </summary>
public sealed record DoorTiming
{
    public double OpenTicks { get; init; }

    public double CloseDelayTicks { get; init; }

    public double MoveTicks { get; init; }

    internal bool Valid() =>
        PolicyFunctions.FinitePositive(OpenTicks) && PolicyFunctions.FinitePositive(CloseDelayTicks) && MoveTicks >= 0 && !double.IsInfinity(MoveTicks);

    /// <summary>The ticks one passage holds a door open: the wait, the walk through and the close delay.</summary>
    internal double Window() => OpenTicks + MoveTicks + CloseDelayTicks;
}

/// <summary>
/// A heater or cooler: the heat it moves per second (CompProperties_TempControl.energyPerSecond, as a positive
/// number) at the power it draws.
/// </summary>
public sealed record ClimatePlant
{
    public double HeatPerSecond { get; init; }

    public double PowerW { get; init; }

    internal bool Valid() => PolicyFunctions.FinitePositive(HeatPerSecond) && PolicyFunctions.FinitePositive(PowerW);
}

/// <summary>
/// The market value of the cheapest cost list of the buildings the options build: a vent, a door and a wall,
/// and of the heater and cooler the model runs. A plant whose cost is unknown is zero: unpriced, not a veto.
/// </summary>
public sealed record ClimateBuildValue
{
    public double Vent { get; init; }

    public double Door { get; init; }

    public double Wall { get; init; }

    public double Heater { get; init; }

    public double Cooler { get; init; }

    /// <summary>What an opening costs to build beyond the door that stands there anyway: a vent, or an airlock's second door and two walls.</summary>
    internal double BuildValue(Opening o) =>
        o switch
        {
            Opening.Vent => Vent,
            Opening.Airlock => Door + (2 * Wall),
            _ => 0,
        };
}

/// <summary>The temperature range a room is held in; an open end is infinite.</summary>
public readonly record struct Band(double MinC, double MaxC);

/// <summary>
/// Everything the game states that the model reads. The zero value is unknown (Valid is false): a hand-built
/// catalog gets no advice.
/// </summary>
public sealed record ClimateModel
{
    /// <summary>The exchange of a vent (TickRare, TemperatureTuning.Vent_TempEqualizeRate).</summary>
    public Exchange Vent { get; init; } = new();

    /// <summary>The exchange of a door held open (its BuildingProperties interval and rate).</summary>
    public Exchange DoorOpen { get; init; } = new();

    /// <summary>The exchange of a door held shut.</summary>
    public Exchange DoorClosed { get; init; } = new();

    /// <summary>The time one passage holds a door open.</summary>
    public DoorTiming Door { get; init; } = new();

    /// <summary>The catalog's cheapest powered heater.</summary>
    public ClimatePlant Heater { get; init; } = new();

    /// <summary>The catalog's most efficient cooler.</summary>
    public ClimatePlant Cooler { get; init; } = new();

    /// <summary>The model's time step (GenTicks.TickRareInterval, the vent's).</summary>
    public int StepTicks { get; init; }

    /// <summary>The game's ticks per second (GenTicks.TicksPerRealSecond), the unit the plants' heat per second is in.</summary>
    public double TicksPerSecond { get; init; }

    /// <summary>The sun cycle's amplitude, TemperatureTuning.DailyTempVariationAmplitude.</summary>
    public double SwingC { get; init; }

    public ClimateBuildValue Build { get; init; } = new();

    /// <summary>
    /// The band a room people live in is held in (the race's comfortable range). Zero for a catalog that
    /// cannot state it (HasLiving false); the tiler needs it, the cold group's advice does not.
    /// </summary>
    public Band Living { get; init; }

    /// <summary>The temperature a heated neighbour is held at (the heater's default target).</summary>
    public double HeldC { get; init; }

    /// <summary>A model that states the living band and the temperature a heated neighbour is held at.</summary>
    public bool HasLiving() =>
        Valid()
        && Living.MinC < Living.MaxC
        && !double.IsInfinity(Living.MinC)
        && !double.IsInfinity(Living.MaxC)
        && !double.IsNaN(HeldC)
        && !double.IsInfinity(HeldC);

    /// <summary>A model with every number present.</summary>
    public bool Valid() =>
        Vent.Valid()
        && DoorOpen.Valid()
        && DoorClosed.Valid()
        && Door.Valid()
        && Heater.Valid()
        && Cooler.Valid()
        && StepTicks > 0
        && PolicyFunctions.FinitePositive(TicksPerSecond)
        && !double.IsNaN(SwingC)
        && !double.IsInfinity(SwingC);

    /// <summary>
    /// The heat one opening moves per tick per degree of difference. <paramref name="passages"/> is the pawn
    /// passages a day through the opening (a door's or an airlock's; the others ignore it).
    /// </summary>
    internal double Conductance(ClimateLink l, int cells, double passages)
    {
        if (!l.Side.Outdoors && l.Side.Cells > 0)
        {
            cells = Math.Min(cells, l.Side.Cells);
        }

        switch (l.Opening)
        {
            case Opening.Vent:
                return Vent.Conductance(cells);
            case Opening.DoorOpen:
                return DoorOpen.Conductance(cells);
            case Opening.DoorClosed:
                return DoorClosed.Conductance(cells);
            case Opening.Door:
                var f = PolicyFunctions.OpenFraction(passages, Door.Window());
                return (f * DoorOpen.Conductance(cells)) + ((1 - f) * DoorClosed.Conductance(cells));
            case Opening.Airlock:
                return AirlockConductance(passages);
            default:
                return 0;
        }
    }

    /// <summary>
    /// The airlock's conductance with <paramref name="passages"/> a day. A passage opens the outer door for the
    /// door's window; the pawn walks to the vestibule one wait and one door-cell walk later, where the inner
    /// door opens a close delay before the outer shuts. The two are open together for that close delay (the
    /// overlap) and each alone for the rest of its window. The time splits into overlap, one door open (the
    /// other closed) and both closed, each stream of windows a Poisson cover as a door's.
    ///
    /// With both open, the one-cell vestibule sits between two events of one interval, each moving the fraction
    /// f of the way to its side, so the heat through per event pair is 1/(2-f) of one open door's per event:
    /// 2/3 at f=1/2, where a one-cell room moves min(rate, 1)/2. One open door leaves the closed one's leak in
    /// series.
    /// </summary>
    internal double AirlockConductance(double passages)
    {
        var open = DoorOpen.Conductance(PolicyFunctions.AirlockVestibuleCells);
        var closed = DoorClosed.Conductance(PolicyFunctions.AirlockVestibuleCells);
        var f = Math.Min(DoorOpen.Rate, PolicyFunctions.AirlockVestibuleCells) / 2;
        var both = PolicyFunctions.OpenFraction(passages, Door.CloseDelayTicks);
        var one = Math.Min(Math.Max(0, 2 * (PolicyFunctions.OpenFraction(passages, Door.Window()) - both)), 1 - both);
        return (both * open / (2 - f)) + (one * PolicyFunctions.Series(open, closed)) + ((1 - both - one) * PolicyFunctions.Series(closed, closed));
    }

    /// <summary>
    /// The build cost, as sustained watts, of the heaters and coolers that hold <paramref name="watts"/>'
    /// load: as many of each as its peak draw fills (a plant is a whole building), each priced at the
    /// catalog's heater or cooler. A plant whose cost is unknown adds nothing.
    /// </summary>
    public double PlantsW(SiteTypeWeights w, double steelValue, ClimateWatts watts)
    {
        static double Units(double peakW, double plantW) => Math.Ceiling((peakW / plantW) - 1e-9);

        return (Units(watts.HeatingPeakW, Heater.PowerW) * PolicyFunctions.BuildW(w, Build.Heater, steelValue))
            + (Units(watts.CoolingPeakW, Cooler.PowerW) * PolicyFunctions.BuildW(w, Build.Cooler, steelValue));
    }
}

/// <summary>
/// The heat a station gives off into its room: <see cref="HeatPerSecond"/> while the room's temperature is
/// between MinC and MaxC (exclusive), as CompProperties_HeatPusher states. Negative heat cools (the passive
/// cooler).
/// </summary>
public sealed record HeatSource
{
    public double HeatPerSecond { get; init; }

    public double MinC { get; init; }

    public double MaxC { get; init; }

    /// <summary>A source that gives off any heat.</summary>
    public bool Pushes() => HeatPerSecond != 0;
}

/// <summary>One way a room is open to its neighbour.</summary>
public enum Opening
{
    None,

    /// <summary>A vent in the wall (the door beside it stays closed).</summary>
    [WireName("vent")]
    Vent,

    /// <summary>A door held open.</summary>
    [WireName("door_open")]
    DoorOpen,

    /// <summary>A door held shut.</summary>
    [WireName("door_closed")]
    DoorClosed,

    /// <summary>
    /// A door pawns use: open for the share of the time the passages through it hold it, shut otherwise. It is
    /// priced beside an airlock (OpeningOrder, the list of the vent site, leaves it out).
    /// </summary>
    [WireName("door")]
    Door,

    /// <summary>
    /// Two doors around a one-cell vestibule. A pawn opens them one after the other, and the inner one opens
    /// while the outer is still in its close delay, so each passage has a window with both open.
    /// </summary>
    [WireName("airlock")]
    Airlock,
}

/// <summary>What is beyond an opening: the outdoors, following the curve, or a room held at HeldC with Cells floor cells.</summary>
public sealed record ClimateSide
{
    public bool Outdoors { get; init; }

    public double HeldC { get; init; }

    public int Cells { get; init; }
}

/// <summary>One opening of a room.</summary>
public sealed record ClimateLink
{
    public Opening Opening { get; init; }

    public ClimateSide Side { get; init; } = new();
}

/// <summary>A roofed room: its floor cells, band and openings.</summary>
public sealed record ClimateRoom
{
    public int Cells { get; init; }

    public Band Band { get; init; }

    public EquatableArray<ClimateLink> Links { get; init; }

    /// <summary>What the room's own stations give off.</summary>
    public EquatableArray<HeatSource> Heat { get; init; }
}

/// <summary>
/// The power a room's openings cost over a year: the mean and peak electrical watts of the heaters and
/// coolers that hold its band, and the mean and peak of each (the peaks size the plants: a plant is bought for
/// the load it holds at its worst step).
/// </summary>
public sealed record ClimateWatts
{
    public double MeanW { get; init; }

    public double PeakW { get; init; }

    public double HeatingW { get; init; }

    public double CoolingW { get; init; }

    public double HeatingPeakW { get; init; }

    public double CoolingPeakW { get; init; }
}

/// <summary>
/// One opening's price: the operating watts and the build cost as watts (the opening's own, and the heaters
/// and coolers its load needs); <see cref="TotalW"/> is their sum, the number options are compared by.
/// </summary>
public sealed record ClimateOption
{
    public Opening Opening { get; init; }

    public ClimateWatts Watts { get; init; } = new();

    public double BuildW { get; init; }

    public double TotalW { get; init; }
}

/// <summary>The climate answer beside a cold group's cooling station: the group's room priced against the outdoors through each opening.</summary>
public sealed record GroupClimate
{
    public int Cells { get; init; }

    public Band Band { get; init; }

    public EquatableArray<ClimateOption> Options { get; init; }

    public Opening Best { get; init; }
}

public static partial class PolicyFunctions
{
    private static readonly ClimateTable _climateTable = new()
    {
        VentDef = "Vent",
        DoorDef = ShellDoorDefinition,
        WallDef = ShellWallDefinition,
        RaceDef = "Human",
        ComfyMinStat = "ComfyTemperatureMin",
        ComfyMaxStat = "ComfyTemperatureMax",
        SunPhase = 0.32,
        CellMoveTicks = PathMoveCardinal,
    };

    /// <summary>The building defs the climate options are priced in.</summary>
    public static ClimateTable ClimateDefs() => _climateTable;

    /// <summary>The airlock's one floor cell (PlannedAirlock).</summary>
    internal const int AirlockVestibuleCells = 1;

    /// <summary>The order options are listed and ties are broken in.</summary>
    public static readonly IReadOnlyList<Opening> OpeningOrder = [Opening.Vent, Opening.DoorOpen, Opening.DoorClosed, Opening.Airlock];

    /// <summary>
    /// The fraction of the time covered by windows of the given ticks when passages a day each start one (a
    /// Poisson stream: windows that overlap share the open time).
    /// </summary>
    internal static double OpenFraction(double passages, double ticks)
    {
        if (!(passages > 0) || double.IsInfinity(passages) || !(ticks > 0))
        {
            return 0;
        }

        return 1 - Math.Exp(-passages * ticks / GameTime.TicksPerDay);
    }

    /// <summary>The conductance of two openings one after the other.</summary>
    internal static double Series(double a, double b) => a * b / (a + b);

    /// <summary>
    /// Prices a room over one day-resolved year of the outdoor curve (twelve means, C): at each step the room
    /// floats to the conductance-weighted mean of what is beyond its openings, is held at the band edge it
    /// floats past, and the heat that costs is turned into heater or cooler watts.
    /// </summary>
    public static ClimateWatts ClimateLoad(ClimateModel m, IReadOnlyList<double> curve, ClimateRoom room, double passages)
    {
        if (!m.Valid())
        {
            throw new ClimateException("model has unknown numbers");
        }

        if (curve.Count != 12 || curve.Any(v => double.IsNaN(v) || double.IsInfinity(v)))
        {
            throw new ClimateException("outdoor curve is not twelve finite means");
        }

        if (room.Cells <= 0 || double.IsNaN(room.Band.MinC) || double.IsNaN(room.Band.MaxC) || room.Band.MinC > room.Band.MaxC)
        {
            throw new ClimateException($"room has {room.Cells} cells and band {room.Band}");
        }

        var links = room.Links.Items;
        var g = new double[links.Count];
        for (var i = 0; i < links.Count; i++)
        {
            g[i] = m.Conductance(links[i], room.Cells, passages);
        }

        var inside = InsideHeat.Create(room.Heat.Items, m.TicksPerSecond);
        double heatingSum = 0, coolingSum = 0, peak = 0, heatingPeak = 0, coolingPeak = 0;
        var steps = 0;
        for (var tick = 0; tick < GameTime.TicksPerYear; tick += m.StepTicks)
        {
            steps++;
            double flow = 0, total = 0;
            for (var i = 0; i < links.Count; i++)
            {
                var side = links[i].Side.HeldC;
                if (links[i].Side.Outdoors)
                {
                    side = OutdoorAt(curve, m.SwingC, tick);
                }

                flow += g[i] * side;
                total += g[i];
            }

            if (total == 0)
            {
                continue;
            }

            // heat and cool are the heat per tick (cell-degrees) held at the edge, beyond what the room's own
            // stations give off at that temperature.
            double heat = 0, cool = 0;
            var floating = inside.Floats(flow, total);
            if (floating < room.Band.MinC)
            {
                heat = Math.Max(0, (room.Band.MinC * total) - flow - inside.PushAt(room.Band.MinC));
            }
            else if (floating > room.Band.MaxC)
            {
                cool = Math.Max(0, flow + inside.PushAt(room.Band.MaxC) - (room.Band.MaxC * total));
            }

            var heatingW = heat * m.TicksPerSecond / m.Heater.HeatPerSecond * m.Heater.PowerW;
            var coolingW = cool * m.TicksPerSecond / m.Cooler.HeatPerSecond * m.Cooler.PowerW;
            heatingSum += heatingW;
            coolingSum += coolingW;
            peak = Math.Max(peak, heatingW + coolingW);
            heatingPeak = Math.Max(heatingPeak, heatingW);
            coolingPeak = Math.Max(coolingPeak, coolingW);
        }

        var heatingMean = heatingSum / steps;
        var coolingMean = coolingSum / steps;
        return new ClimateWatts
        {
            HeatingW = heatingMean,
            CoolingW = coolingMean,
            PeakW = peak,
            HeatingPeakW = heatingPeak,
            CoolingPeakW = coolingPeak,
            MeanW = heatingMean + coolingMean,
        };
    }

    /// <summary>
    /// The outdoor temperature at a tick: the twelfths' means interpolated linearly between their midpoints
    /// (around the year), plus the diurnal swing.
    /// </summary>
    private static double OutdoorAt(IReadOnlyList<double> temps, double swingC, int tick)
    {
        const int twelfths = 12;
        var year = tick % GameTime.TicksPerYear;
        var pos = (year / ((double)GameTime.TicksPerYear / twelfths)) - 0.5;
        if (pos < 0)
        {
            pos += twelfths;
        }

        var i = (int)pos;
        var frac = pos - i;
        var mean = (temps[i % twelfths] * (1 - frac)) + (temps[(i + 1) % twelfths] * frac);
        var dayPct = (double)(tick % GameTime.TicksPerDay) / GameTime.TicksPerDay;
        return mean + (Math.Cos(2 * Math.PI * (dayPct + _climateTable.SunPhase)) * swingC);
    }

    /// <summary>
    /// The one-time cost of a building list as sustained watts: the cost in steel (its market value over
    /// steel's) charged at the layout's own rate, the site pricing's (SiteTypeWeights: Construction per 100
    /// steel a day against Power per kilowatt averaged over the day). The weights are per day and already
    /// amortised: Construction's 2.6 is 100 steel (190 silver) over one 60-day year
    /// (DefaultSiteTypeWeights), so the formula divides by no horizon.
    /// </summary>
    public static double BuildW(SiteTypeWeights w, double value, double steelValue)
    {
        const int steelPerCharge = 100;
        if (!FinitePositive(steelValue) || !FinitePositive(w.Power))
        {
            return 0;
        }

        return UnitsW(w, value / steelValue / steelPerCharge * w.Construction);
    }

    /// <summary>Turns site-type units (a normal-soil cell's harvest per grow day) into sustained watts: Power units are a kilowatt averaged over the day.</summary>
    private static double UnitsW(SiteTypeWeights w, double units)
    {
        const int wattsPerKilowatt = 1000;
        return units / w.Power * wattsPerKilowatt;
    }

    /// <summary>
    /// Prices each of the openings a room could have toward <paramref name="side"/>, beside the links it
    /// already has, cheapest first-listed on a tie. An opening the caller leaves out (an unresearched vent) is
    /// absent, not a veto.
    /// </summary>
    public static List<ClimateOption> ClimateOptions(
        ClimateModel m,
        SiteTypeWeights w,
        double steelValue,
        IReadOnlyList<double> curve,
        ClimateRoom room,
        ClimateSide side,
        double passages,
        IEnumerable<Opening> openings
    )
    {
        var output = new List<ClimateOption>();
        foreach (var o in openings)
        {
            var r = room with { Links = EquatableArray.From(room.Links.Items.Append(new ClimateLink { Opening = o, Side = side })) };
            var watts = ClimateLoad(m, curve, r, passages);
            var build = BuildW(w, m.Build.BuildValue(o), steelValue) + m.PlantsW(w, steelValue, watts);
            output.Add(
                new ClimateOption
                {
                    Opening = o,
                    Watts = watts,
                    BuildW = build,
                    TotalW = watts.MeanW + build,
                }
            );
        }

        return output;
    }

    /// <summary>The cheapest option by total watts, the first listed on a tie; null for none.</summary>
    public static ClimateOption? BestClimateOption(IReadOnlyList<ClimateOption> options)
    {
        var best = -1;
        for (var i = 0; i < options.Count; i++)
        {
            if (best < 0 || options[i].TotalW < options[best].TotalW)
            {
                best = i;
            }
        }

        return best < 0 ? null : options[best];
    }

    /// <summary>The heat the group's stations give off into its room, one source per station def at its count.</summary>
    public static List<HeatSource> GroupHeat(this StationCatalog c, RoomGroup g)
    {
        var output = new List<HeatSource>();
        foreach (var s in g.Stations.Items)
        {
            if (!c.Station(s.Def, out var st) || !st.Heat.Pushes() || s.Count <= 0)
            {
                continue;
            }

            output.Add(st.Heat with { HeatPerSecond = st.Heat.HeatPerSecond * s.Count });
        }

        return output;
    }

    /// <summary>The catalog's cooling station that draws the least power per heat moved, null when it has none.</summary>
    public static string? BestCoolerByPower(this StationCatalog c) => BestCoolerOf(c);

    /// <summary>
    /// The climate answer beside a cold group's cooling station: null for a group that does not require
    /// cold, or while the curve, the full-rot temperature, the model or steel's value is unknown. The band is
    /// everything under the full-rot temperature (slows rot; it does not stop it).
    /// </summary>
    internal static GroupClimate? GroupClimateOf(StationFacts f, RoomGroup g)
    {
        var known = f.OutdoorCurve.TryGetValue(out var curve);
        var m = f.Catalog.Climate;
        if (!known || f.FullRotRateC <= 0 || !g.Requires.Items.Contains(EnvRequirement.Cold) || !m.Valid())
        {
            return null;
        }

        double steel;
        List<ClimateOption> options;
        var band = new Band(double.NegativeInfinity, f.FullRotRateC);
        try
        {
            steel = f.Items.MarketValue(SiteSteelUnit);
            options = ClimateOptions(
                m,
                DefaultSiteTypeWeights(),
                steel,
                curve.Items,
                new ClimateRoom
                {
                    Cells = g.Area,
                    Band = band,
                    Heat = EquatableArray.From(f.Catalog.GroupHeat(g)),
                },
                new ClimateSide { Outdoors = true },
                0,
                OpeningOrder
            );
        }
        catch (DomainException)
        {
            return null;
        }

        var output = new GroupClimate { Cells = g.Area, Band = band, Options = EquatableArray.From(options) };
        return BestClimateOption(options) is { } best ? output with { Best = best.Opening } : output;
    }
}

/// <summary>
/// A room's stations' heat as the model reads it: the heat per tick each gives off between its limits, cut
/// into the temperature intervals the limits bound so that a step finds where the room floats without sorting.
/// </summary>
internal sealed class InsideHeat
{
    private readonly List<TickHeat> _sources = [];

    /// <summary>The intervals between the limits, lowest first, with the heat given off throughout each.</summary>
    private readonly List<HeatPiece> _pieces = [];

    private readonly record struct TickHeat(double PerTick, double Min, double Max);

    private readonly record struct HeatPiece(double Lo, double Hi, double Push);

    public static InsideHeat Create(IEnumerable<HeatSource> sources, double ticksPerSecond)
    {
        var h = new InsideHeat();
        var edges = new List<double> { double.NegativeInfinity, double.PositiveInfinity };
        foreach (var s in sources)
        {
            if (!s.Pushes() || double.IsNaN(s.HeatPerSecond) || double.IsNaN(s.MinC) || double.IsNaN(s.MaxC))
            {
                continue;
            }

            h._sources.Add(new TickHeat(s.HeatPerSecond / ticksPerSecond, s.MinC, s.MaxC));
            edges.Add(s.MinC);
            edges.Add(s.MaxC);
        }

        if (h._sources.Count == 0)
        {
            return h;
        }

        edges.Sort();
        var distinct = edges.Distinct().ToList();
        for (var i = 0; i + 1 < distinct.Count; i++)
        {
            double lo = distinct[i], hi = distinct[i + 1], push = 0;
            foreach (var s in h._sources)
            {
                if (s.Min <= lo && s.Max >= hi)
                {
                    push += s.PerTick;
                }
            }

            h._pieces.Add(new HeatPiece(lo, hi, push));
        }

        return h;
    }

    /// <summary>The heat per tick given off with the room held at <paramref name="c"/>.</summary>
    public double PushAt(double c)
    {
        double output = 0;
        foreach (var s in _sources)
        {
            if (c > s.Min && c < s.Max)
            {
                output += s.PerTick;
            }
        }

        return output;
    }

    /// <summary>
    /// The temperature the room settles at when its openings carry <paramref name="flow"/> (conductance times
    /// the temperature beyond, summed) over <paramref name="total"/> conductance and its stations give off
    /// heat inside their limits: the first temperature where the heat given off at it holds the room there,
    /// else the limit a station reaches and switches off at (the room sits there, the station working part of
    /// the time).
    /// </summary>
    public double Floats(double flow, double total)
    {
        for (var i = 0; i < _pieces.Count; i++)
        {
            var p = _pieces[i];
            var t = (flow + p.Push) / total;
            if (t > p.Lo && t < p.Hi)
            {
                return t;
            }

            if (t >= p.Hi && i + 1 < _pieces.Count && (flow + _pieces[i + 1].Push) / total <= p.Hi)
            {
                return p.Hi;
            }
        }

        return flow / total;
    }
}
