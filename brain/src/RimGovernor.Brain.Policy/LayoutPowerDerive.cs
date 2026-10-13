namespace RimGovernor.Brain.Policy;

/// <summary>One generator definition as the site count sees it: nominal watts, the market value of its cheapest cost list and how it delivers over a day. The zero value is unknown.</summary>
public sealed record SupplyRow
{
    public double NominalW { get; init; }
    public double Build { get; init; }
    public PowerSourceProfile Profile { get; init; }

    internal bool Known() => PolicyFunctions.FinitePositive(NominalW) && PolicyFunctions.FinitePositive(Build) && PolicyFunctions.FinitePositive(Profile.Daily);
}

/// <summary>What the power sites are derived and priced from, read from the defs: the turbine and the solar generator, the battery, and the market value of the conduit's and the ring wall's cheapest cost lists, per cell. The zero value is unknown.</summary>
public sealed record PowerModel
{
    public SupplyRow Wind { get; init; } = new();
    public SupplyRow Solar { get; init; } = new();
    public PowerBattery Battery { get; init; }
    public double Conduit { get; init; }
    public double Wall { get; init; }

    internal bool Known() =>
        Wind.Known() && Solar.Known() && PolicyFunctions.FinitePositive(Conduit) && PolicyFunctions.FinitePositive(Wall)
        && PolicyFunctions.FinitePositive(Battery.CapacityWD) && PolicyFunctions.FinitePositive(Battery.Efficiency);

    /// <summary>The storage of the planned battery room: its slots at the catalog battery's capacity.</summary>
    internal double BatteryRoomCapacityWD()
    {
        var table = PolicyFunctions.RoomSizes;
        var room = new PlannedRoom { Role = PolicyFunctions.PlannedBattery, Region = PolicyFunctions.RectRegion(new Rectangle { Width = table.Battery[0], Height = table.Battery[1] }), DoorRot = Rotation.South };
        return PolicyFunctions.BatterySlots(room).Count * Battery.CapacityWD;
    }
}

/// <summary>
/// Ports <c>layout_power_derive.go</c>: how many power sites a first plan owes. The count is not a number the planner
/// holds; it is what the planned demand needs, found by adding sites one at a time until the budget has no generation
/// shortfall. Each step quotes the next turbine and the next solar plot at their cheapest sites and takes the one whose
/// price (build, conduit, wall and ground, in watts) is least per watt it delivers. A catalog without the rows, or a
/// colony without the steel price, makes the count unknown, never a default.
/// </summary>
public static partial class PolicyFunctions
{
    /// <summary>The DefTables table that owns the conduit's name.</summary>
    private const string SiteConduit = "PowerConduit";

    /// <summary>The conduit a site is wired to the grid with.</summary>
    public const string PowerConduitDefinition = SiteConduit;

    /// <summary>The watts the groups draw: their powered stations and the lamps that light their floor. A group that wants a lamp the catalog does not price makes the demand unknown.</summary>
    internal static double PlannedDemandW(this StationFacts f, IReadOnlyList<RoomGroup> groups)
    {
        var lamp = f.Catalog.Light.Standing;
        var reach = f.Catalog.Light.StandingReach();
        var demand = 0.0;
        foreach (var g in groups)
        {
            foreach (var s in g.Stations)
            {
                var (st, ok) = f.Catalog.Station(s.Def);
                if (ok && st.DrawsPower())
                {
                    demand += -st.BaseW * s.Count;
                }
            }
            if (g.Area <= 0 || g.Requires.Contains(EnvRequirement.OpenSky))
            {
                continue;
            }
            if (!FinitePositive(lamp.PowerW) || !FinitePositive(reach))
            {
                throw UnknownStationFact("lamp");
            }
            demand += lamp.PowerW * Math.Ceiling(g.Area / (Math.PI * reach * reach));
        }
        return demand;
    }

    private static double? SteelValue(StationFacts f)
    {
        try
        {
            var steel = f.Items.MarketValue(SiteSteelUnit);
            return FinitePositive(steel) ? steel : null;
        }
        catch (DomainException)
        {
            return null;
        }
    }

    /// <summary>The watts a free site is priced in, from the catalog's power rows and the steel price; null while either is unknown.</summary>
    internal static SitePrice? SitePriceOf(this StationFacts f)
    {
        if (SteelValue(f) is not { } steel || !f.Catalog.Power.Known())
        {
            return null;
        }
        var w = DefaultSiteTypeWeights();
        var p = f.Catalog.Power;
        return new SitePrice { Weights = w, WindW = p.Wind.NominalW * p.Wind.Profile.Daily, ConduitW = BuildW(w, p.Conduit, steel), WallW = BuildW(w, p.Wall, steel) };
    }

    /// <summary>
    /// Places the power sites around the embedding: the plan's own, and for a first plan (Derive) the turbines and
    /// solar plots its planned demand needs. It returns the nodes in the order they were taken.
    /// </summary>
    internal static (List<SiteNode> Nodes, SiteEmbedding Placed) PowerSites(this StationFacts f, IReadOnlyList<RoomGroup> groups, EmbedInput embedIn, Embedding embedding)
    {
        var nodes = PowerNodesOf(f.Plan);
        var priced = f.SitePriceOf(); // a replan's advice places the sites it holds, priced or not
        var input = new SiteEmbedInput { Embed = embedIn, Embedding = embedding, Nodes = nodes, Price = priced ?? new SitePrice() };
        if (!f.Derive)
        {
            if (nodes.Count == 0)
            {
                return ([], new SiteEmbedding());
            }
            return (nodes, EmbedSites(input));
        }
        var demand = f.PlannedDemandW(groups);
        if (demand > 0 && priced is null)
        {
            if (SteelValue(f) is null)
            {
                throw UnknownStationFact("steel value");
            }
            throw UnknownStationFact("power supply");
        }
        var run = new SiteRun(input);
        foreach (var n in nodes)
        {
            run.Admit(n);
        }
        foreach (var n in nodes)
        {
            if (n.Anchor is not null)
            {
                run.Place(n);
            }
        }
        foreach (var n in nodes)
        {
            if (n.Anchor is null)
            {
                run.Place(n);
            }
        }
        if (demand > 0)
        {
            nodes = f.GrowSites(run, nodes, demand);
        }
        return (nodes, run.Result());
    }

    /// <summary>Adds sites to the run, one at a time, until the budget has no generation shortfall or no ground holds another.</summary>
    private static List<SiteNode> GrowSites(this StationFacts f, SiteRun run, List<SiteNode> nodes, double demand)
    {
        var p = f.Catalog.Power;
        var steel = SteelValue(f) ?? 0;
        var w = DefaultSiteTypeWeights();
        double windBuild = BuildW(w, p.Wind.Build, steel), solarBuild = BuildW(w, p.Solar.Build, steel);
        var solarW = p.Solar.NominalW * p.Solar.Profile.Daily;
        int turbines = 0, plots = 0;
        while (true)
        {
            if (f.SiteBudget(run, nodes, demand).GenerationShortfallW <= 0)
            {
                return nodes;
            }
            var turbine = TurbineNode(WindNodeId(turbines + 1));
            var plot = SolarNode(SolarNodeId(plots + 1));
            var tq = run.Quote(turbine);
            var sq = run.Quote(plot);

            // Price per delivered watt: what the site costs to build and reach, over what it delivers once its lane has taken its share.
            double tRatio = double.PositiveInfinity, sRatio = double.PositiveInfinity;
            if (tq is not null && (p.Wind.NominalW * p.Wind.Profile.Daily) - tq.LaneW > 0)
            {
                tRatio = (tq.CostW + windBuild) / ((p.Wind.NominalW * p.Wind.Profile.Daily) - tq.LaneW);
            }
            if (sq is not null && solarW > 0)
            {
                sRatio = (sq.CostW + solarBuild) / solarW;
            }
            if (double.IsPositiveInfinity(tRatio) && double.IsPositiveInfinity(sRatio))
            {
                run.Place(turbine); // no ground, or none that delivers: the decision says so
                return nodes;
            }
            if (tRatio <= sRatio)
            {
                run.Take(turbine, tq!);
                nodes = [.. nodes, turbine];
                turbines++;
            }
            else
            {
                run.Take(plot, sq!);
                nodes = [.. nodes, plot];
                plots++;
            }
        }
    }

    /// <summary>The power budget of the demand against the sites placed so far: each turbine at the output its lane leaves, each solar plot by the day curve, and the planned battery room as the bank.</summary>
    private static PowerBudget SiteBudget(this StationFacts f, SiteRun run, IReadOnlyList<SiteNode> nodes, double demand)
    {
        var p = f.Catalog.Power;
        var placed = run.Result();
        var producers = new List<PowerProducer>();
        foreach (var n in nodes)
        {
            if (placed.Site(n.Id) is not { } site)
            {
                continue;
            }
            if (n.Kind == NodeTurbine)
            {
                var blocked = site.Lanes.Where(l => l.Wind).Sum(l => l.Blocked);
                var output = WindOutput(blocked);
                producers.Add(new PowerProducer { Definition = WindTurbineDefinition, NominalW = p.Wind.NominalW, Profile = new PowerSourceProfile { Daily = p.Wind.Profile.Daily * output, Night = p.Wind.Profile.Night * output } });
            }
            else if (n.Kind == NodeSolar)
            {
                producers.Add(new PowerProducer { Definition = SolarDefinition, NominalW = p.Solar.NominalW, Profile = p.Solar.Profile });
            }
        }
        var input = new PowerBudgetInput { DemandW = demand, CapacityWD = p.BatteryRoomCapacityWD(), ChargeEfficiency = p.Battery.Efficiency, StorageMargin = DefaultPowerPlanning().StorageMargin, Producers = producers };
        return ComputePowerBudget(input);
    }
}
