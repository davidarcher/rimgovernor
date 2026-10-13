namespace RimGovernor.Brain.Policy;

public sealed partial record StoreView
{
    /// <summary>Bounds the beds the haul-distance ranking walks from: each medical bed is one unit of traffic.</summary>
    private const int MaxMedicineHaulConsumers = 64;

    /// <summary>
    /// The hospital's medicine store: a 2x2 inside the first planned hospital, nearest the template's bed slots
    /// by traffic-weighted walking distance, off the planned beds and monitors. Without bed slots (no template
    /// fit) it sits nearest the hospital's door.
    /// </summary>
    internal (Store? Store, bool Ok) MedicineStore()
    {
        if (Layout is null)
        {
            return (null, false);
        }
        var hospital = new PlannedRoom();
        foreach (var planned in Layout.Rooms)
        {
            if (planned.Role == PlannedRole.Hospital)
            {
                hospital = planned;
                break;
            }
        }
        var hospitalBounds = hospital.Region.Bounds();
        if (hospitalBounds.Width <= 0)
        {
            return (null, false);
        }
        var avoid = new List<Cell>();
        var beds = new List<Cell>();
        var (room, ok) = PolicyFunctions.InteriorRoomFromLayout(hospital, Shapes);
        if (ok)
        {
            var (plan, planned) = PolicyFunctions.PlanRoomInterior(room!, new InteriorPieceDef());
            if (planned)
            {
                foreach (var p in plan!.Pieces)
                {
                    avoid.AddRange(PolicyFunctions.RectCells(p.Rect));
                    if (p.Row == "beds")
                    {
                        beds.AddRange(PolicyFunctions.RectCells(p.Rect));
                    }
                }
            }
        }
        var open = PolicyFunctions.NewStockpileOpen(new StockpileRequest { Bounds = Bounds, Cells = Cells, Protected = Protected });
        open = open with { Only = PolicyFunctions.CellSet(PolicyFunctions.WithoutCells(PolicyFunctions.RectCells(hospitalBounds), avoid)) };
        var rects = PolicyFunctions.RectangleSites(open, hospital.Door, 2, 2, null, hospitalBounds.Width * hospitalBounds.Height);
        if (rects.Count == 0)
        {
            return (null, false);
        }
        var best = rects[0];
        if (beds.Count > 0)
        {
            var consumers = beds.Select(c => new HaulConsumer { Cells = [c], Weight = 1 }).ToList();
            if (consumers.Count > MaxMedicineHaulConsumers)
            {
                consumers = consumers.GetRange(0, MaxMedicineHaulConsumers);
            }
            try
            {
                var costs = PolicyFunctions.HaulCosts(Cells, consumers);
                best = PolicyFunctions.RankSitesByHaul(rects, costs)[0];
            }
            catch (DomainException)
            {
                // The haul ranking is unavailable: keep the nearest-door patch.
            }
        }
        // The anchor is the best patch's centre: the store takes that patch, else the free one nearest it.
        return (
            new Store
            {
                Site = new StoreSite
                {
                    Role = PolicyFunctions.PlannedKey(DomainConstants.MedicineRolePrefix, hospitalBounds),
                    Interior = hospitalBounds,
                    Width = 2,
                    Height = 2,
                    Anchor = new Cell(best.X + (best.Width / 2), best.Z + (best.Height / 2)),
                    Avoid = avoid,
                    Filter = StockpileFilter.MedicineFilter(),
                    Priority = StockpilePriority.Important,
                },
            },
            true
        );
    }
}
