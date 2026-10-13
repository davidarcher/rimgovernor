using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Simulation.Supply;

/// <summary>The end-of-day state.</summary>
public sealed record DayReport
{
    public int Day { get; init; }

    public IReadOnlyDictionary<Good, double> Stock { get; init; } = new Dictionary<Good, double>();

    /// <summary>Days of stock at the next day's demand.</summary>
    public IReadOnlyDictionary<Good, double> Runway { get; init; } = new Dictionary<Good, double>();

    public IReadOnlyDictionary<string, double> Delivered { get; init; } = new Dictionary<string, double>();

    public IReadOnlyDictionary<Good, double> Unmet { get; init; } = new Dictionary<Good, double>();
}

/// <summary>The result of a run.</summary>
public sealed record Report
{
    public IReadOnlyList<DayReport> Days { get; init; } = [];

    /// <summary>The first day each good went unmet; goods never short are absent.</summary>
    public IReadOnlyDictionary<Good, int> FirstStarved { get; init; } = new Dictionary<Good, int>();

    public IReadOnlyDictionary<Good, int> StarvedDays { get; init; } = new Dictionary<Good, int>();

    /// <summary>Total per source.</summary>
    public IReadOnlyDictionary<string, double> Delivered { get; init; } = new Dictionary<string, double>();

    public IReadOnlyDictionary<Good, double> MinRunway { get; init; } = new Dictionary<Good, double>();

    /// <summary>The day each build was paid in full.</summary>
    public IReadOnlyDictionary<string, int> BuildDone { get; init; } = new Dictionary<string, int>();

    /// <summary>Whether any of g went unmet.</summary>
    public bool Starved(Good g) => FirstStarved.ContainsKey(g);
}

internal sealed class RunState
{
    public Dictionary<Good, double> Stock { get; } = [];

    public List<SourceRun> Sources { get; } = [];

    public int ThreatUntil { get; set; }

    public double PowerFactor { get; set; }

    public int PowerUntil { get; set; }

    public Dictionary<Good, double> CapLimit { get; } = [];

    public Dictionary<Good, int> CapUntil { get; } = [];

    public Dictionary<Good, Surge> Surge { get; } = [];

    public List<BuildState> Builds { get; } = [];

    public Dictionary<Good, bool> Latched { get; } = [];

    /// <summary>Consumption per finished day.</summary>
    public List<Dictionary<Good, double>> Used { get; } = [];

    public bool Threat(int day) => day < ThreatUntil;

    public double Power(World w, int day) => day < PowerUntil ? w.Power * PowerFactor : w.Power;

    /// <summary>The headroom for g under the storage ceiling, +Inf when unbounded.</summary>
    public double Room(World w, Good g, int day)
    {
        var ok = w.Capacity.TryGetValue(g, out var limit);
        if (CapLimit.TryGetValue(g, out var l) && day < CapUntil.GetValueOrDefault(g))
        {
            limit = l;
            ok = true;
        }
        if (!ok)
        {
            return double.PositiveInfinity;
        }
        return Math.Max(0, limit - Stock.GetValueOrDefault(g));
    }

    /// <summary>The day's draw per good including active surges.</summary>
    public Dictionary<Good, double> Demand(World w, int day)
    {
        var d = DemandOf(w, day);
        foreach (var (g, s) in Surge)
        {
            if (day < s.Until)
            {
                d[g] = d.GetValueOrDefault(g) * s.Factor;
            }
        }
        return d;
    }

    public SourceRun? FindSource(string id)
    {
        foreach (var s in Sources)
        {
            if (s.Config.Id == id)
            {
                return s;
            }
        }
        return null;
    }

    public void Command(IReadOnlyList<Command> cmds, int day)
    {
        foreach (var c in cmds)
        {
            var s = FindSource(c.Source);
            if (s is null || s.Removed)
            {
                continue;
            }
            if (c.Kind == CommandKind.Open && !s.Open)
            {
                s.Open = true;
                s.OpenedDay = day;
            }
            else if (c.Kind == CommandKind.Close)
            {
                s.Open = false;
            }
        }
    }

    public WorldView View(World w, int day)
    {
        var demand = Demand(w, day);
        var stock = new Dictionary<Good, double>(Stock);
        var runway = new Dictionary<Good, double>();
        foreach (var (g, d) in demand)
        {
            if (d > 0)
            {
                runway[g] = Stock.GetValueOrDefault(g) / d;
            }
        }
        var floors = new List<FloorView>();
        var forecasts = new Dictionary<Good, Forecast>();
        foreach (var f in w.Floors)
        {
            var have = Stock.GetValueOrDefault(f.Good);
            var latched = Latched.GetValueOrDefault(f.Good);
            floors.Add(new FloorView(f.Good, latched, latched ? Math.Max(0, f.Target - have) : 0, have > f.Max, f.Min, f.Target, f.Max));
            if (Forecast(f, day) is { } fc)
            {
                forecasts[f.Good] = fc;
            }
        }
        var builds = new List<BuildView>();
        foreach (var b in Builds)
        {
            if (!b.Done && day >= b.Build.Day)
            {
                builds.Add(new BuildView(b.Build.Name, new Dictionary<Good, double>(b.Left)));
            }
        }
        return new WorldView
        {
            Day = day,
            Tick = new Tick((long)day * GameTime.TicksPerDay),
            Stock = stock,
            Demand = demand,
            Runway = runway,
            LaborBudget = w.LaborBudget,
            Threat = Threat(day),
            Sources = Sources.Select(s => s.View(day)).ToArray(),
            Floors = floors,
            Builds = builds,
            Forecast = forecasts,
        };
    }

    /// <summary>
    /// Updates each floor's latch from the stock: on below Min, off above Target (the policy wood latch,
    /// rounds.go latchValue).
    /// </summary>
    public void Latch(World w)
    {
        foreach (var f in w.Floors)
        {
            var v = Stock.GetValueOrDefault(f.Good);
            Latched[f.Good] = Latched.GetValueOrDefault(f.Good) ? v <= f.Target : v < f.Min;
        }
    }

    // Mirrors policy.ForecastResourceRunway: the consumption rate over up to ForecastHistoryDays finished
    // days, and stock above the floor's Min reserve plus unopened finite ore over that rate against the
    // horizon. Under one day of history there is no forecast.
    private Forecast? Forecast(Floor f, int day)
    {
        var n = Math.Min(day, SupplyConstants.ForecastHistoryDays);
        if (n < 1)
        {
            return null;
        }
        var used = 0.0;
        foreach (var u in Used.Skip(Used.Count - n))
        {
            used += u.GetValueOrDefault(f.Good);
        }
        var rate = used / n;
        if (rate == 0)
        {
            return new Forecast(rate, -1, Stock.GetValueOrDefault(f.Good) < f.Min);
        }
        var usable = Math.Max(0, Stock.GetValueOrDefault(f.Good) - f.Min);
        foreach (var s in Sources)
        {
            if (s.Config.Finite && !s.Open && !s.Removed)
            {
                foreach (var y in s.Config.Yields)
                {
                    if (y.Good == f.Good)
                    {
                        usable += s.Stock * y.PerUnit;
                    }
                }
            }
        }
        var daysLeft = usable / rate;
        return new Forecast(rate, daysLeft, daysLeft < SupplyConstants.ForecastHorizonDays);
    }

    // Pays out every source that can work today; when their labor exceeds the budget every delivery is
    // scaled by the same fraction.
    public void Deliver(World w, int day, Dictionary<string, double> output)
    {
        var avail = new double[Sources.Count];
        var labor = 0.0;
        var power = Power(w, day);
        var room = new Dictionary<Good, double>();
        for (var i = 0; i < Sources.Count; i++)
        {
            var s = Sources[i];
            var a = s.Available(day);
            if (s.Config.Safe && Threat(day))
            {
                a = 0;
            }
            if (a > 0 && s.Config.PowerDraw > 0)
            {
                if (power < s.Config.PowerDraw)
                {
                    a = 0;
                }
                else
                {
                    power -= s.Config.PowerDraw;
                }
            }
            foreach (var c in s.Config.Costs)
            {
                if (c.PerUnit > 0)
                {
                    a = Math.Min(a, Stock.GetValueOrDefault(c.Good) / c.PerUnit);
                }
            }
            foreach (var y in s.Config.Yields)
            {
                if (!room.TryGetValue(y.Good, out var headroom))
                {
                    headroom = Room(w, y.Good, day);
                    room[y.Good] = headroom;
                }
                if (y.PerUnit > 0)
                {
                    a = Math.Min(a, headroom / y.PerUnit);
                }
            }
            foreach (var y in s.Config.Yields)
            {
                room[y.Good] -= a * y.PerUnit;
            }
            avail[i] = a;
            if (a > 0)
            {
                labor += s.Config.Labor;
            }
        }
        var frac = 1.0;
        if (labor > 0)
        {
            frac = Math.Min(1, w.LaborBudget / labor);
        }
        for (var i = 0; i < Sources.Count; i++)
        {
            var s = Sources[i];
            var taken = avail[i] * frac;
            if (taken <= 0)
            {
                s.Last = 0;
                continue;
            }
            foreach (var y in s.Config.Yields)
            {
                Stock[y.Good] = Stock.GetValueOrDefault(y.Good) + (taken * y.PerUnit);
            }
            foreach (var c in s.Config.Costs)
            {
                Stock[c.Good] = Stock.GetValueOrDefault(c.Good) - (taken * c.PerUnit);
            }
            s.Take(taken);
            output[s.Config.Id] = World.Round(taken);
        }
    }

    public void Consume(World w, int day, Dictionary<Good, double> unmet, Dictionary<string, int> buildDone)
    {
        var used = new Dictionary<Good, double>();
        foreach (var (g, d) in Demand(w, day))
        {
            var have = Stock.GetValueOrDefault(g);
            if (have >= d)
            {
                Stock[g] = have - d;
                used[g] = used.GetValueOrDefault(g) + d;
                continue;
            }
            var short_ = d - have;
            if (short_ > 1e-9)
            {
                unmet[g] = short_;
            }
            used[g] = used.GetValueOrDefault(g) + have;
            Stock[g] = 0;
        }
        // Builds draw what stock allows; a shortfall stays open, not unmet.
        foreach (var b in Builds)
        {
            if (b.Done || day < b.Build.Day)
            {
                continue;
            }
            b.Done = true;
            foreach (var (g, n) in b.Left.ToArray())
            {
                var pay = Math.Min(n, Stock.GetValueOrDefault(g));
                Stock[g] = Stock.GetValueOrDefault(g) - pay;
                used[g] = used.GetValueOrDefault(g) + pay;
                b.Left[g] = n - pay;
                if (b.Left[g] > 1e-9)
                {
                    b.Done = false;
                }
            }
            if (b.Done)
            {
                buildDone[b.Build.Name] = day;
            }
        }
        Used.Add(used);
    }

    internal static Dictionary<Good, double> DemandOf(World w, int day)
    {
        var d = new Dictionary<Good, double>();
        foreach (var c in w.Consumers)
        {
            d[c.Good] = d.GetValueOrDefault(c.Good) + Math.Max(0, c.Demand(day));
        }
        return d;
    }
}

public sealed partial record World
{
    /// <summary>
    /// Simulates <paramref name="days"/> days under <paramref name="planner"/> (null opens nothing beyond each
    /// source's initial state). It is deterministic in <see cref="Seed"/> and never mutates the world.
    /// </summary>
    public Report Run(IPlanner? planner, int days)
    {
        var r = new RunState();
        foreach (var b in Builds)
        {
            var left = new Dictionary<Good, double>();
            foreach (var c in b.Costs)
            {
                left[c.Good] = left.GetValueOrDefault(c.Good) + c.PerUnit;
            }
            r.Builds.Add(new BuildState(b, left));
        }
        foreach (var (g, v) in Stock)
        {
            r.Stock[g] = v;
        }
        for (var i = 0; i < Sources.Count; i++)
        {
            r.Sources.Add(new SourceRun(Sources[i], Seed, i));
        }
        var dayReports = new List<DayReport>();
        var firstStarved = new Dictionary<Good, int>();
        var starvedDays = new Dictionary<Good, int>();
        var minRunway = new Dictionary<Good, double>();
        var buildDone = new Dictionary<string, int>();
        for (var day = 0; day < days; day++)
        {
            foreach (var sh in Shocks)
            {
                if (sh.Day == day)
                {
                    sh.Apply(r);
                }
            }
            r.Latch(this);
            if (planner is not null)
            {
                r.Command(planner.Plan(r.View(this, day)), day);
            }
            foreach (var s in r.Sources)
            {
                s.Arrive(day);
            }
            var delivered = new Dictionary<string, double>();
            var unmet = new Dictionary<Good, double>();
            r.Deliver(this, day, delivered);
            r.Consume(this, day, unmet, buildDone);
            foreach (var (g, f) in Spoilage)
            {
                r.Stock[g] = r.Stock.GetValueOrDefault(g) * (1 - Shock.Clamp01(f));
            }
            foreach (var s in r.Sources)
            {
                s.Grow(day);
            }
            var stock = new Dictionary<Good, double>();
            var runway = new Dictionary<Good, double>();
            foreach (var (g, v) in r.Stock)
            {
                stock[g] = Round(v);
            }
            foreach (var (g, d) in r.Demand(this, day + 1))
            {
                if (d > 0)
                {
                    var rw = Round(r.Stock.GetValueOrDefault(g) / d);
                    runway[g] = rw;
                    if (!minRunway.TryGetValue(g, out var m) || rw < m)
                    {
                        minRunway[g] = rw;
                    }
                }
            }
            foreach (var g in unmet.Keys.ToArray())
            {
                unmet[g] = Round(unmet[g]);
                firstStarved.TryAdd(g, day);
                starvedDays[g] = starvedDays.GetValueOrDefault(g) + 1;
            }
            dayReports.Add(new DayReport { Day = day, Stock = stock, Runway = runway, Delivered = delivered, Unmet = unmet });
        }
        var total = new Dictionary<string, double>();
        foreach (var s in r.Sources)
        {
            total[s.Config.Id] = Round(s.Delivered);
        }
        return new Report
        {
            Days = dayReports,
            FirstStarved = firstStarved,
            StarvedDays = starvedDays,
            Delivered = total,
            MinRunway = minRunway,
            BuildDone = buildDone,
        };
    }

    /// <summary>Trims float noise so reports compare across platforms.</summary>
    internal static double Round(double v) => Math.Round(v * 1e6, MidpointRounding.AwayFromZero) / 1e6;
}
