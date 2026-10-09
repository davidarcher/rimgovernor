package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// QuestSpareColonists uses the roster's intended assignments, including changes
// that have not been applied yet. Sole primary owners of essential work stay home.
func QuestSpareColonists(workers domain.Fact[[]WorkPawn], mood domain.Fact[[]MoodPawn], roster WorkDecision) domain.Fact[[]PawnID] {
	spare, _, known := questRosterSplit(workers, mood, roster)
	if !known {
		return domain.Unknown[[]PawnID]()
	}
	return domain.Known(spare)
}

// QuestHomeFloor is the number of colonists who must stay home: the sole
// primary owners of doctor, cooking and construction. It follows roster
// coverage, so a colony with redundant owners may send more.
func QuestHomeFloor(workers domain.Fact[[]WorkPawn], mood domain.Fact[[]MoodPawn], roster WorkDecision) domain.Fact[int] {
	_, floor, known := questRosterSplit(workers, mood, roster)
	if !known {
		return domain.Unknown[int]()
	}
	return domain.Known(floor)
}

func questRosterSplit(workers domain.Fact[[]WorkPawn], mood domain.Fact[[]MoodPawn], roster WorkDecision) ([]PawnID, int, bool) {
	pawns, wk := workers.Value()
	states, mk := mood.Value()
	if _, known := roster.Capacity.Value(); !known || !wk || !mk {
		return nil, 0, false
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
			return nil, 0, false
		}
	}
	assignments := map[PawnID][]WorkPriority{}
	for _, row := range roster.Assignments {
		assignments[row.Pawn] = row.Priorities
	}
	spare, floor := []PawnID{}, 0
	for _, pawn := range pawns {
		available, known := pawn.Available.Value()
		if !known {
			return nil, 0, false
		}
		if !available {
			continue
		}
		state, found := status[pawn.ID]
		if !found || !allKnown(state.Dead, state.Downed, state.Drafted, state.Mental) {
			return nil, 0, false
		}
		if positive(state.Dead) || positive(state.Downed) || positive(state.Drafted) || positive(state.Mental) {
			continue
		}
		priorities, found := assignments[pawn.ID]
		if !found {
			return nil, 0, false
		}
		needed := false
		for _, setting := range priorities {
			owners, essentialWork := essential[setting.Work]
			if essentialWork && owners <= 1 && setting.Priority == 1 && !setting.Disabled {
				needed = true
			}
		}
		if needed {
			floor++
		} else {
			spare = append(spare, pawn.ID)
		}
	}
	slices.Sort(spare)
	return spare, floor, true
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
