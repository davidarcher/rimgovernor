package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func colonyUpkeep(v *o.ColonyFactsSnapshot) policy.UpkeepObservation {
	r := policy.UpkeepObservation{}
	u := v.GetUpkeep().GetObserved()
	if u == nil {
		return r
	}
	if !hasIssue(u.Issues, "items") {
		rows := []policy.UpkeepItem{}
		known := true
		for _, item := range u.Items {
			if item.Roofed == nil || item.InStorage == nil || item.Forbidden == nil || item.BaseDeteriorationRate == nil || item.Medicine == nil || item.Count == nil {
				known = false
				break
			}
			rows = append(rows, policy.UpkeepItem{ID: item.Item.GetId(), Definition: item.Item.GetDefName(), Cell: domain.Cell{X: item.Item.GetPosition().GetX(), Z: item.Item.GetPosition().GetZ()}, Roofed: item.GetRoofed(), InStorage: item.GetInStorage(), Forbidden: item.GetForbidden(), Deterioration: item.GetBaseDeteriorationRate(), Medicine: item.GetMedicine(), Count: item.GetCount(), RotTicks: optional(item.RotTicks)})
		}
		if known {
			r.Items = domain.Known(rows)
		}
	}
	if !hasIssue(u.Issues, "structures") {
		rows := []policy.UpkeepStructure{}
		known := true
		for _, item := range u.Structures {
			b := item.Building
			if item.Home == nil || item.RepairPriority == nil || b.HitPoints == nil || b.MaxHitPoints == nil {
				known = false
				break
			}
			rows = append(rows, policy.UpkeepStructure{ID: b.Building.GetId(), Cell: domain.Cell{X: b.Building.GetPosition().GetX(), Z: b.Building.GetPosition().GetZ()}, Home: item.GetHome(), HitPoints: int64(b.GetHitPoints()), MaxHitPoints: int64(b.GetMaxHitPoints()), Priority: int(item.GetRepairPriority())})
		}
		if known {
			r.Structures = domain.Known(rows)
		}
	}
	if !hasIssue(u.Issues, "fires") {
		rows := []policy.UpkeepFire{}
		known := true
		for _, item := range u.Fires {
			if item.Home == nil {
				known = false
				break
			}
			rows = append(rows, policy.UpkeepFire{ID: item.Fire.GetId(), Home: item.GetHome(), Size: optional(item.Size)})
		}
		if known {
			r.Fires = domain.Known(rows)
		}
	}
	if !hasIssue(u.Issues, "filth") {
		rows := []policy.UpkeepFilth{}
		known := true
		for _, item := range u.Filth {
			if item.Home == nil || item.Thickness == nil {
				known = false
				break
			}
			rows = append(rows, policy.UpkeepFilth{ID: item.Filth.GetId(), Definition: item.Filth.GetDefName(), Cell: domain.Cell{X: item.Filth.GetPosition().GetX(), Z: item.Filth.GetPosition().GetZ()}, Home: item.GetHome(), Room: item.GetRoomRole(), RoomID: optional(item.RoomId), Thickness: item.GetThickness()})
		}
		if known {
			r.Filth = domain.Known(rows)
		}
	}
	if !hasIssue(u.Issues, "lighting") {
		r.Lighting = colonyLighting(u.Lighting)
	}
	return r
}

// colonyLighting decodes the lighting section; any row missing a measured
// glow, roof or lit flag leaves the whole census unknown so MaintainLighting
// keeps its previous latch instead of reasoning from half a map.
func colonyLighting(section *o.LightingSection) domain.Fact[policy.LightingObservation] {
	l := section.GetObserved()
	if l == nil {
		return domain.Fact[policy.LightingObservation]{}
	}
	r := policy.LightingObservation{WorkCells: []policy.WorkLightCell{}, Lamps: []policy.Lamp{}}
	for _, row := range l.WorkCells {
		if row.Glow == nil || row.Roofed == nil {
			return domain.Fact[policy.LightingObservation]{}
		}
		r.WorkCells = append(r.WorkCells, policy.WorkLightCell{Bench: row.Bench.GetId(), Definition: row.Bench.GetDefName(), Cell: domain.Cell{X: row.Cell.GetX(), Z: row.Cell.GetZ()}, Glow: row.GetGlow(), Roofed: row.GetRoofed(), Room: optional(row.RoomId)})
	}
	for _, row := range l.Lamps {
		b := row.GetBuilding()
		s := b.GetService()
		if row.GlowRadius == nil || row.Lit == nil {
			return domain.Fact[policy.LightingObservation]{}
		}
		r.Lamps = append(r.Lamps, policy.Lamp{ID: b.GetBuilding().GetId(), Definition: b.GetBuilding().GetDefName(), Cell: domain.Cell{X: b.GetBuilding().GetPosition().GetX(), Z: b.GetBuilding().GetPosition().GetZ()}, Radius: row.GetGlowRadius(), Lit: row.GetLit(), Room: optional(row.RoomId),
			Powered: optional(s.PowerOn), Connected: optional(s.Connected), SwitchedOn: optional(s.SwitchedOn), OutOfFuel: optional(s.OutOfFuel), BrokenDown: optional(s.BrokenDown), FuelDefinitions: append([]string(nil), s.GetAllowedFuelDefs()...)})
	}
	return domain.Known(r)
}
