package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func trainable(def string, available, learned bool) HusbandryTrainable {
	return HusbandryTrainable{Def: def, Available: domain.Known(available), Learned: domain.Known(learned)}
}

func TestAnimalHerdDeficitUnknownCensus(t *testing.T) {
	if v, known := AnimalHerdDeficit(domain.Unknown[[]UpkeepAnimal]()).Value(); known || v {
		t.Fatal("unknown census must not be treated as recovered")
	}
}

func TestAnimalHerdDeficitEmptyHerdRecovered(t *testing.T) {
	deficit := AnimalHerdDeficit(domain.Known([]UpkeepAnimal{}))
	if v, known := deficit.Value(); !known || v {
		t.Fatal("empty herd must be recovered", deficit)
	}
}

func TestAnimalHerdDeficitDetectsUntrainedAvailableTrainable(t *testing.T) {
	animals := domain.Known([]UpkeepAnimal{
		{ID: "muffalo-1", Release: domain.Known(false), Slaughter: domain.Known(false), Training: []HusbandryTrainable{trainable("Obedience", true, false)}},
	})
	if v, known := AnimalHerdDeficit(animals).Value(); !known || !v {
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
	if v, known := AnimalHerdDeficit(animals).Value(); !known || v {
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
		if _, known := AnimalHerdDeficit(domain.Known([]UpkeepAnimal{animal})).Value(); known {
			t.Fatalf("case %d: incomplete facts must stay unknown", i)
		}
	}
}

func TestSelectHusbandryMethodUnknownCensus(t *testing.T) {
	choice := SelectHusbandryMethod(domain.Unknown[[]UpkeepAnimal]())
	if choice.Reason != HusbandryUnknown {
		t.Fatal(choice)
	}
}

func TestSelectHusbandryMethodNoDeficit(t *testing.T) {
	choice := SelectHusbandryMethod(domain.Known([]UpkeepAnimal{
		{ID: "a", Release: domain.Known(false), Slaughter: domain.Known(false), Training: []HusbandryTrainable{trainable("Obedience", true, true)}},
	}))
	if choice.Reason != HusbandryNoDeficit {
		t.Fatal(choice)
	}
}

func TestSelectHusbandryMethodPicksLowestAnimalThenTrainable(t *testing.T) {
	animals := domain.Known([]UpkeepAnimal{
		{ID: "muffalo-2", Release: domain.Known(false), Slaughter: domain.Known(false), Training: []HusbandryTrainable{trainable("Release", true, false), trainable("Obedience", true, false)}},
		{ID: "muffalo-1", Release: domain.Known(false), Slaughter: domain.Known(false), Training: []HusbandryTrainable{trainable("Obedience", true, false)}},
	})
	choice := SelectHusbandryMethod(animals)
	if choice.Reason != "" || choice.Animal != "muffalo-1" || choice.TrainableDef != "Obedience" {
		t.Fatal(choice)
	}
}

func TestSelectHusbandryMethodSkipsReleasedAndSlaughteredAnimals(t *testing.T) {
	animals := domain.Known([]UpkeepAnimal{
		{ID: "muffalo-1", Release: domain.Known(true), Slaughter: domain.Known(false), Training: []HusbandryTrainable{trainable("Obedience", true, false)}},
		{ID: "muffalo-2", Release: domain.Known(false), Slaughter: domain.Known(true), Training: []HusbandryTrainable{trainable("Obedience", true, false)}},
		{ID: "muffalo-3", Release: domain.Known(false), Slaughter: domain.Known(false), Training: []HusbandryTrainable{trainable("Obedience", true, false)}},
	})
	choice := SelectHusbandryMethod(animals)
	if choice.Animal != "muffalo-3" {
		t.Fatal(choice)
	}
}
