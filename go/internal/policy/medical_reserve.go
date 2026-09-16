package policy

import (
	"crypto/sha256"
	"encoding/json"
	"errors"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"math"
	"sort"
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

type MedicineMethodKind string

const (
	MedicineUnknown   MedicineMethodKind = "unknown"
	MedicineRecovered MedicineMethodKind = "recovered"
	MedicineProduce   MedicineMethodKind = "produce"
	MedicineWait      MedicineMethodKind = "wait_for_existing_work"
	MedicineBlocked   MedicineMethodKind = "no_eligible_method"
)

type MedicineMethod struct {
	Kind          MedicineMethodKind
	ID            domain.MethodID
	Bench, Recipe string
	Resource      Resource
	Target        int64
	Costs         []Amount
	Filter        []Resource
	RequiredWork  []WorkRequirement
}

// MedicinePlanningRequest names the one resource MaintainMedicalReserves
// replenishes (MedicineHerbal; the planner refuses to run without native
// confirmation that definition exists)
// and reuses the exact bench/recipe census shape GearProduce established
// (policy.GearBench/GearRecipe): bridge.ReadGearBenches/ReadSupplyStock are
// fully generic native reads, not specific to gear-crafting benches, so no
// separate medical bench census type is needed.
type MedicinePlanningRequest struct {
	Review   MedicalReserveReview
	Resource Resource
	Seen     []domain.MethodID
	Benches  domain.Fact[[]GearBench]
	Stock    []Stock
	Rules    []ResourceRule
	Holds    []Amount
}

func medicineMethodID(resource Resource, bench, recipe string) domain.MethodID {
	value := struct {
		Resource      Resource
		Bench, Recipe string
	}{resource, bench, recipe}
	data, _ := json.Marshal(value)
	sum := sha256.Sum256(data)
	return domain.MethodID(fmt.Sprintf("medicine-produce-%x", sum[:16]))
}

// SelectMedicineMethod proposes one repeat-count StockTarget bill that keeps
// at least the reviewed recovery Target units of Resource in stock, the same
// pause-when-satisfied bill GearProduce dispatches through
// ProductionBillAction/domain.StockTarget. It issues no game orders and does
// not reserve resources; the shared method admission must recheck these
// costs against concurrent plans before committing. Unlike GearProduce there
// is no per-pawn candidate to prefer over crafting: with the reserve active,
// the single Resource target is either already covered by an observed
// active bill (MedicineWait), fundable at some bench/recipe (MedicineProduce)
// or blocked.
func SelectMedicineMethod(r MedicinePlanningRequest) (MedicineMethod, error) {
	if !r.Review.Active {
		return MedicineMethod{Kind: MedicineRecovered}, nil
	}
	target, known := r.Review.Target.Value()
	if !known || !validResource(r.Resource) {
		return MedicineMethod{Kind: MedicineUnknown}, nil
	}
	if target <= 0 {
		return MedicineMethod{Kind: MedicineRecovered}, nil
	}
	if target > 10000 {
		return MedicineMethod{Kind: MedicineBlocked}, nil
	}
	if len(r.Seen) > 4096 {
		return MedicineMethod{}, errors.New("medicine method history exceeds bound")
	}
	seen := map[domain.MethodID]bool{}
	for _, id := range r.Seen {
		if !foodID(string(id)) || seen[id] {
			return MedicineMethod{}, errors.New("invalid medicine method history")
		}
		seen[id] = true
	}
	benches, known := r.Benches.Value()
	if !known {
		return MedicineMethod{Kind: MedicineUnknown}, nil
	}
	gearRequest := GearPlanningRequest{Stock: r.Stock, Rules: r.Rules, Holds: r.Holds}
	if err := validateGearProduction(benches, gearRequest); err != nil {
		return MedicineMethod{}, err
	}
	benches = append([]GearBench(nil), benches...)
	sort.Slice(benches, func(i, j int) bool { return benches[i].ID < benches[j].ID })
	for _, b := range benches {
		bills, known := b.Bills.Value()
		if !known {
			return MedicineMethod{Kind: MedicineUnknown}, nil
		}
		for _, bill := range bills {
			if !containsResource(bill.Products, r.Resource) {
				continue
			}
			active, known := bill.Active.Value()
			if !known {
				return MedicineMethod{Kind: MedicineUnknown}, nil
			}
			if active {
				return MedicineMethod{Kind: MedicineWait}, nil
			}
		}
	}
	for _, b := range benches {
		recipes, known := b.Recipes.Value()
		if !known {
			return MedicineMethod{Kind: MedicineUnknown}, nil
		}
		recipes = append([]GearRecipe(nil), recipes...)
		sort.Slice(recipes, func(i, j int) bool { return recipes[i].Definition < recipes[j].Definition })
		for _, recipe := range recipes {
			if !containsResource(recipe.Products, r.Resource) {
				continue
			}
			available, ak := recipe.Available.Value()
			on, ok := recipe.AvailableOn.Value()
			if ak && !available || ok && !on {
				continue
			}
			if !ak || !ok {
				return MedicineMethod{Kind: MedicineUnknown}, nil
			}
			work, known := recipe.RequiredWork.Value()
			if !known {
				return MedicineMethod{Kind: MedicineUnknown}, nil
			}
			slots, known := recipe.Ingredients.Value()
			if !known {
				return MedicineMethod{Kind: MedicineUnknown}, nil
			}
			costs, filter, funded, unknown := gearIngredients(slots, "", gearRequest)
			if unknown {
				return MedicineMethod{Kind: MedicineUnknown}, nil
			}
			if !funded {
				continue
			}
			id := medicineMethodID(r.Resource, b.ID, recipe.Definition)
			if seen[id] {
				return MedicineMethod{Kind: MedicineWait, ID: id}, nil
			}
			return MedicineMethod{Kind: MedicineProduce, ID: id, Bench: b.ID, Recipe: recipe.Definition, Resource: r.Resource, Target: target, Costs: costs, Filter: filter, RequiredWork: append([]WorkRequirement(nil), work...)}, nil
		}
	}
	return MedicineMethod{Kind: MedicineBlocked}, nil
}
