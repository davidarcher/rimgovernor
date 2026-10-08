package policy

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func plannedAnimal(id string, def Resource, training ...HusbandryTrainable) UpkeepAnimal {
	return UpkeepAnimal{ID: PawnID(id), Definition: def, Release: domain.Known(false), Slaughter: domain.Known(false), Training: training}
}

func TestSelectHusbandryMethodTrainsByJob(t *testing.T) {
	jobs := HerdPolicy{Roles: map[Resource]HerdRole{"Muffalo": {Job: HerdJobHaul}, "Warg": {Job: HerdJobWar}, "Cat": {Job: HerdJobCompanion}, "Boomrat": {Job: HerdJobNone}}}
	pick := func(herd HerdPolicy, a UpkeepAnimal) HusbandryChoice {
		return SelectHusbandryMethod(domain.Known([]UpkeepAnimal{a}), noWild, feedFine, herd, anyTamer)
	}
	all := func(learned ...string) []HusbandryTrainable {
		var out []HusbandryTrainable
		for _, def := range []string{"Haul", "Obedience", "Release", "Rescue"} {
			out = append(out, trainable(def, true, slices.Contains(learned, def)))
		}
		return out
	}
	if c := pick(jobs, plannedAnimal("m", "Muffalo", all()...)); c.TrainableDef != "Obedience" {
		t.Fatal(c)
	}
	if c := pick(jobs, plannedAnimal("m", "Muffalo", all("Obedience")...)); c.TrainableDef != "Haul" {
		t.Fatal("hauler trains Haul", c)
	}
	if c := pick(jobs, plannedAnimal("m", "Muffalo", all("Obedience", "Haul")...)); c.Reason != HusbandryNoDeficit {
		t.Fatal("hauler skips attack and rescue", c)
	}
	if c := pick(jobs, plannedAnimal("w", "Warg", all("Obedience")...)); c.TrainableDef != "Release" {
		t.Fatal(c)
	}
	if c := pick(jobs, plannedAnimal("w", "Warg", all("Obedience", "Release")...)); c.TrainableDef != "Rescue" {
		t.Fatal(c)
	}
	if c := pick(jobs, plannedAnimal("c", "Cat", all("Obedience")...)); c.Reason != HusbandryNoDeficit {
		t.Fatal("companion learns obedience only", c)
	}
	if c := pick(jobs, plannedAnimal("b", "Boomrat", all()...)); c.Reason != HusbandryNoDeficit {
		t.Fatal("no job trains nothing", c)
	}
	retired := HerdPolicy{Roles: map[Resource]HerdRole{"Muffalo": {Job: HerdJobHaul, Retiring: true}}}
	if c := pick(retired, plannedAnimal("m", "Muffalo", all()...)); c.Method == domain.HusbandryTrain {
		t.Fatal("retiring race trains nothing", c)
	}
}

// A decayed skill reads as unlearned again, so the job's training is
// requested anew.
func TestSelectHusbandryMethodRetrainsDecayedSkill(t *testing.T) {
	jobs := HerdPolicy{Roles: map[Resource]HerdRole{"Muffalo": {Job: HerdJobHaul}}}
	a := plannedAnimal("m", "Muffalo", trainable("Obedience", true, true), trainable("Haul", true, false))
	if c := SelectHusbandryMethod(domain.Known([]UpkeepAnimal{a}), noWild, feedFine, jobs, anyTamer); c.TrainableDef != "Haul" {
		t.Fatal(c)
	}
}

func TestSelectHusbandryMethodRemovalDesignatedAnimalGetsNoTraining(t *testing.T) {
	jobs := HerdPolicy{Roles: map[Resource]HerdRole{"Muffalo": {Job: HerdJobHaul}}}
	a := plannedAnimal("m", "Muffalo", trainable("Obedience", true, false))
	a.Slaughter = domain.Known(true)
	if c := SelectHusbandryMethod(domain.Known([]UpkeepAnimal{a}), noWild, feedFine, jobs, anyTamer); c.Method == domain.HusbandryTrain {
		t.Fatal(c)
	}
}

func TestAnimalHerdDeficitIgnoresUnwantedTrainables(t *testing.T) {
	jobs := HerdPolicy{Roles: map[Resource]HerdRole{"Muffalo": {Job: HerdJobHaul}}}
	a := plannedAnimal("m", "Muffalo", trainable("Obedience", true, true), trainable("Haul", true, true), trainable("Release", true, false))
	if v, known := AnimalHerdDeficit(domain.Known([]UpkeepAnimal{a}), noWild, feedFine, jobs).Value(); !known || v {
		t.Fatal(v, known)
	}
}

func TestRequestedTrainingWaitsForLearningWithoutAnotherWrite(t *testing.T) {
	herd := HerdPolicy{Roles: map[Resource]HerdRole{"Warg": {Job: HerdJobWar}}}
	a := plannedAnimal("warg", "Warg", trainable("Obedience", true, true), trainable("Release", true, false))
	a.Training[1].Wanted = domain.Known(true)
	animals := domain.Known([]UpkeepAnimal{a})
	for i := 0; i < 12; i++ {
		if choice := SelectHusbandryMethod(animals, noWild, feedFine, herd, anyTamer); choice.Reason != HusbandryNoDeficit {
			t.Fatal("requested training was reissued", choice)
		}
		if deficit, known := AnimalHerdDeficit(animals, noWild, feedFine, herd).Value(); !known || !deficit {
			t.Fatal("a request was mistaken for learning", deficit, known)
		}
		if !HerdWorkPending(animals, noWild, herd) {
			t.Fatal("pending training needs native work")
		}
	}
	// Other animals can still receive requests while this one's training waits.
	b := plannedAnimal("other", "Warg", trainable("Obedience", true, false))
	if choice := SelectHusbandryMethod(domain.Known([]UpkeepAnimal{a, b}), noWild, feedFine, herd, anyTamer); choice.Animal != b.ID || choice.Method != domain.HusbandryTrain {
		t.Fatal(choice)
	}
	// If the request is absent in a fresh read, it can be issued again.
	a.Training[1].Wanted = domain.Known(false)
	if choice := SelectHusbandryMethod(domain.Known([]UpkeepAnimal{a}), noWild, feedFine, herd, anyTamer); choice.TrainableDef != "Release" {
		t.Fatal(choice)
	}
	a.Training[1].Wanted = domain.Unknown[bool]()
	if choice := SelectHusbandryMethod(domain.Known([]UpkeepAnimal{a}), noWild, feedFine, herd, anyTamer); choice.Reason != HusbandryUnknown {
		t.Fatal("unknown request became absent", choice)
	}
	a.Training[1].Wanted, a.Training[1].Learned = domain.Known(true), domain.Known(true)
	if HerdWorkPending(domain.Known([]UpkeepAnimal{a}), noWild, herd) {
		t.Fatal("learned training is not pending")
	}
}

func TestHerdDesignationsWaitOnVanillaHandlers(t *testing.T) {
	for _, method := range []domain.HusbandryMethod{domain.HusbandrySlaughter, domain.HusbandryRelease, domain.HusbandryTame} {
		t.Run(string(method), func(t *testing.T) {
			a := plannedAnimal("animal", "Warg")
			wild := noWild
			animals := domain.Known([]UpkeepAnimal{a})
			switch method {
			case domain.HusbandrySlaughter:
				a.Slaughter = domain.Known(true)
				animals = domain.Known([]UpkeepAnimal{a})
			case domain.HusbandryRelease:
				a.Release = domain.Known(true)
				animals = domain.Known([]UpkeepAnimal{a})
			case domain.HusbandryTame:
				a.Tame, a.Tameable = domain.Known(true), domain.Known(true)
				wild = domain.Known([]UpkeepAnimal{a})
				animals = domain.Known([]UpkeepAnimal{})
			}
			for i := 0; i < 12; i++ {
				if !HerdWorkPending(animals, wild, HerdPolicy{}) {
					t.Fatal("native handlers need game time")
				}
				if choice := SelectHusbandryMethod(animals, wild, feedFine, HerdPolicy{}, anyTamer); choice.Method != "" {
					t.Fatal("standing designation selected another write", choice)
				}
			}
		})
	}
}
