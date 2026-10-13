namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>The roofed, walkable, unbuilt, unzoned and unprotected floor keep accepts: where a basin, lamp or heater may stand.</summary>
    private static HashSet<Cell> SiteFreeFloor(FarmSiteRequest site, Func<SiteCell, bool> keep)
    {
        var blocked = new HashSet<Cell>(site.Protected);
        var free = new HashSet<Cell>();
        foreach (var s in site.Cells)
        {
            if (keep(s) && !blocked.Contains(s.Cell) && Positive(s.Roofed) && Positive(s.Walkable) && !s.Occupied() && SiteKnownFalse(s.Zone))
            {
                free.Add(s.Cell);
            }
        }
        return free;
    }

    /// <summary>Packs up to limit 1x4 basins on free floor, nearest the anchor first.</summary>
    private static List<SiteBuilding> SiteBasinPlacements(HashSet<Cell> free, string definition, Cell anchor, int limit)
    {
        var ordered = free.ToList();
        ordered.Sort((a, b) =>
        {
            int da = SiteManhattan(a, anchor), db = SiteManhattan(b, anchor);
            if (da != db)
            {
                return da.CompareTo(db);
            }
            return CellLess(a, b) ? -1 : CellLess(b, a) ? 1 : 0;
        });
        var taken = new HashSet<Cell>();
        var output = new List<SiteBuilding>();
        foreach (var cell in ordered)
        {
            if (output.Count >= limit)
            {
                break;
            }
            var footprint = SiteBasinFootprint(cell);
            var fits = true;
            foreach (var f in footprint)
            {
                fits = fits && free.Contains(f) && !taken.Contains(f);
            }
            if (!fits)
            {
                continue;
            }
            foreach (var f in footprint)
            {
                taken.Add(f);
            }
            output.Add(new SiteBuilding { Definition = definition, Cell = cell, Rotation = Rotation.North });
        }
        return output;
    }

    /// <summary>The summed Manhattan distance from the anchor over every footprint cell of the basins.</summary>
    private static int SiteBasinWalk(IEnumerable<SiteBuilding> basins, Cell anchor)
    {
        var walk = 0;
        foreach (var b in basins)
        {
            foreach (var f in SiteBasinFootprint(b.Cell))
            {
                walk += SiteManhattan(f, anchor);
            }
        }
        return walk;
    }

    /// <summary>
    /// Sites a soil-free hydroponics unit where no sun lamp exists: one lamp, a heater when the room or
    /// outdoors is below SiteColdC, and basins on the roofed indoor floor inside the lamp's growth disc, as one
    /// plan (a lamp alone lights nothing, and the lamp builder needs soil). The unit is the largest basin count
    /// one network carries (lamp by day, basins and heater all day) and the accessible stock pays for; with
    /// none it holds with the reason. Wiring the buildings to the network is not this plan's work.
    /// </summary>
    private static SiteTypeCandidate SiteHydroponicsUnit(
        SiteTypeRequest r, SiteTypeWeights w, ControlledEnvironment env, ViableCrop v, SiteTypeCandidate c,
        Infrastructure basin, double basinDraw, double fertility, double unit)
    {
        var lk = r.Lamp.TryGetValue(out var lamp);
        var dk = lamp.PowerW.TryGetValue(out var lampDraw);
        if (!lk || !Positive(lamp.Available) || !dk || !FoodNumber(lampDraw) || lamp.Name == "")
        {
            return c with { Reason = "sun lamp unavailable" };
        }
        var site = r.Field.Site;
        // Indoor soil the crop grows in belongs to the greenhouse kinds; the unit is the soil-free bootstrap.
        v.Crop.FertilityMin.TryGetValue(out var minimum);
        foreach (var s in site.Cells)
        {
            if (FreeCropSoil(SiteUnroofed(s), minimum, null).Ok && Positive(s.Roofed) && Positive(s.Indoors))
            {
                return c with { Reason = "free indoor soil: a greenhouse lamp is the cheaper kind" };
            }
        }
        var roomOf = new Dictionary<Cell, string>();
        foreach (var s in site.Cells)
        {
            if (s.Room.TryGetValue(out var id) && id != "")
            {
                roomOf[s.Cell] = id;
            }
        }
        string RoomAt(Cell cell) => roomOf.TryGetValue(cell, out var room) ? room : "";
        var free = SiteFreeFloor(site, s => Positive(s.Indoors));
        var cells = new List<Cell>(free.Count);
        foreach (var cell in free.ToList())
        {
            if (RoomAt(cell) == "")
            {
                free.Remove(cell);
                continue;
            }
            cells.Add(cell);
        }
        cells.Sort((a, b) => CellLess(a, b) ? -1 : CellLess(b, a) ? 1 : 0);
        // A basin's fertility yields several soil cells' harvest, so the unit needs fewer cells than the soil-cell target.
        var cellsPerSoil = CropRate(v.Crop, fertility) / unit;
        if (!FoodNumber(cellsPerSoil) || cellsPerSoil < 1)
        {
            cellsPerSoil = 1;
        }
        var limit = Math.Min((int)Math.Ceiling(v.Needed / cellsPerSoil / SiteBasinCells), FarmSitePatchLimit);
        Cell center = default;
        var basins = new List<SiteBuilding>();
        var bestWalk = 0;
        foreach (var candidate in cells)
        {
            var disc = new HashSet<Cell>();
            foreach (var cell in free)
            {
                if (cell != candidate && RoomAt(cell) == RoomAt(candidate) && SiteWithin(cell, candidate, r.LampGrowthRadius))
                {
                    disc.Add(cell);
                }
            }
            var placed = SiteBasinPlacements(disc, basin.Name, site.Anchor, limit);
            var walk = SiteBasinWalk(placed, site.Anchor);
            if (placed.Count > basins.Count || (placed.Count == basins.Count && placed.Count > 0 && walk < bestWalk))
            {
                center = candidate;
                basins = placed;
                bestWalk = walk;
            }
        }
        if (basins.Count == 0)
        {
            return c with { Reason = "no free roofed floor for a lamp and a basin" };
        }
        var cold = false;
        if (env.OutdoorTemperatureC.TryGetValue(out var outdoor) && outdoor < SiteColdC)
        {
            cold = true;
        }
        foreach (var room in env.Rooms)
        {
            if (room.TemperatureC.TryGetValue(out var t) && room.Id == RoomAt(center) && t < SiteColdC)
            {
                cold = true;
            }
        }
        Infrastructure heater = new();
        var heaterDraw = 0.0;
        Cell heaterAt = default;
        if (cold)
        {
            var hk = r.Heater.TryGetValue(out heater);
            var hdk = heater.PowerW.TryGetValue(out heaterDraw);
            if (!hk || !Positive(heater.Available) || !hdk || !FoodNumber(heaterDraw) || heater.Name == "")
            {
                return c with { Reason = "room too cold and no heater available" };
            }
            var used = new HashSet<Cell> { center };
            foreach (var b in basins)
            {
                foreach (var f in SiteBasinFootprint(b.Cell))
                {
                    used.Add(f);
                }
            }
            var found = false;
            foreach (var cell in cells)
            {
                if (used.Contains(cell) || RoomAt(cell) != RoomAt(center))
                {
                    continue;
                }
                if (!found || SiteManhattan(cell, center) < SiteManhattan(heaterAt, center))
                {
                    heaterAt = cell;
                    found = true;
                }
            }
            if (!found)
            {
                return c with { Reason = "no free floor for a heater" };
            }
        }
        var stockKnown = r.Stock.TryGetValue(out var stock);
        stock ??= new Dictionary<Resource, long>();
        Dictionary<Resource, long> UnitCosts(int count)
        {
            var need = new Dictionary<Resource, long>();
            void Add(Infrastructure infra, long amount)
            {
                infra.Costs.TryGetValue(out var costs);
                foreach (var a in costs)
                {
                    need[a.Resource] = need.GetValueOrDefault(a.Resource) + (a.Count * amount);
                }
            }
            Add(lamp, 1);
            if (cold)
            {
                Add(heater, 1);
            }
            Add(basin, count);
            return need;
        }
        // The largest count both power and stock carry; the reason is the one that refuses the smallest unit.
        var n = 0;
        var reason = "";
        for (var k = basins.Count; k >= 1; k--)
        {
            var powered = false;
            foreach (var network in env.Networks)
            {
                var one = env with { Networks = EquatableArray.From([network]) };
                var day = SitePower(one, lampDraw + (k * basinDraw) + heaterDraw, false).Fits;
                var night = SitePower(one, (k * basinDraw) + heaterDraw, true).Fits;
                powered = powered || (day && night);
            }
            var shortage = "";
            if (stockKnown)
            {
                var need = UnitCosts(k);
                var names = need.Keys.Select(x => x.Value).ToList();
                names.Sort(StringComparer.Ordinal);
                foreach (var name in names)
                {
                    var resource = new Resource(name);
                    var have = stock.TryGetValue(resource, out var held) ? held : 0;
                    if (have < need[resource])
                    {
                        shortage = $"not enough {name} for a hydroponics unit: need {need[resource]} have {have}";
                        break;
                    }
                }
            }
            if (powered && shortage == "")
            {
                n = k;
                break;
            }
            if (k == 1)
            {
                reason = "no power headroom for a hydroponics unit";
                if (powered)
                {
                    reason = shortage;
                }
            }
        }
        if (n == 0)
        {
            return c with { Reason = reason };
        }
        var buildings = new List<SiteBuilding> { new() { Definition = lamp.Name, Cell = center, Rotation = Rotation.North } };
        if (cold)
        {
            buildings.Add(new SiteBuilding { Definition = heater.Name, Cell = heaterAt, Rotation = Rotation.North });
        }
        buildings.AddRange(basins.Take(n));
        var charges = new List<(Infrastructure Infra, int Count)> { (lamp, 1), (basin, n) };
        if (cold)
        {
            charges.Add((heater, 1));
        }
        var build = 0.0;
        foreach (var (infra, count) in charges)
        {
            try
            {
                build += SiteConstructionCharge(r.Items, w, unit, infra, count);
            }
            catch (DomainException e)
            {
                return c with { Buildings = default, Reason = e.Message };
            }
        }
        var cellCount = n * SiteBasinCells;
        var walkTotal = SiteBasinWalk(basins.Take(n), site.Anchor);
        var draw = (lampDraw * SiteLampDuty) + (n * basinDraw) + heaterDraw;
        var terms = new List<FarmSiteTerm>
        {
            new("yield", CropRate(v.Crop, fertility) * cellCount),
            new("travel", -SiteTravelWeight * unit * walkTotal),
            new("construction", build),
            new("power", -w.Power * unit * draw / 1000),
        };
        var total = 0.0;
        foreach (var t in terms)
        {
            total += t.Value;
        }
        return c with
        {
            Buildings = EquatableArray.From(buildings),
            Cells = cellCount,
            Terms = EquatableArray.From(terms),
            Score = total / v.Needed,
            Reason = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"net {total:F4}/day over a lamp and {n} basins"),
        };
    }
}
