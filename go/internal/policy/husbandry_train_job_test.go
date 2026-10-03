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
