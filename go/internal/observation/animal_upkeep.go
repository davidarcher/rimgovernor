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
		rows = append(rows, policy.UpkeepAnimal{ID: policy.PawnID(a.Pawn.Pawn.GetId()), Definition: policy.Resource(a.Pawn.Pawn.GetDefName()), RequiresPen: optional(a.RequiresPen), Contained: optional(state.Contained), Release: optional(state.Release), Slaughter: optional(state.Slaughter), Pen: domain.Known(state.GetPenId()), SuitablePen: domain.Known(a.GetSuitablePenId()), SafeToSlaughter: optional(state.SafeToSlaughter), Training: training})
	}
	return domain.Known(rows)
}
