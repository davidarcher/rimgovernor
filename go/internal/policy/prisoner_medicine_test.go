package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// careLimitedPrisoner holds only industrial medicine: every harvest reads
// ingredients off the map because herbal care forbids it (#1239).
func careLimitedPrisoner(id string) PrisonerFacts {
	var ops []SurgeryOperation
	for _, op := range []SurgeryOperation{harvestOp("Kidney", 20, false), harvestOp("Kidney", 21, false), harvestOp("Lung", 18, false), harvestOp("Lung", 19, false)} {
		op.IngredientsOnMap, op.CareLimited = domain.Known(false), domain.Known(true)
		ops = append(ops, op)
	}
	row := harvestPrisoner(id, 0, ops...)
	row.MedicalCare = domain.Known("HerbalOrWorse")
	return row
}

func TestPrisonerCareLimitedHarvest(t *testing.T) {
	five := domain.Known(PrisonerColony{Colonists: 5, BestSkill: core.BestSkill})
	sale := OrganNeeds(domain.Known([]CarePawn{}), nil, true, nil)
	limited := domain.Known([]PrisonerFacts{careLimitedPrisoner("p")})
	if _, ok := SelectOrganHarvest(limited, five, sale, nil); ok {
		t.Fatal("care-limited harvest queued")
	}
	h, ok := CareLimitedHarvest(limited, five, sale, nil, nil)
	if !ok || h.Prisoner != "p" || h.Part != 18 {
		t.Fatalf("care-limited harvest %+v %v", h, ok)
	}
	if _, ok := CareLimitedHarvest(domain.Known([]PrisonerFacts{harvestPrisoner("p", 0)}), five, sale, nil, nil); ok {
		t.Fatal("stocked harvest read as care-limited")
	}
	f := RoutineFacts{Prisoners: limited, PrisonerColony: five, Resources: domain.Known([]Amount{{Resource: "MedicineIndustrial", Count: 5}})}
	needs := PrisonerHerbalNeeds(map[Resource]int64{"Steel": 10}, f, domain.Known(true))
	if needs["MedicineHerbal"] != PrisonerSurgeryHerbal || needs["Steel"] != 10 {
		t.Fatalf("herbal want %v", needs)
	}
	if needs := PrisonerHerbalNeeds(nil, f, domain.Known(false)); needs["MedicineHerbal"] != 0 {
		t.Fatalf("herbal wanted with no harvest need: %v", needs)
	}
}

func TestPrisonerCarePins(t *testing.T) {
	row := func(id, care string, dead bool) PrisonerFacts {
		r := harvestPrisoner(id, 0)
		r.MedicalCare, r.Dead = domain.Known(care), domain.Known(dead)
		return r
	}
	pins := PrisonerCarePins(domain.Known([]PrisonerFacts{row("a", "NormalOrWorse", false), row("b", "HerbalOrWorse", false), row("c", "Best", false), row("d", "Best", true), harvestPrisoner("e", 0)}))
	if len(pins) != 2 || pins[0] != "a" || pins[1] != "c" {
		t.Fatalf("pins %v", pins)
	}
}
