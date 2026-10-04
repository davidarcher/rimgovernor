package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestJoinerLettersNeedKnownCapacityAndChooseOneOffer(t *testing.T) {
	rows := domain.Known([]JoinerLetterOffer{{ID: 9, CanAccept: true}, {ID: 2, CanAccept: true}, {ID: 1}})
	for _, capacity := range []domain.Fact[bool]{domain.Unknown[bool](), domain.Known(false)} {
		if _, ok := SelectJoinerLetter(rows, capacity); ok {
			t.Fatal("accepted without capacity")
		}
	}
	chosen, ok := SelectJoinerLetter(rows, domain.Known(true))
	if !ok || chosen.ID != 2 {
		t.Fatal(chosen, ok)
	}
	// A creepjoiner offer (#1740, David: accept them) takes the same path.
	creep := domain.Known([]JoinerLetterOffer{{ID: 4, CanAccept: true, CreepJoiner: true}})
	if chosen, ok := SelectJoinerLetter(creep, domain.Known(true)); !ok || !chosen.CreepJoiner {
		t.Fatal("creepjoiner letter not chosen", chosen, ok)
	}
	if _, known := JoinerLetterDeficit(rows, domain.Unknown[bool]()).Value(); known {
		t.Fatal("unknown capacity became known")
	}
	if deficit, known := JoinerLetterDeficit(domain.Known([]JoinerLetterOffer{}), domain.Unknown[bool]()).Value(); !known || deficit {
		t.Fatal("empty census is not a deficit")
	}
}

func TestPendingLetterRaisesPopulationNeedOnlyWithRoom(t *testing.T) {
	capacity := joinerFacts(t, 2)
	facts := RoundsFacts{Custody: capacity.Custody, Sleeping: capacity.Sleeping, FoodDays: capacity.FoodDays,
		QuestOffers: domain.Known([]JoinerOffer{}), JoinerLetters: domain.Known([]JoinerLetterOffer{{ID: 3, CanAccept: true}})}
	for _, food := range []float64{20, 1} {
		facts.FoodDays = domain.Known(food)
		needs, err := InspectRounds(facts, RoundsLatches{}, DefaultRoundsPolicy())
		if err != nil {
			t.Fatal(err)
		}
		found := false
		for _, assessment := range needs.Assessments {
			if assessment.ID == MaintainPopulation {
				found = true
				if (assessment.Finding == domain.FindingUnmet) != (food == 20) {
					t.Fatal(food, assessment)
				}
			}
		}
		if !found {
			t.Fatal("population need not assessed")
		}
	}
}
