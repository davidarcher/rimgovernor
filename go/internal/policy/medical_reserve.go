package policy

import (
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"math"
)

// MedicineStack is an observed medicine stack, not a recipe or promised output.
type MedicineStack struct {
	ID         string
	Definition Resource
	Count      int64
	Forbidden  bool
	Perishable domain.Fact[bool]
	RotTicks   domain.Fact[int64]
}
type MedicalReserveObservation struct {
	Colonists domain.Fact[int64]
	Items     domain.Fact[[]MedicineStack]
	// Resources is the complete usable native stock census, excluding inaccessible
	// and forbidden stock. It is capped again against the actual medicine stacks.
	Resources domain.Fact[[]Amount]
}
type MedicalReservePolicy struct{ MinimumPerColonist, TargetPerColonist int64 }
type MedicalReserveReview struct {
	Active                          bool
	Stock, Entry, Target, Replenish domain.Fact[int64]
}

func DefaultMedicalReservePolicy() MedicalReservePolicy { return MedicalReservePolicy{1, 3} }

// ReviewMedicalReserve preserves the latch through unavailable reads. Equal entry
// stock does not activate it; equal recovery stock clears it. Future production
// and expired stacks never contribute to the current reserve.
func ReviewMedicalReserve(v MedicalReserveObservation, active bool, p MedicalReservePolicy) (MedicalReserveReview, error) {
	r := MedicalReserveReview{Active: active}
	invalid := errors.New("invalid medicine reserve facts or thresholds")
	if p.MinimumPerColonist < 0 || p.TargetPerColonist <= p.MinimumPerColonist {
		return r, invalid
	}
	people, pk := v.Colonists.Value()
	items, ik := v.Items.Value()
	resources, rk := v.Resources.Value()
	if pk && (people < 0 || people > 0 && p.TargetPerColonist > math.MaxInt64/people) {
		return r, invalid
	}
	if !pk || !ik || !rk {
		return r, nil
	}
	if len(items) > 256 || len(resources) > 4096 {
		return r, invalid
	}
	stockByDefinition := map[Resource]int64{}
	for _, q := range resources {
		if !validResource(q.Resource) || q.Count < 0 {
			return r, invalid
		}
		if _, exists := stockByDefinition[q.Resource]; exists {
			return r, invalid
		}
		stockByDefinition[q.Resource] = q.Count
	}
	usable := map[Resource]int64{}
	seen := map[string]bool{}
	for _, item := range items {
		if !foodID(item.ID) || seen[item.ID] || !validResource(item.Definition) || item.Count < 0 {
			return r, invalid
		}
		seen[item.ID] = true
		perishable, known := item.Perishable.Value()
		ticks, tk := item.RotTicks.Value()
		if tk && ticks < 0 {
			return r, invalid
		}
		if item.Forbidden || !(known && !perishable || tk && ticks > 0) {
			continue
		}
		if item.Count > math.MaxInt64-usable[item.Definition] {
			return r, invalid
		}
		usable[item.Definition] += item.Count
	}
	var stock int64
	for definition, count := range usable {
		add := min(count, stockByDefinition[definition])
		if add > math.MaxInt64-stock {
			return r, invalid
		}
		stock += add
	}
	entry, target := people*p.MinimumPerColonist, people*p.TargetPerColonist
	threshold := entry
	if active {
		threshold = target
	}
	r.Active = people > 0 && stock < threshold
	r.Stock, r.Entry, r.Target = domain.Known(stock), domain.Known(entry), domain.Known(target)
	replenishment := int64(0)
	if r.Active {
		replenishment = max(0, target-stock)
	}
	r.Replenish = domain.Known(replenishment)
	return r, nil
}
