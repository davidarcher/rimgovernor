using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Policy;

public sealed record PasteSiteRequest
{
    public MealTierRequest Meals { get; init; } = new();

    public EquatableArray<ProductionBench> Benches { get; init; }

    public Infrastructure? Hopper { get; init; }

    public Bounds Size { get; init; }

    public PowerTopology Power { get; init; } = new();
}

public static partial class PasteSiteRules
{
    /// <summary>
    /// Paste shares site admission with growing infrastructure. The conservative clear apron leaves room for
    /// an adjacent hopper and the dispensing cell; native placement previews remain authoritative for both
    /// footprints.
    /// </summary>
    internal static SiteTypePlan? PlanPasteSite(SiteTypeRequest r)
    {
        var v = r.Paste!;
        MealTierReview review;
        try
        {
            review = MealRules.ReviewMealTier(v.Meals, v.Benches);
        }
        catch (DomainException)
        {
            return null;
        }
        if (v.Hopper is not { } hopper)
        {
            return null;
        }
        var size = v.Size;
        if (review.Tier != MealTier.Paste || !hopper.Available || size.Width <= 0 || size.Height <= 0 || size.Width > 6 || size.Height > 6)
        {
            return null;
        }
        var cells = new Dictionary<Cell, bool>();
        var listed = new HashSet<Cell>();
        var rock = new Dictionary<Cell, bool>();
        foreach (var c in r.Field.Site.Cells)
        {
            cells[c.Cell] = c.Walkable && !c.Occupied() && !c.Zone;
            listed.Add(c.Cell);
            rock[c.Cell] = TunnelSiteRules.RockCell(c);
        }
        foreach (var c in r.Field.Site.Protected)
        {
            cells[c] = false;
            rock[c] = false;
        }
        foreach (var c in r.Field.Site.Cells)
        {
            var connected = false;
            foreach (var b in v.Power.Buildings)
            {
                connected = connected || (b.Connected && b.Network == review.PasteNetwork && EquipRules.DistanceSquared(c.Cell, b.Cell) <= 36);
            }
            if (!connected)
            {
                continue;
            }
            // RimWorld's north-facing footprint is centred with the even-size surplus on its positive edge.
            // Hopper touches the west edge.
            var h = new Cell(c.Cell.X - ((size.Width - 1) / 2) - 1, c.Cell.Z);
            // The dispenser's anchor and the hopper may stand on natural rock or fogged mountain, which the
            // rock step mines first; the rest of the 9x9 must be open ground a pawn can work from.
            bool Minable(Cell p) => rock.GetValueOrDefault(p) || (!listed.Contains(p) && p == h);
            var clear = true;
            for (var x = -4; x <= 4; x++)
            {
                for (var z = -4; z <= 4; z++)
                {
                    var p = new Cell(c.Cell.X + x, c.Cell.Z + z);
                    clear = clear && (cells.GetValueOrDefault(p) || ((p == c.Cell || p == h) && Minable(p)));
                }
            }
            if (!clear || r.Field.Site.Protected.Contains(h))
            {
                continue;
            }
            var infra = v.Meals.Paste ?? throw new RequiredFactException("paste infrastructure");
            var buildings = new List<SiteBuilding>
            {
                new() { Definition = infra!.Name, Cell = c.Cell, Rotation = Rotation.North },
                new() { Definition = hopper.Name, Cell = h, Rotation = Rotation.North },
            };
            var watts = infra.PowerW;
            var candidate = new SiteTypeCandidate
            {
                Kind = SiteKind.Paste,
                Buildings = EquatableArray.From(buildings),
                Cells = 2,
                Terms = EquatableArray.From([new FarmSiteTerm("power_w", watts)]),
            };
            return new SiteTypePlan { Kind = SiteKind.Paste, Buildings = EquatableArray.From(buildings), Candidates = EquatableArray.From([candidate]) };
        }
        return null;
    }
}
