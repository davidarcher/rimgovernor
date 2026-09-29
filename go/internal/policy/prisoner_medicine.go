package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Prisoner medicine (#1239, epic #1160): prisoners stay on herbal medicine
// at most. MaintainSurgery pins a prisoner whose care allows better back to
// PrisonerMedicalCare and never raises it. A harvest or part recovery that
// only the care limit blocks (better medicine stocked, no herbal) is refused
// with a reason naming the limit, and asks MaintainResource for herbal
// medicine through ResourceNeeds (healroot growing, trade buys).

// PrisonerMedicalCare is the care ceiling every colony prisoner is held at.
const PrisonerMedicalCare = "HerbalOrWorse"

// PrisonerSurgeryHerbal is the herbal stock a care-limited prisoner surgery
// asks for: vanilla's harvest and removal recipes take one medicine, and a
// second covers the tend after a failure.
const PrisonerSurgeryHerbal int64 = 2

// PrisonerCarePins lists the living prisoners whose known care allows
// medicine above herbal (NormalOrWorse, Best), by pawn id.
func PrisonerCarePins(prisoners domain.Fact[[]PrisonerFacts]) []domain.PawnID {
	rows, _ := prisoners.Value()
	var out []domain.PawnID
	for _, row := range rows {
		care, ck := row.MedicalCare.Value()
		dead, dk := row.Dead.Value()
		if ck && dk && !dead && (care == "NormalOrWorse" || care == "Best") {
			out = append(out, row.Pawn)
		}
	}
	return out
}

// CareLimitedHarvest is the organ harvest, else the part recovery, that
// SelectOrganHarvest or SelectPartRecovery would choose were every
// care-limited operation stocked, when the chosen operation is care-limited.
// ok is false when nothing is chosen or the choice is not blocked by care.
func CareLimitedHarvest(prisoners domain.Fact[[]PrisonerFacts], colony domain.Fact[PrisonerColony], needs, parts []OrganNeed, inFlight map[PawnID]bool) (OrganHarvest, bool) {
	rows, known := prisoners.Value()
	if !known {
		return OrganHarvest{}, false
	}
	relaxed := make([]PrisonerFacts, len(rows))
	limited := map[careOp]bool{}
	for i, row := range rows {
		relaxed[i] = row
		ops, ok := row.Operations.Value()
		if !ok {
			continue
		}
		copied := append([]SurgeryOperation(nil), ops...)
		for j, op := range copied {
			if positive(op.CareLimited) {
				copied[j].IngredientsOnMap = domain.Known(true)
				recipe, _ := op.Recipe.Value()
				part, _ := op.PartIndex.Value()
				limited[careOp{row.Pawn, recipe, part}] = true
			}
		}
		relaxed[i].Operations = domain.Known(copied)
	}
	if len(limited) == 0 {
		return OrganHarvest{}, false
	}
	facts := domain.Known(relaxed)
	h, ok := SelectOrganHarvest(facts, colony, needs, inFlight)
	if !ok {
		h, ok = SelectPartRecovery(facts, colony, parts, inFlight)
	}
	return h, ok && limited[careOp{h.Prisoner, h.Recipe, h.Part}]
}

type careOp struct {
	pawn   domain.PawnID
	recipe string
	part   int
}

// PrisonerHerbalNeeds adds PrisonerSurgeryHerbal herbal medicine to needs
// while a sale harvest or stock recovery (the ones that hold MaintainSurgery
// open) is blocked only by the prisoner care limit.
func PrisonerHerbalNeeds(needs map[Resource]int64, f RoutineFacts, silverShort domain.Fact[bool]) map[Resource]int64 {
	stock := map[Resource]int64{}
	rows, _ := f.Resources.Value()
	for _, row := range rows {
		stock[row.Resource] += row.Count
	}
	sale := OrganNeeds(domain.Unknown[[]CarePawn](), nil, positive(silverShort), stock)
	if _, ok := CareLimitedHarvest(f.Prisoners, f.PrisonerColony, sale, nil, nil); !ok {
		return needs
	}
	return ResourceGoalTargets(needs, map[Resource]int64{"MedicineHerbal": PrisonerSurgeryHerbal})
}
