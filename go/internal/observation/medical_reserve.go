package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// ColonyMedicalReserve decodes the medicine-reserve facts of one colony read.
func ColonyMedicalReserve(v *o.ColonyFactsSnapshot, tables bridge.Tables) policy.MedicalReserveObservation {
	r := policy.MedicalReserveObservation{Colonists: countFact(v.ColonistCount)}
	if !hasIssue(v.Issues, "resources") {
		rows := []policy.Amount{}
		known := true
		for _, q := range v.Resources {
			if q.Units == nil {
				known = false
				break
			}
			rows = append(rows, policy.Amount{Resource: policy.Resource(q.GetDefName()), Count: q.GetUnits()})
		}
		if known {
			r.Resources = domain.Known(rows)
		}
	}
	u := v.GetUpkeep().GetObserved()
	if u == nil || hasIssue(u.Issues, "items") || !headed(tables, u.Items, (*o.UpkeepItem).GetItem) {
		return r
	}
	rows := []policy.MedicineStack{}
	for _, item := range u.Items {
		// Whether the def is medicine is the catalog's (#1733); a def it has
		// no row for leaves the stock unknown.
		medicine, err := tables.Catalog.Medicine(tables.Entity(item.Item).GetDefName())
		if err != nil {
			return r
		}
		if !medicine {
			continue
		}
		if item.Count == nil || item.Forbidden == nil {
			return r
		}
		rows = append(rows, policy.MedicineStack{ID: item.Item.GetId(), Definition: policy.Resource(tables.Entity(item.Item).GetDefName()), Count: item.GetCount(), Forbidden: item.GetForbidden(), Perishable: domain.Known(item.RotTicks != nil), RotTicks: optional(item.RotTicks)})
	}
	r.Items = domain.Known(rows)
	return r
}
