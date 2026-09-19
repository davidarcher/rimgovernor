package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func colonyAnimals(v *o.ColonyFactsSnapshot) domain.Fact[[]policy.UpkeepAnimal] {
	u := v.GetUpkeep().GetObserved()
	if u == nil || hasIssue(u.Issues, "animals") {
		return domain.Unknown[[]policy.UpkeepAnimal]()
	}
	rows := []policy.UpkeepAnimal{}
	for _, a := range u.Animals {
		state := a.Pawn.AnimalState
		training := make([]policy.HusbandryTrainable, 0, len(state.GetTraining()))
		for _, entry := range state.GetTraining() {
			training = append(training, policy.HusbandryTrainable{Def: entry.GetDefName(), Available: optional(entry.Available), Learned: optional(entry.Learned)})
		}
		rows = append(rows, policy.UpkeepAnimal{ID: policy.PawnID(a.Pawn.Pawn.GetId()), Definition: policy.Resource(a.Pawn.Pawn.GetDefName()), RequiresPen: optional(a.RequiresPen), Contained: optional(state.Contained), Release: optional(state.Release), Slaughter: optional(state.Slaughter), Pen: domain.Known(state.GetPenId()), SuitablePen: domain.Known(a.GetSuitablePenId()), SafeToSlaughter: optional(state.SafeToSlaughter), SafeToRelease: optional(state.SafeToRelease), Training: training, ReachableBenches: append([]string{}, a.ReachableBenchIds...)})
	}
	return domain.Known(rows)
}

// colonyWildAnimals decodes the factionless census MaintainHerd tames from.
// A native section issue (bound exceeded, read failed) leaves it unknown.
func colonyWildAnimals(v *o.ColonyFactsSnapshot) domain.Fact[[]policy.UpkeepAnimal] {
	u := v.GetUpkeep().GetObserved()
	if u == nil || hasIssue(u.Issues, "wild_animals") {
		return domain.Unknown[[]policy.UpkeepAnimal]()
	}
	rows := []policy.UpkeepAnimal{}
	for _, a := range u.WildAnimals {
		state := a.Pawn.AnimalState
		rows = append(rows, policy.UpkeepAnimal{ID: policy.PawnID(a.Pawn.Pawn.GetId()), Definition: policy.Resource(a.Pawn.Pawn.GetDefName()), RequiresPen: domain.Known(false), Contained: domain.Known(false), Release: domain.Known(false), Slaughter: domain.Known(false), Pen: domain.Known(""), SuitablePen: domain.Known(""), Tameable: optional(state.Tameable), Tame: optional(state.Tame)})
	}
	return domain.Known(rows)
}
