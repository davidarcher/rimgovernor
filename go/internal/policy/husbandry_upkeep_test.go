package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func trainable(def string, available, learned bool) HusbandryTrainable {
	return HusbandryTrainable{Def: def, Available: domain.Known(available), Learned: domain.Known(learned)}
}

func TestAnimalHerdDeficitUnknownCensus(t *testing.T) {
	if v, known := AnimalHerdDeficit(domain.Unknown[[]UpkeepAnimal](), false, nil).Value(); known || v {
		t.Fatal("unknown census must not be treated as recovered")
	}
}

func TestAnimalHerdDeficitEmptyHerdRecovered(t *testing.T) {
	deficit := AnimalHerdDeficit(domain.Known([]UpkeepAnimal{}), false, nil)
	if v, known := deficit.Value(); !known || v {
		t.Fatal("empty herd must be recovered", deficit)
	}
}

func TestAnimalHerdDeficitDetectsUntrainedAvailableTrainable(t *testing.T) {
	animals := domain.Known([]UpkeepAnimal{
		{ID: "muffalo-1", Release: domain.Known(false), Slaughter: domain.Known(false), Training: []HusbandryTrainable{trainable("Obedience", true, false)}},
	})
	if v, known := AnimalHerdDeficit(animals, false, nil).Value(); !known || !v {
		t.Fatal("untrained available trainable must be a deficit")
	}
}

func TestAnimalHerdDeficitIgnoresLearnedUnavailableReleasedOrSlaughtered(t *testing.T) {
	animals := domain.Known([]UpkeepAnimal{
		{ID: "learned", Release: domain.Known(false), Slaughter: domain.Known(false), Training: []HusbandryTrainable{trainable("Obedience", true, true)}},
		{ID: "unavailable", Release: domain.Known(false), Slaughter: domain.Known(false), Training: []HusbandryTrainable{trainable("Obedience", false, false)}},
		{ID: "released", Release: domain.Known(true), Slaughter: domain.Known(false), Training: []HusbandryTrainable{trainable("Obedience", true, false)}},
		{ID: "slaughter-marked", Release: domain.Known(false), Slaughter: domain.Known(true), Training: []HusbandryTrainable{trainable("Obedience", true, false)}},
	})
	if v, known := AnimalHerdDeficit(animals, false, nil).Value(); !known || v {
		t.Fatal("no animal should register a deficit", v, known)
	}
}

func TestAnimalHerdDeficitUnknownFactsStayUnknown(t *testing.T) {
	cases := []UpkeepAnimal{
		{ID: "a", Release: domain.Unknown[bool](), Slaughter: domain.Known(false)},
		{ID: "a", Release: domain.Known(false), Slaughter: domain.Unknown[bool]()},
		{ID: "a", Release: domain.Known(false), Slaughter: domain.Known(false), Training: []HusbandryTrainable{{Def: "x", Available: domain.Unknown[bool](), Learned: domain.Known(false)}}},
		{ID: "a", Release: domain.Known(false), Slaughter: domain.Known(false), Training: []HusbandryTrainable{{Def: "x", Available: domain.Known(true), Learned: domain.Unknown[bool]()}}},
	}
	for i, animal := range cases {
		if _, known := AnimalHerdDeficit(domain.Known([]UpkeepAnimal{animal}), false, nil).Value(); known {
			t.Fatalf("case %d: incomplete facts must stay unknown", i)
		}
	}
}

func TestAnimalHerdDeficitSlaughterDisabledByDefaultIgnoresSurplus(t *testing.T) {
	animals := domain.Known([]UpkeepAnimal{
		{ID: "muffalo-1", Definition: "Muffalo", Release: domain.Known(false), Slaughter: domain.Known(false), SafeToSlaughter: domain.Known(true)},
		{ID: "muffalo-2", Definition: "Muffalo", Release: domain.Known(false), Slaughter: domain.Known(false), SafeToSlaughter: domain.Known(true)},
	})
	populationMax := map[Resource]int64{"Muffalo": 1}
	if v, known := AnimalHerdDeficit(animals, false, populationMax).Value(); !known || v {
		t.Fatal("AllowSlaughter=false must never register a surplus deficit", v, known)
	}
}

func TestAnimalHerdDeficitDetectsSurplusOnlyWhenOptedIn(t *testing.T) {
	animals := domain.Known([]UpkeepAnimal{
		{ID: "muffalo-1", Definition: "Muffalo", Release: domain.Known(false), Slaughter: domain.Known(false), SafeToSlaughter: domain.Known(true)},
		{ID: "muffalo-2", Definition: "Muffalo", Release: domain.Known(false), Slaughter: domain.Known(false), SafeToSlaughter: domain.Known(true)},
	})
	populationMax := map[Resource]int64{"Muffalo": 1}
	if v, known := AnimalHerdDeficit(animals, true, populationMax).Value(); !known || !v {
		t.Fatal("surplus over the configured maximum must be a deficit once opted in", v, known)
	}
	if v, known := AnimalHerdDeficit(animals, true, nil).Value(); !known || v {
		t.Fatal("no configured population max must never register a surplus deficit", v, known)
	}
}

func TestAnimalHerdDeficitSurplusIgnoresUntrackedRace(t *testing.T) {
	animals := domain.Known([]UpkeepAnimal{
		{ID: "alpaca-1", Definition: "Alpaca", Release: domain.Known(false), Slaughter: domain.Known(false), SafeToSlaughter: domain.Unknown[bool]()},
	})
	populationMax := map[Resource]int64{"Muffalo": 1}
	if v, known := AnimalHerdDeficit(animals, true, populationMax).Value(); !known || v {
		t.Fatal("an untracked race's unknown facts must not block recovery", v, known)
	}
}

func TestAnimalHerdDeficitSurplusUnknownSafetyStaysUnknown(t *testing.T) {
	animals := domain.Known([]UpkeepAnimal{
		{ID: "muffalo-1", Definition: "Muffalo", Release: domain.Known(false), Slaughter: domain.Known(false), SafeToSlaughter: domain.Known(true)},
		{ID: "muffalo-2", Definition: "Muffalo", Release: domain.Known(false), Slaughter: domain.Known(false), SafeToSlaughter: domain.Unknown[bool]()},
	})
	populationMax := map[Resource]int64{"Muffalo": 1}
	if _, known := AnimalHerdDeficit(animals, true, populationMax).Value(); known {
		t.Fatal("an unknown safe-to-slaughter fact on a surplus-relevant animal must stay unknown")
	}
}

func TestSelectHusbandryMethodUnknownCensus(t *testing.T) {
	choice := SelectHusbandryMethod(domain.Unknown[[]UpkeepAnimal](), false, nil)
	if choice.Reason != HusbandryUnknown {
		t.Fatal(choice)
	}
}

func TestSelectHusbandryMethodNoDeficit(t *testing.T) {
	choice := SelectHusbandryMethod(domain.Known([]UpkeepAnimal{
		{ID: "a", Release: domain.Known(false), Slaughter: domain.Known(false), Training: []HusbandryTrainable{trainable("Obedience", true, true)}},
	}), false, nil)
	if choice.Reason != HusbandryNoDeficit {
		t.Fatal(choice)
	}
}

func TestSelectHusbandryMethodPicksLowestAnimalThenTrainable(t *testing.T) {
	animals := domain.Known([]UpkeepAnimal{
		{ID: "muffalo-2", Release: domain.Known(false), Slaughter: domain.Known(false), Training: []HusbandryTrainable{trainable("Release", true, false), trainable("Obedience", true, false)}},
		{ID: "muffalo-1", Release: domain.Known(false), Slaughter: domain.Known(false), Training: []HusbandryTrainable{trainable("Obedience", true, false)}},
	})
	choice := SelectHusbandryMethod(animals, false, nil)
	if choice.Reason != "" || choice.Animal != "muffalo-1" || choice.Method != domain.HusbandryTrain || choice.TrainableDef != "Obedience" {
		t.Fatal(choice)
	}
}

func TestSelectHusbandryMethodSkipsReleasedAndSlaughteredAnimals(t *testing.T) {
	animals := domain.Known([]UpkeepAnimal{
		{ID: "muffalo-1", Release: domain.Known(true), Slaughter: domain.Known(false), Training: []HusbandryTrainable{trainable("Obedience", true, false)}},
		{ID: "muffalo-2", Release: domain.Known(false), Slaughter: domain.Known(true), Training: []HusbandryTrainable{trainable("Obedience", true, false)}},
		{ID: "muffalo-3", Release: domain.Known(false), Slaughter: domain.Known(false), Training: []HusbandryTrainable{trainable("Obedience", true, false)}},
	})
	choice := SelectHusbandryMethod(animals, false, nil)
	if choice.Animal != "muffalo-3" {
		t.Fatal(choice)
	}
}

func TestSelectHusbandryMethodNeverProposesSlaughterWithoutOptIn(t *testing.T) {
	animals := domain.Known([]UpkeepAnimal{
		{ID: "muffalo-1", Definition: "Muffalo", Release: domain.Known(false), Slaughter: domain.Known(false), SafeToSlaughter: domain.Known(true)},
		{ID: "muffalo-2", Definition: "Muffalo", Release: domain.Known(false), Slaughter: domain.Known(false), SafeToSlaughter: domain.Known(true)},
	})
	populationMax := map[Resource]int64{"Muffalo": 1}
	if choice := SelectHusbandryMethod(animals, false, populationMax); choice.Reason != HusbandryNoDeficit {
		t.Fatal("AllowSlaughter=false must never propose a slaughter write", choice)
	}
	if choice := SelectHusbandryMethod(animals, true, nil); choice.Reason != HusbandryNoDeficit {
		t.Fatal("an empty HerdPopulationMax must never propose a slaughter write", choice)
	}
}

func TestSelectHusbandryMethodPrefersTrainingOverSlaughter(t *testing.T) {
	animals := domain.Known([]UpkeepAnimal{
		{ID: "muffalo-1", Definition: "Muffalo", Release: domain.Known(false), Slaughter: domain.Known(false), SafeToSlaughter: domain.Known(true), Training: []HusbandryTrainable{trainable("Obedience", true, false)}},
		{ID: "muffalo-2", Definition: "Muffalo", Release: domain.Known(false), Slaughter: domain.Known(false), SafeToSlaughter: domain.Known(true)},
	})
	populationMax := map[Resource]int64{"Muffalo": 1}
	choice := SelectHusbandryMethod(animals, true, populationMax)
	if choice.Method != domain.HusbandryTrain || choice.Animal != "muffalo-1" {
		t.Fatal("a training deficit must still be preferred over a slaughter surplus", choice)
	}
}

func TestSelectHusbandryMethodProposesSlaughterForSurplus(t *testing.T) {
	animals := domain.Known([]UpkeepAnimal{
		{ID: "muffalo-2", Definition: "Muffalo", Release: domain.Known(false), Slaughter: domain.Known(false), SafeToSlaughter: domain.Known(true)},
		{ID: "muffalo-1", Definition: "Muffalo", Release: domain.Known(false), Slaughter: domain.Known(false), SafeToSlaughter: domain.Known(true)},
	})
	populationMax := map[Resource]int64{"Muffalo": 1}
	choice := SelectHusbandryMethod(animals, true, populationMax)
	if choice.Method != domain.HusbandrySlaughter || choice.Animal != "muffalo-1" || choice.TrainableDef != "" {
		t.Fatal("the lowest-ID surplus candidate must be proposed for slaughter", choice)
	}
}

func TestSelectHusbandryMethodSlaughterSkipsUnsafeAndAlreadyDesignated(t *testing.T) {
	animals := domain.Known([]UpkeepAnimal{
		{ID: "muffalo-1", Definition: "Muffalo", Release: domain.Known(false), Slaughter: domain.Known(false), SafeToSlaughter: domain.Known(false)},
		{ID: "muffalo-2", Definition: "Muffalo", Release: domain.Known(false), Slaughter: domain.Known(true), SafeToSlaughter: domain.Known(true)},
		{ID: "muffalo-3", Definition: "Muffalo", Release: domain.Known(false), Slaughter: domain.Known(false), SafeToSlaughter: domain.Known(true)},
	})
	populationMax := map[Resource]int64{"Muffalo": 1}
	choice := SelectHusbandryMethod(animals, true, populationMax)
	if choice.Method != domain.HusbandrySlaughter || choice.Animal != "muffalo-3" {
		t.Fatal("an unsafe or already-designated animal must never be re-proposed", choice)
	}
}
