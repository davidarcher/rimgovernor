package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// AppliedBuildingOpen reports an applied building intent whose blueprint or
// frame still stands (or whose census is unknown).
func AppliedBuildingOpen(p domain.Progress, census domain.Fact[CurrentConstruction]) bool {
	return p.Action().Kind() == domain.BuildingAction && p.Action().ConstructionTarget() == "" && p.View().Stage == domain.Completed && workOpen(p, census) == BuildingOpen
}

// PlanWorkOpen is GoalWorkOpen plus applied buildings still under
// construction.
func PlanWorkOpen(progress []domain.Progress, census domain.Fact[CurrentConstruction]) bool {
	if domain.StandardWorkOpen(progress) {
		return true
	}
	for _, p := range progress {
		if AppliedBuildingOpen(p, census) {
			return true
		}
	}
	return false
}

// PlanBuilt reports every action of the plan completed, with each building
// standing built in the census.
func PlanBuilt(progress []domain.Progress, census domain.Fact[CurrentConstruction]) bool {
	if len(progress) == 0 {
		return false
	}
	for _, p := range progress {
		v := p.View()
		effect, known := v.Effect.Value()
		if v.Stage != domain.Completed || !known || effect != domain.EffectCompleted {
			return false
		}
		if p.Action().Kind() == domain.BuildingAction && workOpen(p, census) != BuildingDone {
			return false
		}
	}
	return true
}

// workOpen is WorkOpen for a building progress row.
func workOpen(p domain.Progress, census domain.Fact[CurrentConstruction]) BuildingWork {
	building, _ := p.Action().Building()
	return WorkOpen(building, census)
}
