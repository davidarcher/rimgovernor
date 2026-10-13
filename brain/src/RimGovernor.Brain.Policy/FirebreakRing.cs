namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>
    /// Go <c>FirebreakRing</c>: the firebreak band's geometry alone: every in-bounds cell within
    /// <see cref="FirebreakWidth"/> of the base footprint (the home base of the colony extent with no margin,
    /// plus GrowingZones) and outside it, whatever its ground or treatment. It reads only Bounds, Construction,
    /// Claims, Home and GrowingZones; field siting protects these cells so new fields never take the ring.
    /// Throws where Go returned an error.
    /// </summary>
    public static Fact<IReadOnlyList<Cell>> FirebreakRing(FirebreakRequest r)
    {
        var unknown = Unknown<IReadOnlyList<Cell>>();
        var bk = r.Bounds.TryGetValue(out var bounds);
        var zk = r.GrowingZones.TryGetValue(out var zones);
        if (!bk || !zk)
        {
            return unknown;
        }
        var extent = DeriveColonyExtent(new ColonyExtentRequest { Bounds = r.Bounds, Construction = r.Construction, Claims = r.Claims, Home = r.Home });
        if (!extent.TryGetValue(out var value))
        {
            return unknown;
        }
        var footprint = new HashSet<Cell>(HomeBase(value));
        foreach (var c in zones!)
        {
            footprint.Add(c);
        }
        var band = new HashSet<Cell>();
        foreach (var c in footprint)
        {
            for (var dx = -FirebreakWidth; dx <= FirebreakWidth; dx++)
            {
                for (var dz = -FirebreakWidth; dz <= FirebreakWidth; dz++)
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
        cells.Sort((a, b) => ExtentCellLess(a, b) ? -1 : ExtentCellLess(b, a) ? 1 : 0);
        return Known<IReadOnlyList<Cell>>(cells);
    }
}
