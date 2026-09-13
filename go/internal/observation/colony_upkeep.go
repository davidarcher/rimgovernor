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
			rows = append(rows, policy.UpkeepStructure{ID: b.Building.GetId(), Home: item.GetHome(), HitPoints: int64(b.GetHitPoints()), MaxHitPoints: int64(b.GetMaxHitPoints()), Priority: int(item.GetRepairPriority())})
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
			rows = append(rows, policy.UpkeepFilth{ID: item.Filth.GetId(), Home: item.GetHome(), Room: item.GetRoomRole(), Thickness: item.GetThickness()})
		}
		if known {
			r.Filth = domain.Known(rows)
		}
	}
	return r
}
