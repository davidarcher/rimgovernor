package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func TestQuestFeasibilityCostClassesAndOpenCommitments(t *testing.T) {
	makeOffer := func(cost QuestCost) JoinerOffer {
		return JoinerOffer{Quest: "q", State: "NotYetAccepted", Profile: domain.Known(QuestProfile{Family: QuestFamilyPawnLend, Cost: cost, Disposition: QuestDecide})}
	}
	for name, c := range map[string]struct {
		cost QuestCost
		edit func(*RoundsFacts)
		want QuestSkipReason
	}{
		"free":             {cost: QuestCostFree},
		"pawns affordable": {cost: QuestCostPawns},
		"time affordable":  {cost: QuestCostTime},
		"trap":             {cost: QuestCostTrap, want: "automatic_trap"},
		"no spare":         {cost: QuestCostPawns, edit: func(f *RoundsFacts) { f.QuestSparePawns = domain.Known([]PawnID{}) }, want: "no_spare_pawn"},
		"busy":             {cost: QuestCostTime, edit: func(f *RoundsFacts) { f.QuestColonyCalm = domain.Known(false) }, want: "colony_busy"},
		"home floor":       {cost: QuestCostPawns, edit: func(f *RoundsFacts) { f.QuestColonistsAtHome = domain.Known(3) }, want: "home_capacity"},
		"open commitment": {cost: QuestCostPawns, edit: func(f *RoundsFacts) {
			open := makeOffer(QuestCostPawns)
			open.State = "Ongoing"
			f.QuestOffers = domain.Known([]JoinerOffer{open})
		}, want: "no_spare_pawn"},
		"unknown": {cost: QuestCostPawns, edit: func(f *RoundsFacts) { f.QuestColonyCalm = domain.Unknown[bool]() }, want: "capacity_unknown"},
	} {
		t.Run(name, func(t *testing.T) {
			f := questTestFacts(domain.Known([]JoinerOffer{}))
			if c.edit != nil {
				c.edit(&f)
			}
			if got := QuestFeasibility(makeOffer(c.cost), f); got != c.want {
				t.Fatalf("%s, want %s", got, c.want)
			}
		})
	}
	free := makeOffer(QuestCostFree)
	free.Objectives = []QuestObjective{{Kind: o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_ACCEPT_REQUIREMENT_UNMET}}
	if got := QuestFeasibility(free, RoundsFacts{}); got != "accept_requirement" {
		t.Fatal(got)
	}
}

func TestUniversalFavorDoesNotBecomeChoiceIndex(t *testing.T) {
	offer := empireOffer("q", func(o *JoinerOffer) { o.Favor = []QuestFavor{{Choice: -1, Favor: 20}, {Choice: 0, Favor: 2}} })
	if choice := SelectQuestMethod(questTestFacts(domain.Known([]JoinerOffer{offer}))); choice.RewardChoice != 0 {
		t.Fatal(choice)
	}
}
