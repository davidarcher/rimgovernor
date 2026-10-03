package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// BiotechColony is the Biotech colony section (#1679): pollution makers and
// removers, wastepack stacks, mech gestators and chargers, and baby care. An
// absent scalar is unknown, never zero; a Fact for the whole section is
// unknown without Biotech or when the read failed.
type BiotechColony struct {
	TotalPollution  domain.Fact[int32]
	PollutableCells domain.Fact[uint32]
	ClearAreaID     domain.Fact[string]
	ClearAreaCells  domain.Fact[int32]
	Polluters       []Polluter
	Wastepacks      []Wastepack
	Atomizers       []WastepackAtomizer
	Pumps           []PollutionPump
	Gestators       []MechGestator
	Chargers        []MechCharger
	Babies          []BabyCare
	Breastfeeders   []string
}

// BuildingRow heads every building row: the thing id, definition and cell.
type BuildingRow struct {
	ID, Definition string
	Position       domain.Cell
}
type Polluter struct {
	BuildingRow
	Polluting   domain.Fact[bool]
	CellsPerDay domain.Fact[int32]
}
type Wastepack struct {
	BuildingRow
	Count                                                   int32
	Frozen, Outdoors, InAtomizer, CanDissolveNow, Forbidden domain.Fact[bool]
	DeteriorationRate                                       domain.Fact[int32]
}
type WastepackAtomizer struct {
	BuildingRow
	Powered, AutoLoad    domain.Fact[bool]
	SpaceLeft, TicksLeft domain.Fact[int32]
	FillPercent          domain.Fact[float64]
}
type PollutionPump struct {
	BuildingRow
	Powered, DisabledByArtificialBuildings domain.Fact[bool]
}

// MechGestator is a gestator and its active bill; the bill facts are unknown
// (not zero) without one.
type MechGestator struct {
	BuildingRow
	Powered                              domain.Fact[bool]
	BillID, State, MechKind, BoundPawnID domain.Fact[string]
	CyclesCompleted, WasteCount          domain.Fact[int32]
	BandwidthCost, FormingPercent        domain.Fact[float64]
}
type MechCharger struct {
	BuildingRow
	Powered, FullOfWaste domain.Fact[bool]
	ChargingMechID       domain.Fact[string]
	WasteCount           domain.Fact[int32]
}
type BabyCare struct {
	PawnID                                            string
	WantsSuckle, CanSuckleNow, BeingPlayedWith, InBed domain.Fact[bool]
	Autofeeders                                       []BabyAutofeeder
}
type BabyAutofeeder struct{ PawnID, Mode string }

func buildingRow(id, def *string, at *c.Cell) BuildingRow {
	return BuildingRow{ID: *id, Definition: *def, Position: domain.Cell{X: at.GetX(), Z: at.GetZ()}}
}

// colonyBiotech projects a validated section; an absent or unavailable one is
// an unknown fact.
func colonyBiotech(section *o.BiotechSection) domain.Fact[BiotechColony] {
	f := section.GetObserved()
	if f == nil {
		return domain.Fact[BiotechColony]{}
	}
	p := f.Pollution
	if p == nil {
		p = &o.PollutionTotals{}
	}
	r := BiotechColony{TotalPollution: optional(p.TotalPollution), PollutableCells: optional(p.PollutableCells),
		ClearAreaID: optional(p.ClearAreaId), ClearAreaCells: optional(p.ClearAreaCells), Breastfeeders: f.Breastfeeders}
	for _, x := range f.Polluters {
		r.Polluters = append(r.Polluters, Polluter{BuildingRow: buildingRow(x.ThingId, x.DefName, x.Position), Polluting: optional(x.Polluting), CellsPerDay: optional(x.CellsPerDay)})
	}
	for _, x := range f.Wastepacks {
		r.Wastepacks = append(r.Wastepacks, Wastepack{BuildingRow: buildingRow(x.ThingId, x.DefName, x.Position), Count: x.GetCount(), Frozen: optional(x.Frozen),
			Outdoors: optional(x.Outdoors), InAtomizer: optional(x.InAtomizer), CanDissolveNow: optional(x.CanDissolveNow), Forbidden: optional(x.Forbidden),
			DeteriorationRate: optional(x.DeteriorationRate)})
	}
	for _, x := range f.Atomizers {
		r.Atomizers = append(r.Atomizers, WastepackAtomizer{BuildingRow: buildingRow(x.ThingId, x.DefName, x.Position), Powered: optional(x.Powered), AutoLoad: optional(x.AutoLoad),
			SpaceLeft: optional(x.SpaceLeft), TicksLeft: optional(x.TicksLeft), FillPercent: optional(x.FillPercent)})
	}
	for _, x := range f.Pumps {
		r.Pumps = append(r.Pumps, PollutionPump{BuildingRow: buildingRow(x.ThingId, x.DefName, x.Position), Powered: optional(x.Powered),
			DisabledByArtificialBuildings: optional(x.DisabledByArtificialBuildings)})
	}
	for _, x := range f.Gestators {
		r.Gestators = append(r.Gestators, MechGestator{BuildingRow: buildingRow(x.ThingId, x.DefName, x.Position), Powered: optional(x.Powered), BillID: optional(x.BillId),
			State: optional(x.State), MechKind: optional(x.MechKind), BoundPawnID: optional(x.BoundPawnId), CyclesCompleted: optional(x.CyclesCompleted),
			WasteCount: optional(x.WasteCount), BandwidthCost: optional(x.BandwidthCost), FormingPercent: optional(x.FormingPercent)})
	}
	for _, x := range f.Chargers {
		r.Chargers = append(r.Chargers, MechCharger{BuildingRow: buildingRow(x.ThingId, x.DefName, x.Position), Powered: optional(x.Powered), FullOfWaste: optional(x.FullOfWaste),
			ChargingMechID: optional(x.ChargingMechId), WasteCount: optional(x.WasteCount)})
	}
	for _, x := range f.Babies {
		b := BabyCare{PawnID: x.GetPawnId(), WantsSuckle: optional(x.WantsSuckle), CanSuckleNow: optional(x.CanSuckleNow), BeingPlayedWith: optional(x.BeingPlayedWith), InBed: optional(x.InBed)}
		for _, a := range x.Autofeeders {
			b.Autofeeders = append(b.Autofeeders, BabyAutofeeder{PawnID: a.GetPawnId(), Mode: a.GetMode()})
		}
		r.Babies = append(r.Babies, b)
	}
	return domain.Known(r)
}

// MechChargerRows are the section's chargers as the recharge policy reads
// them (#1688): charging is a mech attached.
func (b BiotechColony) MechChargerRows() []policy.MechCharger {
	out := make([]policy.MechCharger, 0, len(b.Chargers))
	for _, c := range b.Chargers {
		// Native sets charging_mech_id exactly while a mech is attached.
		_, charging := c.ChargingMechID.Value()
		row := policy.MechCharger{Powered: c.Powered, FullOfWaste: c.FullOfWaste, Charging: domain.Known(charging)}
		out = append(out, row)
	}
	return out
}

// MechChargerDefs are the catalog definitions that are mech chargers
// (PlanningDefinition.MechCharger, from the game's Building_MechCharger
// class), in catalog order; the build planner picks among them.
func MechChargerDefs(defs []PlanningDefinition) []PlanningDefinition {
	var out []PlanningDefinition
	for _, d := range defs {
		if charger, ok := d.MechCharger.Value(); ok && charger {
			out = append(out, d)
		}
	}
	return out
}
