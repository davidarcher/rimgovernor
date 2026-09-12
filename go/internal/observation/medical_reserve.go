package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func colonyMedicalReserve(v *o.ColonyFactsSnapshot) policy.MedicalReserveObservation {
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
	if u == nil || hasIssue(u.Issues, "items") {
		return r
	}
	rows := []policy.MedicineStack{}
	for _, item := range u.Items {
		if item.Medicine == nil {
			return r
		}
		if !item.GetMedicine() {
			continue
		}
		if item.Count == nil || item.Forbidden == nil {
			return r
		}
		rows = append(rows, policy.MedicineStack{ID: item.Item.GetId(), Definition: policy.Resource(item.Item.GetDefName()), Count: item.GetCount(), Forbidden: item.GetForbidden(), Perishable: optional(item.Perishable), RotTicks: optional(item.RotTicks)})
	}
	r.Items = domain.Known(rows)
	return r
}
