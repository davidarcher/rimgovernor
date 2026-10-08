package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
)

func TestQuestAccepter(t *testing.T) {
	offer := JoinerOffer{RequiresAccepter: true, EligiblePawnIDs: []domain.PawnID{"builder", "spare"}}
	workers := domain.Known([]WorkPawn{{ID: "builder", Skills: domain.Known([]WorkSkill{{Name: "Construction", Level: 15}})}, {ID: "spare", Skills: domain.Known([]WorkSkill{{Name: "Construction", Level: 2}})}})
	if id, ok := QuestAccepter(offer, RoundsFacts{}, workers); !ok || id != "spare" {
		t.Fatalf("accepter=%s,%v", id, ok)
	}
	offer.Favor = []QuestFavor{{Choice: 0, Favor: 4}}
	facts := RoundsFacts{Royalty: domain.Known(RoyaltyFacts{Holders: map[PawnID][]RoyalHolding{"builder": {{Title: "Yeoman"}}}})}
	if id, ok := QuestAccepter(offer, facts, workers); !ok || id != "builder" {
		t.Fatalf("royal accepter=%s,%v", id, ok)
	}
	offer.EligiblePawnIDs = nil
	if _, ok := QuestAccepter(offer, facts, workers); ok {
		t.Fatal("unsupported title requirement supplied an accepter")
	}
	offer.EligiblePawnIDs = []domain.PawnID{"missing"}
	if _, ok := QuestAccepter(offer, facts, workers); ok {
		t.Fatal("unobserved pawn supplied an accepter")
	}
	if _, ok := QuestAccepter(offer, facts, domain.Unknown[[]WorkPawn]()); ok {
		t.Fatal("unknown roster supplied an accepter")
	}
}
