package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func questMonument(m *o.QuestMonument) domain.Fact[policy.QuestMonument] {
	if m == nil {
		return domain.Unknown[policy.QuestMonument]()
	}
	row := policy.QuestMonument{Marker: m.GetMarkerId(), Def: m.GetDefName(), Map: domain.MapID(m.GetMapId()), Packed: optional(m.Packed), Installed: optional(m.Installed), Complete: optional(m.Complete), AllDone: optional(m.AllDone), DisallowedBuilding: m.GetDisallowedBuildingId(), DisallowedTicks: optional(m.DisallowedTicks)}
	row.Offered = optional(m.Offered)
	row.ClearSite = optional(m.ClearSite)
	for _, r := range m.SuppliedResources {
		row.SuppliedResources = append(row.SuppliedResources, policy.Amount{Resource: policy.Resource(r.GetDefName()), Count: r.GetUnits()})
	}
	for _, r := range m.AvailableResources {
		row.AvailableResources = append(row.AvailableResources, policy.Amount{Resource: policy.Resource(r.GetDefName()), Count: r.GetUnits()})
	}
	if m.Cell != nil {
		row.Cell = domain.Cell{X: m.Cell.GetX(), Z: m.Cell.GetZ()}
	}
	for _, c := range m.InstallCells {
		row.InstallCells = append(row.InstallCells, domain.Cell{X: c.GetX(), Z: c.GetZ()})
	}
	for _, p := range m.Pieces {
		row.Pieces = append(row.Pieces, policy.QuestMonumentPiece{Def: p.GetDefName(), Stuff: p.GetStuff(), Offset: domain.Cell{X: p.Offset.GetX(), Z: p.Offset.GetZ()}, Rotation: []domain.Rotation{domain.North, domain.East, domain.South, domain.West}[p.GetRotation()], Built: optional(p.Built), Queued: optional(p.Queued), Allowed: optional(p.Allowed), AllowedStuffs: append([]string(nil), p.AllowedStuffs...)})
	}
	for i, p := range m.Pieces {
		for _, option := range p.BuildOptions {
			rowOption := policy.QuestMonumentBuildOption{Stuff: option.GetStuff(), Work: option.GetWork()}
			for _, cost := range option.Costs {
				rowOption.Costs = append(rowOption.Costs, policy.Amount{Resource: policy.Resource(cost.GetDefName()), Count: cost.GetUnits()})
			}
			row.Pieces[i].BuildOptions = append(row.Pieces[i].BuildOptions, rowOption)
		}
		for _, c := range p.Footprint {
			row.Pieces[i].Footprint = append(row.Pieces[i].Footprint, domain.Cell{X: c.GetX(), Z: c.GetZ()})
		}
	}
	for _, r := range m.Resources {
		resource := policy.QuestMonumentResource{ID: r.GetId(), Def: r.GetDefName(), Cell: domain.Cell{X: r.Cell.GetX(), Z: r.Cell.GetZ()}, InStorage: optional(r.InStorage)}
		for _, pawn := range r.EligibleHaulers {
			resource.Haulers = append(resource.Haulers, domain.PawnID(pawn))
		}
		row.Resources = append(row.Resources, resource)
	}
	return domain.Known(row)
}
