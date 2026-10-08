package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// QuestMinimumColonistsAtHome preserves a doctor, cook and builder while a squad is away.
const QuestMinimumColonistsAtHome = 3

// QuestSpareColonists uses the roster's intended assignments, including changes
// that have not been applied yet. Sole primary owners of essential work stay home.
func QuestSpareColonists(workers domain.Fact[[]WorkPawn], mood domain.Fact[[]MoodPawn], roster WorkDecision) domain.Fact[[]PawnID] {
	pawns, wk := workers.Value()
	states, mk := mood.Value()
	if _, known := roster.Capacity.Value(); !known || !wk || !mk {
		return domain.Unknown[[]PawnID]()
	}
	status := map[PawnID]MoodPawn{}
	for _, pawn := range states {
		status[pawn.ID] = pawn
	}
	essential := map[WorkType]int{}
	for _, row := range roster.Coverage {
		essential[row.Work] = row.Owners
	}
	for _, work := range []WorkType{WorkDoctor, WorkCooking, WorkConstruction} {
		if _, known := essential[work]; !known {
			return domain.Unknown[[]PawnID]()
		}
	}
	assignments := map[PawnID][]WorkPriority{}
	for _, row := range roster.Assignments {
		assignments[row.Pawn] = row.Priorities
	}
	spare := []PawnID{}
	for _, pawn := range pawns {
		available, known := pawn.Available.Value()
		if !known {
			return domain.Unknown[[]PawnID]()
		}
		if !available {
			continue
		}
		state, found := status[pawn.ID]
		if !found || !allKnown(state.Dead, state.Downed, state.Drafted, state.Mental) {
			return domain.Unknown[[]PawnID]()
		}
		if positive(state.Dead) || positive(state.Downed) || positive(state.Drafted) || positive(state.Mental) {
			continue
		}
		priorities, found := assignments[pawn.ID]
		if !found {
			return domain.Unknown[[]PawnID]()
		}
		needed := false
		for _, setting := range priorities {
			owners, essentialWork := essential[setting.Work]
			if essentialWork && owners <= 1 && setting.Priority == 1 && !setting.Disabled {
				needed = true
			}
		}
		if !needed {
			spare = append(spare, pawn.ID)
		}
	}
	slices.Sort(spare)
	return domain.Known(spare)
}

// QuestCalmColony retains uncertainty from the existing emergency decision.
func QuestCalmColony(f RoundsFacts, emergency EmergencyDecision, podsPending bool) domain.Fact[bool] {
	if podsPending {
		return domain.Known(false)
	}
	unknown := false
	for _, hold := range emergency.Holds {
		if hold.Reason == EmergencyUnknownFacts || hold.Reason == EmergencyStaleFacts {
			unknown = true
		} else {
			return domain.Known(false)
		}
	}
	if unknown {
		return domain.Unknown[bool]()
	}
	if !emergency.Clear {
		return domain.Unknown[bool]()
	}
	return combatCleared(f)
}
