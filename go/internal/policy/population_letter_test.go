package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
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
	if _, known := JoinerLetterDeficit(rows, domain.Unknown[bool]()).Value(); known {
		t.Fatal("unknown capacity became known")
	}
	if deficit, known := JoinerLetterDeficit(domain.Known([]JoinerLetterOffer{}), domain.Unknown[bool]()).Value(); !known || deficit {
		t.Fatal("empty census is not a deficit")
	}
}

func TestPendingLetterRaisesPopulationNeedOnlyWithRoom(t *testing.T) {
	capacity := joinerFacts(t, 2, 3)
	facts := RoutineFacts{Custody: capacity.Custody, Sleeping: capacity.Sleeping, PopulationFoodDays: capacity.FoodDays, PopulationCapacity: capacity.Policy,
		QuestOffers: domain.Known([]JoinerOffer{}), JoinerLetters: domain.Known([]JoinerLetterOffer{{ID: 3, CanAccept: true}})}
	for _, maximum := range []int32{3, 2} {
		facts.PopulationCapacity = joinerPolicy(t, maximum, 3)
		needs, err := DetectRoutine(facts, RoutineLatches{}, DefaultRoutinePolicy())
		if err != nil {
			t.Fatal(err)
		}
		found := false
		for _, assessment := range needs.Assessments {
			if assessment.ID == MaintainPopulation {
				found = true
				if (assessment.Need == domain.NeedDeficit) != (maximum == 3) {
					t.Fatal(maximum, assessment)
				}
			}
		}
		if !found {
			t.Fatal("population need not assessed")
		}
	}
}
