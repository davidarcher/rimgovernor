package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func wasteLocation(v o.WasteLocation) policy.WasteState {
	switch v {
	case o.WasteLocation_WASTE_LOCATION_EXPOSED:
		return policy.WasteExposed
	case o.WasteLocation_WASTE_LOCATION_RELOCATED:
		return policy.WasteRelocated
	case o.WasteLocation_WASTE_LOCATION_BURIED:
		return policy.WasteBuried
	default:
		return ""
	}
}

// colonyWaste decodes the same generic per-tick ColonyFactsSnapshot's
// embedded WasteReply (no dedicated read call needed, unlike
// husbandry/population's own dedicated census reads) into the plain census
// pendingWaste/SelectWasteMethod expect. A missing observed snapshot or an
// incomplete page (unlike the exact-CAS husbandry/resource reads, this
// generic census is never repaged mid-review) stays unknown; a row's own
// missing eligibility/state is treated as not-pending, mirroring
// waste_management.py's own truthy `.get('eligible') is True` check rather
// than failing the whole census over one partial row.
func colonyWaste(v *o.ColonyFactsSnapshot) domain.Fact[[]policy.WasteItem] {
	snapshot := v.GetWaste().GetObserved()
	if snapshot == nil {
		return domain.Unknown[[]policy.WasteItem]()
	}
	counts := snapshot.Completeness
	if counts == nil || counts.Page == nil || !counts.Page.GetComplete() || counts.Page.GetNextCursor() != "" {
		return domain.Unknown[[]policy.WasteItem]()
	}
	items := make([]policy.WasteItem, 0, len(snapshot.Items))
	for _, row := range snapshot.Items {
		if row == nil {
			continue
		}
		id := row.GetThing().GetId()
		if id == "" {
			continue
		}
		position := row.GetThing().GetPosition()
		if position == nil || position.X == nil || position.Z == nil || position.GetX() < 0 || position.GetZ() < 0 {
			continue
		}
		cell := domain.Cell{X: position.GetX(), Z: position.GetZ()}
		items = append(items, policy.WasteItem{ID: id, Kind: row.GetKind(), State: wasteLocation(row.GetState()), Eligible: row.GetEligible(), Cell: cell})
	}
	return domain.Known(items)
}
