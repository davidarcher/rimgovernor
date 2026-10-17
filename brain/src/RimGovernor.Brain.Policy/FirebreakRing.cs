namespace RimGovernor.Brain.Policy;

public static class FirebreakRingRules
{
    /// <summary>
    /// The firebreak band's geometry alone: every in-bounds cell within
    /// <see cref="FirebreakRules.FirebreakWidth"/> of the base footprint (the home base of the colony extent with no margin,
    /// plus GrowingZones) and outside it, whatever its ground or treatment. It reads only Bounds, Construction,
    /// Claims, Home and GrowingZones; field siting protects these cells so new fields never take the ring.
    /// Throws.
    /// </summary>
    public static Fact<EquatableArray<Cell>> FirebreakRing(FirebreakRequest r)
    {
        var unknown = Unknown<EquatableArray<Cell>>();
        var bk = r.Bounds.TryGetValue(out var bounds);
        var zk = r.GrowingZones.TryGetValue(out var zones);
        if (!bk || !zk)
        {
            return unknown;
        }
        var extent = ColonyExtentRules.DeriveColonyExtent(new ColonyExtentRequest { Bounds = r.Bounds, Construction = r.Construction, Claims = r.Claims, Home = r.Home });
        if (!extent.TryGetValue(out var value))
        {
            return unknown;
        }
        var footprint = new HashSet<Cell>(HomeAreaRules.HomeBase(value));
        foreach (var c in zones!)
        {
            footprint.Add(c);
        }
        var band = new HashSet<Cell>();
        foreach (var c in footprint)
        {
            for (var dx = -FirebreakRules.FirebreakWidth; dx <= FirebreakRules.FirebreakWidth; dx++)
            {
                for (var dz = -FirebreakRules.FirebreakWidth; dz <= FirebreakRules.FirebreakWidth; dz++)
                {
                    var n = new Cell(c.X + dx, c.Z + dz);
                    if (n.X >= 0 && n.Z >= 0 && n.X < bounds.Width && n.Z < bounds.Height && !footprint.Contains(n))
                    {
                        band.Add(n);
                    }
                }
            }
        }
        var cells = band.ToList();
        cells.Sort((a, b) => ColonyExtentRules.ExtentCellLess(a, b) ? -1 : ColonyExtentRules.ExtentCellLess(b, a) ? 1 : 0);
        return Known(EquatableArray.From(cells));
    }
}
