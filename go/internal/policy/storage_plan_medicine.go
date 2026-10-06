package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// maxMedicineHaulConsumers bounds the beds the haul-distance ranking walks
// from: each medical bed is one unit of traffic.
const maxMedicineHaulConsumers = 64

// medicineStore is the hospital's medicine store (#1776, #2219): a 2x2 inside
// the first planned hospital, nearest the template's bed slots by
// traffic-weighted walking distance (#723), off the planned beds and monitors.
// Without bed slots (no template fit) it sits nearest the hospital's door.
func (r StorageRequest) medicineStore() (Store, bool) {
	if r.Layout == nil {
		return Store{}, false
	}
	var hospital PlannedRoom
	for _, planned := range r.Layout.AllRooms() {
		if planned.Role == PlannedHospital {
			hospital = planned
			break
		}
	}
	if hospital.Interior.Width <= 0 {
		return Store{}, false
	}
	var avoid, beds []domain.Cell
	if in, ok := InteriorRoomFromLayout(hospital, r.Shapes); ok {
		if plan, ok := PlanInterior(in, InteriorPieceDef{}); ok {
			for _, p := range plan.Pieces {
				avoid = append(avoid, rectCells(p.Rect)...)
				if p.Row == "beds" {
					beds = append(beds, rectCells(p.Rect)...)
				}
			}
		}
	}
	open := newStockpileOpen(StockpileRequest{Bounds: r.Bounds, Cells: r.Cells, Protected: r.Protected})
	open.only = cellSet(withoutCells(rectCells(hospital.Interior), avoid))
	rects := rectangleSites(open, hospital.Door, 2, 2, nil, int(hospital.Interior.Width*hospital.Interior.Height))
	if len(rects) == 0 {
		return Store{}, false
	}
	best := rects[0]
	if len(beds) > 0 {
		consumers := make([]HaulConsumer, 0, len(beds))
		for _, c := range beds {
			consumers = append(consumers, HaulConsumer{Cells: []domain.Cell{c}, Weight: 1})
		}
		if len(consumers) > maxMedicineHaulConsumers {
			consumers = consumers[:maxMedicineHaulConsumers]
		}
		if costs, err := HaulCosts(r.Cells, consumers); err == nil {
			best = RankSitesByHaul(rects, costs)[0]
		}
	}
	// The anchor is the best patch's centre: the store takes that patch, else
	// the free one nearest it.
	return Store{StoreSite: StoreSite{Role: plannedKey(domain.MedicineRolePrefix, hospital.Interior), Interior: hospital.Interior, Width: 2, Height: 2,
		Anchor: domain.Cell{X: best.X + best.Width/2, Z: best.Z + best.Height/2}, Avoid: avoid,
		Filter: domain.MedicineFilter(), Priority: domain.ImportantPriority}}, true
}
