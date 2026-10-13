using System.Globalization;
using System.Text;

namespace RimGovernor.Brain.Policy;

/// <summary>Where and under what infrastructure a field grows.</summary>
public enum SiteKind
{
    /// <summary>Open soil, the PlanField choice.</summary>
    [WireName("outdoor")]
    Outdoor,

    /// <summary>Roofed soil already lit by a running sun lamp.</summary>
    [WireName("greenhouse-reuse")]
    GreenhouseReuse,

    /// <summary>Roofed indoor soil lit by a sun lamp the plan builds.</summary>
    [WireName("greenhouse-new")]
    GreenhouseNew,

    /// <summary>New basins on roofed cells a running sun lamp lights.</summary>
    [WireName("hydroponics")]
    Hydroponics,

    /// <summary>Unlit roofed indoor soil for a crop that grows without light.</summary>
    [WireName("dark-room")]
    DarkRoom,

    /// <summary>Nutrient-paste dispenser site (paste_site.go).</summary>
    [WireName("nutrient-paste")]
    Paste,
}

/// <summary>
/// A buildable definition a controlled site may add, with its native availability and base power draw.
/// Fertility is a grower's native soil equivalent (2.8 for a basin). Costs is the native cost list; unknown
/// prices as 100 steel.
/// </summary>
public record struct Infrastructure
{
    private string? name;

    public string Name
    {
        readonly get => name ?? "";
        init => name = value;
    }

    public Fact<bool> Available { get; init; }

    public Fact<double> PowerW { get; init; }

    public Fact<double> Fertility { get; init; }

    public Fact<EquatableArray<Amount>> Costs { get; init; }
}

/// <summary>
/// Price infrastructure in normal-soil cells of the crop's daily nutrition, like SiteTravelWeight, so a lamp is
/// only worth building when the soil it lights outgrows its cost. Construction is charged per 100
/// steel-equivalent of a new building's cost list (SiteSteelEquivalent); Power per kilowatt of added draw,
/// averaged over the day.
/// </summary>
public readonly record struct SiteTypeWeights(double Construction, double Power);

public sealed record SiteTypeRequest
{
    public PasteSiteRequest? Paste { get; init; }

    public FieldRequest Field { get; init; } = new();

    public Fact<ControlledEnvironment> Environment { get; init; }

    public Fact<Infrastructure> Lamp { get; init; }

    public Fact<Infrastructure> Basin { get; init; }

    public Fact<Infrastructure> Heater { get; init; }

    /// <summary>The native Building_SunLamp growth radius in cells.</summary>
    public double LampGrowthRadius { get; init; }

    public SiteTypeWeights Weights { get; init; }

    /// <summary>Items price the cost lists (market values).</summary>
    public ItemFacts Items { get; init; }

    /// <summary>The accessible colony stock a soil-free hydroponics unit must be able to pay for; unknown leaves the check to admission.</summary>
    public Fact<IReadOnlyDictionary<Resource, long>> Stock { get; init; }
}

/// <summary>One placement a construction kind needs before its cells can grow.</summary>
public sealed record SiteBuilding
{
    public string Definition { get; init; } = "";

    public Cell Cell { get; init; }

    public Rotation Rotation { get; init; }
}

/// <summary>Records one crop under one kind. Score is net nutrition per day divided by the cells the crop needs, comparable across kinds.</summary>
public sealed record SiteTypeCandidate
{
    public SiteKind Kind { get; init; }

    public CropChoice Crop { get; init; } = new();

    public int Needed { get; init; }

    public FarmSitePlan Sites { get; init; } = new();

    public EquatableArray<SiteBuilding> Buildings { get; init; }

    public int Cells { get; init; }

    public double Score { get; init; }

    public EquatableArray<FarmSiteTerm> Terms { get; init; }

    public string Reason { get; init; } = "";
}

public sealed record SiteTypePlan
{
    public SiteKind Kind { get; init; }

    public CropChoice Crop { get; init; } = new();

    public int Needed { get; init; }

    public FarmSitePlan Sites { get; init; } = new();

    public EquatableArray<SiteBuilding> Buildings { get; init; }

    public bool Urgent { get; init; }

    public EquatableArray<SiteTypeCandidate> Candidates { get; init; }

    public string Explain()
    {
        var b = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        b.Append(inv, $"{PolicyFunctions.KindName(Kind)} {Crop.Name} needed={Needed} urgent={(Urgent ? "true" : "false")} buildings={Buildings.Count}");
        foreach (var c in Candidates)
        {
            b.Append(inv, $"\n {PolicyFunctions.KindName(c.Kind)} {c.Crop.Name} needed={c.Needed} cells={c.Cells} score={c.Score:F4}");
            foreach (var t in c.Terms)
            {
                b.Append(inv, $" {t.Name}={t.Value:F4}");
            }
            if (c.Reason != "")
            {
                b.Append(inv, $" {c.Reason}");
            }
        }
        return b.ToString();
    }
}

public static partial class PolicyFunctions
{
    /// <summary>The construction material a building's cost is counted in.</summary>
    public static readonly Resource SiteSteelUnit = new("Steel");

    internal static string KindName(SiteKind kind) => kind switch
    {
        SiteKind.Outdoor => "outdoor",
        SiteKind.GreenhouseReuse => "greenhouse-reuse",
        SiteKind.GreenhouseNew => "greenhouse-new",
        SiteKind.Hydroponics => "hydroponics",
        SiteKind.DarkRoom => "dark-room",
        _ => "",
    };

    /// <summary>Breaks score ties toward the cheaper, less invasive kind.</summary>
    private static int SiteKindOrder(SiteKind kind) => kind switch
    {
        SiteKind.Outdoor => 0,
        SiteKind.GreenhouseReuse => 1,
        SiteKind.DarkRoom => 2,
        SiteKind.GreenhouseNew => 3,
        SiteKind.Hydroponics => 4,
        _ => 0,
    };

    /// <summary>
    /// Prices infrastructure at the game's own numbers amortised over one 60-day year. A unit is one
    /// normal-soil cell's harvest nutrition per grow day; plants grow only while not resting (06:00-19:12), so
    /// a unit is ~0.055 nutrition/day of rice, 1.1 raw food at 1.1 silver: about 1.2 silver/day. A solar
    /// generator (100 steel + 3 components, 286 silver) yields ~0.55 of its 1700W over a day, so a
    /// kilowatt-day costs ~5 silver, 4 units (wood-fired fuel, 22 wood/day per kW, would be ~22). 100 steel is
    /// 190 silver, ~3.2 silver/day over the year: 2.6 units, so a 40-steel sun lamp costs ~1 unit, a basin ~3
    /// and a heater ~1.75. A 2900W lamp on its 55% duty then costs ~7.4 units: a greenhouse needs eight roofed
    /// cells to pay for itself, and basins beat lit soil only for crops that gain from their fertility (rice,
    /// not potatoes).
    /// </summary>
    public static SiteTypeWeights DefaultSiteTypeWeights() => new(2.6, 4);

    /// <summary>
    /// The native PlantProperties.minOptimalGrowthTemperature: growth falls linearly to nothing between it and
    /// 0C, and a cell below 0C cannot be sown, so a room under it is heated or refused.
    /// </summary>
    internal const double SiteColdC = 6.0;

    /// <summary>
    /// The fraction of the day a sun lamp draws power: its CompProperties_Schedule runs 0.25-0.8 of the day,
    /// the plants' growing hours. Basins and heaters draw all day.
    /// </summary>
    internal const double SiteLampDuty = 0.55;

    /// <summary>The construction material a building's cost is counted in: each cost-list resource is worth its market value over steel's.</summary>
    public static readonly Resource SteelResource = new("Steel");

    /// <summary>
    /// A building's cost list in steel by the game's market values; an unknown list prices as 100 steel so
    /// every kind still pays for what it builds, and a resource the catalog does not price throws DomainException.
    /// </summary>
    private static double SiteSteelEquivalent(ItemFacts items, Infrastructure infra)
    {
        if (!infra.Costs.TryGetValue(out var costs))
        {
            return 100;
        }
        var steel = items.MarketValue(SteelResource);
        var total = 0.0;
        foreach (var c in costs)
        {
            var value = items.MarketValue(c.Resource);
            total += value / steel * c.Count;
        }
        return total;
    }

    /// <summary>The daily unit cost of count new buildings. Throws DomainException when a cost is not priced.</summary>
    internal static double SiteConstructionCharge(ItemFacts items, SiteTypeWeights w, double unit, Infrastructure infra, int count)
    {
        var steel = SiteSteelEquivalent(items, infra);
        return -w.Construction * unit * steel / 100 * count;
    }

    /// <summary>The native HydroponicsBasin footprint (1x4).</summary>
    internal const int SiteBasinCells = 4;

    /// <summary>
    /// The native sow tag a HydroponicsBasin accepts. A new basin sows its definition's default crop;
    /// PlanGrowerCrops re-crops it to the candidate's crop once it is built.
    /// </summary>
    internal const string SiteHydroponicTag = "Hydroponic";

    /// <summary>
    /// Chooses the crop, the site kind and the cells jointly. Outdoor soil is PlanField. Controlled kinds
    /// screen crops without the outdoor season, take only cells the native environment census supports (a
    /// running lamp's growth cells, roofed indoor soil, unlit rooms for dark crops), and charge construction,
    /// power against the best network's observed day or night headroom, and a heater when the room or outdoors
    /// is below SiteColdC. A kind whose power, heating, crop compatibility or infrastructure is unavailable is
    /// kept as an excluded candidate with its reason. Unknown environment facts yield only the outdoor
    /// candidates.
    /// </summary>
    public static (SiteTypePlan Plan, bool Ok) PlanSiteType(SiteTypeRequest r)
    {
        if (r.Paste is not null)
        {
            return PlanPasteSite(r);
        }
        var w = r.Weights;
        if (w == default)
        {
            w = DefaultSiteTypeWeights();
        }
        if (!FoodNumber(w.Construction) || !FoodNumber(w.Power) || !(r.LampGrowthRadius >= 0 && r.LampGrowthRadius <= 64))
        {
            return (new SiteTypePlan(), false);
        }
        var candidates = new List<SiteTypeCandidate>();
        var (field, ok) = PlanField(r.Field);
        var sk = r.Field.Climate.SowingOutdoors().TryGetValue(out var sowing);
        var darkKnown = r.Field.Climate.OutdoorsDark.TryGetValue(out var dark);
        var notPossible = "outdoor sowing not possible";
        if (!darkKnown)
        {
            notPossible = "outdoors darkness unknown";
        }
        else if (dark)
        {
            notPossible = "outdoors permanently dark";
        }
        foreach (var c in field.Candidates)
        {
            var reason = c.Reason;
            if (!sk || !sowing)
            {
                reason = notPossible;
            }
            candidates.Add(new SiteTypeCandidate { Kind = SiteKind.Outdoor, Crop = c.Crop, Needed = c.Needed, Sites = c.Sites, Cells = c.Sites.Cells, Score = c.Score, Terms = c.Terms, Reason = reason });
        }
        if (!ok && (!sk || !sowing) && field.Candidates.Count == 0)
        {
            foreach (var crop in r.Field.Choices)
            {
                candidates.Add(new SiteTypeCandidate { Kind = SiteKind.Outdoor, Crop = crop, Reason = notPossible });
            }
        }
        var urgent = field.Urgent;
        if (r.Environment.TryGetValue(out var env))
        {
            var (viables, excluded, vk) = ViableCrops(r.Field, true);
            if (vk)
            {
                urgent = urgent || FieldUrgency(r.Field, viables, true);
                foreach (var kind in new[] { SiteKind.GreenhouseReuse, SiteKind.DarkRoom, SiteKind.GreenhouseNew, SiteKind.Hydroponics })
                {
                    foreach (var c in excluded)
                    {
                        candidates.Add(new SiteTypeCandidate { Kind = kind, Crop = c.Crop, Reason = c.Reason });
                    }
                    foreach (var v in viables)
                    {
                        var candidate = SiteCandidate(r, w, env, kind, v);
                        if (candidate.Cells > 0)
                        {
                            var terms = candidate.Terms.ToList();
                            terms.AddRange(CropChoiceTerms(r.Field, v.Crop, candidate.Cells));
                            terms.AddRange(SiteRiskTerms(r.Field, v.Crop, candidate.Cells));
                            var total = 0.0;
                            foreach (var term in terms)
                            {
                                total += term.Value;
                            }
                            candidate = candidate with { Terms = EquatableArray.From(terms), Score = total / v.Needed };
                        }
                        candidates.Add(candidate);
                    }
                }
            }
        }
        var sorted = candidates.OrderBy(c => c, Comparer<SiteTypeCandidate>.Create((a, b) => SiteCandidateLess(a, b, urgent) ? -1 : SiteCandidateLess(b, a, urgent) ? 1 : 0)).ToList();
        var plan = new SiteTypePlan { Candidates = EquatableArray.From(sorted), Urgent = urgent };
        if (sorted.Count == 0 || sorted[0].Cells == 0)
        {
            return (plan, false);
        }
        var best = sorted[0];
        return (plan with { Kind = best.Kind, Crop = best.Crop, Needed = best.Needed, Sites = best.Sites, Buildings = best.Buildings }, true);
    }

    private static bool SiteCandidateLess(SiteTypeCandidate a, SiteTypeCandidate b, bool urgent)
    {
        if ((a.Cells > 0) != (b.Cells > 0))
        {
            return a.Cells > 0;
        }
        a.Crop.GrowDays.TryGetValue(out var ad);
        b.Crop.GrowDays.TryGetValue(out var bd);
        if (urgent && ad != bd)
        {
            return ad < bd;
        }
        if (a.Score != b.Score)
        {
            return a.Score > b.Score;
        }
        if (SiteKindOrder(a.Kind) != SiteKindOrder(b.Kind))
        {
            return SiteKindOrder(a.Kind) < SiteKindOrder(b.Kind);
        }
        if (ad != bd)
        {
            return ad < bd;
        }
        return string.CompareOrdinal(a.Crop.Name, b.Crop.Name) < 0;
    }

    /// <summary>Sums the selected patches' terms by name.</summary>
    private static List<FarmSiteTerm> SiteTerms(FarmSitePlan sites)
    {
        var totals = new Dictionary<string, double>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var s in sites.Selected)
        {
            foreach (var t in s.Terms)
            {
                if (!totals.ContainsKey(t.Name))
                {
                    order.Add(t.Name);
                }
                totals[t.Name] = totals.GetValueOrDefault(t.Name) + t.Value;
            }
        }
        return order.Select(name => new FarmSiteTerm(name, totals[name])).ToList();
    }

    internal static double CropRate(CropChoice crop, double soil)
    {
        crop.HarvestNutrition.TryGetValue(out var yield);
        return CropYieldRate(crop, yield, soil);
    }

    /// <summary>The per-day yield of one cell at the given soil fertility for a harvest worth yield (nutrition or resource units).</summary>
    internal static double CropYieldRate(CropChoice crop, double yield, double soil)
    {
        crop.GrowDays.TryGetValue(out var days);
        crop.FertilitySensitivity.TryGetValue(out var sensitivity);
        return yield * Math.Max(0, 1 + ((soil - 1) * sensitivity)) / days;
    }

    /// <summary>
    /// Picks the network with the most headroom for a draw that runs by day (lamps follow the sun schedule) or
    /// all night (basins, heaters). Networks without an active source never qualify; a network without
    /// batteries must carry a night load through a calm night.
    /// </summary>
    internal static (PowerHeadroom Network, double Room, bool Fits) SitePower(ControlledEnvironment env, double drawW, bool night)
    {
        PowerHeadroom best = default;
        var bestRoom = 0.0;
        var found = false;
        foreach (var n in env.Networks)
        {
            if (!Positive(n.ActiveSource))
            {
                continue;
            }
            Fact<double> room = default;
            if (!night)
            {
                var gk = n.GenerationW.TryGetValue(out var generation);
                var ck = n.ConsumptionW.TryGetValue(out var consumption);
                if (gk && ck && FoodNumber(generation) && FoodNumber(consumption))
                {
                    room = Known(generation - consumption);
                }
            }
            else if (n.CapacityWattDays.TryGetValue(out var capacity) && capacity > 0)
            {
                room = n.NightHeadroomW();
            }
            else
            {
                room = n.CalmNightHeadroomW();
            }
            var known = room.TryGetValue(out var v);
            if (known && (!found || v > bestRoom || (v == bestRoom && string.CompareOrdinal(n.Id, best.Id) < 0)))
            {
                best = n;
                bestRoom = v;
                found = true;
            }
        }
        return (best, bestRoom, found && bestRoom >= drawW);
    }

    private static SiteTypeCandidate SiteCandidate(SiteTypeRequest r, SiteTypeWeights w, ControlledEnvironment env, SiteKind kind, ViableCrop v)
    {
        var c = new SiteTypeCandidate { Kind = kind, Crop = v.Crop, Needed = v.Needed };
        if (kind != SiteKind.DarkRoom && GrowsInDark(v.Crop))
        {
            return c with { Reason = "crop needs a dark room" };
        }
        var mk = v.Crop.FertilityMin.TryGetValue(out var minimum);
        if (!mk || !FieldPositive(minimum))
        {
            return c with { Reason = "incomplete crop facts" };
        }
        var unit = CropRate(v.Crop, 1);
        if (!FieldPositive(unit))
        {
            return c with { Reason = "incomplete crop facts" };
        }
        var lit = env.LitCells();
        var lamps = new Dictionary<string, GrowLight>(StringComparer.Ordinal);
        foreach (var l in env.Lights)
        {
            lamps[l.Id] = l;
        }
        var rooms = new Dictionary<string, GrowRoom>(StringComparer.Ordinal);
        foreach (var room in env.Rooms)
        {
            rooms[room.Id] = room;
        }
        var outdoorCold = env.OutdoorTemperatureC.TryGetValue(out var outdoor) && outdoor < SiteColdC;
        var terms = new List<FarmSiteTerm>();
        var reason = "";

        // Charges one heater per cold room on the night headroom, or refuses.
        bool Heat(int coldRooms, string network)
        {
            if (coldRooms == 0)
            {
                return true;
            }
            var hk = r.Heater.TryGetValue(out var heater);
            var dk = heater.PowerW.TryGetValue(out var draw);
            if (!hk || !Positive(heater.Available) || !dk || !FoodNumber(draw))
            {
                reason = "room too cold and no heater available";
                return false;
            }
            var total = draw * coldRooms;
            if (network != "")
            {
                var (n, nok) = env.Network(network);
                var rk = n.CalmNightHeadroomW().TryGetValue(out var room);
                if (!nok || !Positive(n.ActiveSource) || !rk || room < total)
                {
                    reason = "no night power headroom for heating";
                    return false;
                }
            }
            else if (!SitePower(env, total, true).Fits)
            {
                reason = "no night power headroom for heating";
                return false;
            }
            double build;
            try
            {
                build = SiteConstructionCharge(r.Items, w, unit, heater, coldRooms);
            }
            catch (DomainException e)
            {
                reason = e.Message;
                return false;
            }
            terms.Add(new FarmSiteTerm("heating", (-w.Power * unit * total / 1000) + build));
            return true;
        }

        static bool RoofedIndoorSoil(SiteCell s) => Positive(s.Roofed) && Positive(s.Indoors);

        var cells = 0;
        var sites = new FarmSitePlan();
        var buildings = new List<SiteBuilding>();
        switch (kind)
        {
            case SiteKind.Hydroponics:
                if (Positive(v.Crop.RequiresPollution) || GrowsInDark(v.Crop))
                {
                    return c with { Reason = "crop needs a compatible soil or dark site" };
                }
                return SiteHydroponics(r, w, env, v, c, lit, unit);
            case SiteKind.GreenhouseReuse:
            {
                if (lit.Count == 0)
                {
                    return c with { Reason = "no running sun lamp" };
                }
                sites = SitePick(r.Field.Site, v, s => lit.ContainsKey(s.Cell) && Positive(s.Roofed), null, true);
                cells = sites.Cells;
                if (cells == 0)
                {
                    return c with { Sites = sites, Cells = cells, Reason = "no free lit soil" };
                }
                terms = SiteTerms(sites);
                var cold = new HashSet<string>(StringComparer.Ordinal);
                var network = "";
                foreach (var patch in sites.Patches)
                {
                    foreach (var cell in RectCells(patch))
                    {
                        lamps.TryGetValue(lit.GetValueOrDefault(cell) ?? "", out var lamp);
                        if (lamp is not null && lamp.Network.TryGetValue(out var id) && network == "")
                        {
                            network = id;
                        }
                        if (lamp is not null && lamp.Room.TryGetValue(out var roomId))
                        {
                            if (rooms.TryGetValue(roomId, out var gr) && gr.TemperatureC.TryGetValue(out var t) && t < SiteColdC)
                            {
                                cold.Add(roomId);
                            }
                        }
                    }
                }
                if (!Heat(cold.Count, network))
                {
                    return c with { Terms = EquatableArray.From(terms), Reason = reason };
                }
                break;
            }
            case SiteKind.DarkRoom:
            {
                if (!GrowsInDark(v.Crop))
                {
                    return c with { Reason = "crop needs light" };
                }
                sites = SitePick(r.Field.Site, v, s => RoofedIndoorSoil(s) && !lit.ContainsKey(s.Cell), null, true);
                cells = sites.Cells;
                if (cells == 0)
                {
                    return c with { Sites = sites, Cells = cells, Reason = "no free unlit roofed soil" };
                }
                terms = SiteTerms(sites);
                if (!Heat(outdoorCold ? 1 : 0, ""))
                {
                    return c with { Terms = EquatableArray.From(terms), Reason = reason };
                }
                break;
            }
            case SiteKind.GreenhouseNew:
            {
                var lk = r.Lamp.TryGetValue(out var lamp);
                var dk = lamp.PowerW.TryGetValue(out var draw);
                if (!lk || !Positive(lamp.Available) || !dk || !FoodNumber(draw) || lamp.Name == "")
                {
                    return c with { Reason = "sun lamp unavailable" };
                }
                var eligible = new HashSet<Cell>();
                foreach (var s in r.Field.Site.Cells)
                {
                    var isLit = lit.ContainsKey(s.Cell);
                    var (soil, sok) = FreeCropSoil(SiteUnroofed(s), minimum, null);
                    if (sok && RoofedIndoorSoil(s) && !isLit && FoodNumber(soil))
                    {
                        eligible.Add(s.Cell);
                    }
                }
                var (center, covered) = SiteLampCenter(eligible, r.LampGrowthRadius);
                if (covered == 0)
                {
                    return c with { Reason = "no free unlit roofed soil" };
                }
                sites = SitePick(r.Field.Site, v, s => eligible.Contains(s.Cell) && s.Cell != center && SiteWithin(s.Cell, center, r.LampGrowthRadius), [center], false);
                cells = sites.Cells;
                if (cells == 0)
                {
                    return c with { Sites = sites, Cells = cells, Reason = "no free unlit roofed soil" };
                }
                var (network, _, fits) = SitePower(env, draw, false);
                if (!fits)
                {
                    return c with { Reason = "no daytime power headroom for a sun lamp" };
                }
                terms = SiteTerms(sites);
                double build;
                try
                {
                    build = SiteConstructionCharge(r.Items, w, unit, lamp, 1);
                }
                catch (DomainException e)
                {
                    return c with { Terms = EquatableArray.From(terms), Reason = e.Message };
                }
                terms.Add(new FarmSiteTerm("construction", build));
                terms.Add(new FarmSiteTerm("power", -w.Power * unit * draw * SiteLampDuty / 1000));
                buildings = [new SiteBuilding { Definition = lamp.Name, Cell = center, Rotation = Rotation.North }];
                if (!Heat(outdoorCold ? 1 : 0, network.Id))
                {
                    return c with { Terms = EquatableArray.From(terms), Reason = reason };
                }
                break;
            }
        }
        var sum = 0.0;
        foreach (var t in terms)
        {
            sum += t.Value;
        }
        if (double.IsNaN(sum) || double.IsInfinity(sum))
        {
            return new SiteTypeCandidate { Kind = kind, Crop = v.Crop, Needed = v.Needed, Reason = "unscorable" };
        }
        return c with
        {
            Sites = sites,
            Cells = cells,
            Buildings = EquatableArray.From(buildings),
            Terms = EquatableArray.From(terms),
            Score = sum / v.Needed,
            Reason = string.Create(CultureInfo.InvariantCulture, $"net {sum:F4}/day over {sites.Patches.Count} patches"),
        };
    }

    /// <summary>
    /// Places basins on lit roofed floor under a running lamp for any crop carrying the Hydroponic sow tag.
    /// Basins ignore soil and draw all night, so the count is capped by the best network's night headroom;
    /// travel is the walked-step proxy of Manhattan distance from the anchor since basins are not square
    /// patches.
    /// </summary>
    private static SiteTypeCandidate SiteHydroponics(
        SiteTypeRequest r, SiteTypeWeights w, ControlledEnvironment env, ViableCrop v, SiteTypeCandidate c,
        IReadOnlyDictionary<Cell, string> lit, double unit)
    {
        var bk = r.Basin.TryGetValue(out var basin);
        var dk = basin.PowerW.TryGetValue(out var draw);
        var fk = basin.Fertility.TryGetValue(out var fertility);
        if (!bk || !Positive(basin.Available) || !dk || !FoodNumber(draw) || !fk || !FieldPositive(fertility) || basin.Name == "")
        {
            return c with { Reason = "hydroponics basin unavailable" };
        }
        var tk = v.Crop.SowTags.TryGetValue(out var tags);
        if (!tk || !tags.Contains(SiteHydroponicTag))
        {
            return c with { Reason = "crop cannot be sown in a basin" };
        }
        if (lit.Count == 0)
        {
            if (env.Lights.Count > 0)
            {
                return c with { Reason = "no running sun lamp" };
            }
            return SiteHydroponicsUnit(r, w, env, v, c, basin, draw, fertility, unit);
        }
        var free = SiteFreeFloor(r.Field.Site, s => lit.ContainsKey(s.Cell));
        var (_, room, ok) = SitePower(env, draw, true);
        if (!ok)
        {
            return c with { Reason = "no night power headroom for a basin" };
        }
        var limit = (int)Math.Ceiling((double)v.Needed / SiteBasinCells);
        if (draw > 0)
        {
            limit = Math.Min(limit, (int)Math.Floor(room / draw));
        }
        limit = Math.Min(limit, FarmSitePatchLimit);
        var placed = SiteBasinPlacements(free, basin.Name, r.Field.Site.Anchor, limit);
        var walk = SiteBasinWalk(placed, r.Field.Site.Anchor);
        if (placed.Count == 0)
        {
            return c with { Reason = "no free lit floor for a basin" };
        }
        var n = placed.Count;
        double build;
        try
        {
            build = SiteConstructionCharge(r.Items, w, unit, basin, n);
        }
        catch (DomainException e)
        {
            return c with { Reason = e.Message };
        }
        var cellCount = n * SiteBasinCells;
        var terms = new List<FarmSiteTerm>
        {
            new("yield", CropRate(v.Crop, fertility) * cellCount),
            new("travel", -SiteTravelWeight * unit * walk),
            new("construction", build),
            new("power", -w.Power * unit * draw * n / 1000),
        };
        var total = 0.0;
        foreach (var t in terms)
        {
            total += t.Value;
        }
        return c with
        {
            Buildings = EquatableArray.From(placed),
            Cells = cellCount,
            Terms = EquatableArray.From(terms),
            Score = total / v.Needed,
            Reason = string.Create(CultureInfo.InvariantCulture, $"net {total:F4}/day over {n} basins"),
        };
    }

    internal static int SiteManhattan(Cell a, Cell b) => (int)(Math.Abs((double)(a.X - b.X)) + Math.Abs((double)(a.Z - b.Z)));

    internal static bool SiteWithin(Cell a, Cell center, double radius)
    {
        double dx = a.X - center.X, dz = a.Z - center.Z;
        return Math.Sqrt((dx * dx) + (dz * dz)) <= radius;
    }

    /// <summary>
    /// The native occupied rect of a north-facing 1x4 basin placed at anchor: RimWorld centres an even-length
    /// footprint one cell below the placement cell (GenAdj.OccupiedRect), so the basin spans z-1 through z+2.
    /// </summary>
    internal static List<Cell> SiteBasinFootprint(Cell anchor)
    {
        var output = new List<Cell>(SiteBasinCells);
        for (var i = -(SiteBasinCells - 1) / 2; i < SiteBasinCells - ((SiteBasinCells - 1) / 2); i++)
        {
            output.Add(new Cell(anchor.X, anchor.Z + i));
        }
        return output;
    }

    /// <summary>The eligible cell whose growth disc covers the most eligible cells; ties go to the lexically smaller cell.</summary>
    private static (Cell Center, int Covered) SiteLampCenter(HashSet<Cell> eligible, double radius)
    {
        var cells = eligible.ToList();
        cells.Sort((a, b) => CellLess(a, b) ? -1 : CellLess(b, a) ? 1 : 0);
        Cell best = default;
        var covered = 0;
        foreach (var center in cells)
        {
            var n = 0;
            foreach (var c in cells)
            {
                if (c != center && SiteWithin(c, center, radius))
                {
                    n++;
                }
            }
            if (n > covered)
            {
                best = center;
                covered = n;
            }
        }
        return (best, covered);
    }

    internal static SiteCell SiteUnroofed(SiteCell s) => s with { Roofed = Known(false) };

    /// <summary>
    /// Sites a growing-room kind with the rectangle picker: candidates are the free soil cells keep accepts that
    /// meet the crop's fertility floor. byRoom groups them by native room so each block stays inside one room
    /// interior, rooms nearest the anchor first; otherwise (a new lamp's growth circle) they form one set.
    /// Picked cells merge into rectangles so the plan keeps its patch shape.
    /// </summary>
    private static FarmSitePlan SitePick(FarmSiteRequest site, ViableCrop v, Func<SiteCell, bool> keep, IReadOnlyList<Cell>? protectedCells, bool byRoom)
    {
        v.Crop.FertilityMin.TryGetValue(out var minimum);
        var blocked = new HashSet<Cell>(site.Protected);
        if (protectedCells is not null)
        {
            blocked.UnionWith(protectedCells);
        }
        var soil = new Dictionary<Cell, double>();
        var groups = new Dictionary<string, HashSet<Cell>>(StringComparer.Ordinal);
        foreach (var s in site.Cells)
        {
            if (!keep(s) || !CropSoilCompatible(v.Crop, s))
            {
                continue;
            }
            var (f, ok) = FreeCropSoil(SiteUnroofed(s), minimum, blocked);
            if (!ok)
            {
                continue;
            }
            var key = "";
            if (byRoom)
            {
                if (!s.Room.TryGetValue(out var room) || room == "")
                {
                    continue;
                }
                key = room;
            }
            if (!groups.TryGetValue(key, out var group))
            {
                group = [];
                groups[key] = group;
            }
            group.Add(s.Cell);
            soil[s.Cell] = f;
        }
        var anchor = site.Anchor;
        var keys = new List<string>(groups.Keys);
        var near = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (k, g) in groups)
        {
            near[k] = -1;
            foreach (var c in g)
            {
                var d = RectDist(c, anchor);
                if (near[k] < 0 || d < near[k])
                {
                    near[k] = d;
                }
            }
        }
        keys.Sort((a, b) => near[a] != near[b] ? near[a].CompareTo(near[b]) : string.CompareOrdinal(a, b));
        var picked = new HashSet<Cell>();
        foreach (var k in keys)
        {
            if (picked.Count >= v.Needed)
            {
                break;
            }
            foreach (var c in PickRect(groups[k], anchor, v.Needed - picked.Count))
            {
                picked.Add(c);
            }
        }
        double Rate(double s) => v.Units > 0 ? CropYieldRate(v.Crop, v.Units, s) : CropRate(v.Crop, s);
        var unit = Rate(1);
        var patches = new List<Rectangle>();
        var selected = new List<FarmSiteCandidate>();
        var planned = 0;
        foreach (var patch in SiteMergeRects(picked))
        {
            double reward = 0;
            var walk = 0;
            foreach (var c in RectCells(patch))
            {
                reward += Rate(soil[c]);
                walk += SiteManhattan(c, anchor);
            }
            var terms = new[] { new FarmSiteTerm("yield", reward), new FarmSiteTerm("travel", -SiteTravelWeight * unit * walk) };
            double n = patch.Width * patch.Height;
            patches.Add(patch);
            selected.Add(new FarmSiteCandidate
            {
                Patch = patch,
                Score = terms[0].Value + terms[1].Value,
                Density = (terms[0].Value + terms[1].Value) / n,
                Terms = EquatableArray.From(terms),
            });
            planned += (int)n;
        }
        return new FarmSitePlan
        {
            Patches = EquatableArray.From(patches),
            Selected = EquatableArray.From(selected),
            Cells = planned,
            Unplanted = planned < v.Needed ? v.Needed - planned : 0,
        };
    }

    /// <summary>Covers a cell set with disjoint rectangles: row-major, each seed runs as far right as the set allows, then down while every row below holds the same run.</summary>
    private static List<Rectangle> SiteMergeRects(HashSet<Cell> cells)
    {
        var done = new HashSet<Cell>();
        var output = new List<Rectangle>();
        foreach (var c in RectSorted(cells))
        {
            if (done.Contains(c))
            {
                continue;
            }
            var w = 1;
            while (cells.Contains(new Cell(c.X + w, c.Z)) && !done.Contains(new Cell(c.X + w, c.Z)))
            {
                w++;
            }
            var h = 1;
            var grow = true;
            while (grow)
            {
                for (var x = c.X; x < c.X + w; x++)
                {
                    var n = new Cell(x, c.Z + h);
                    if (!cells.Contains(n) || done.Contains(n))
                    {
                        grow = false;
                        break;
                    }
                }
                if (grow)
                {
                    h++;
                }
            }
            var rect = new Rectangle(c.X, c.Z, w, h);
            foreach (var rc in RectCells(rect))
            {
                done.Add(rc);
            }
            output.Add(rect);
        }
        return output;
    }

    internal static bool SiteKnownFalse(Fact<bool> f) => f.TryGetValue(out var v) && !v;
}
