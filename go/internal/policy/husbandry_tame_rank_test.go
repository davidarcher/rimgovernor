package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestSelectHusbandryMethodTamesTheWantedRaceBeforeLowestID(t *testing.T) {
	wild := domain.Known([]UpkeepAnimal{wildAnimal("a-1", "Boomrat", true, false), wildAnimal("z-9", "Cow", true, false)})
	herd := HerdPolicy{PopulationMin: map[Resource]int64{"Boomrat": 1, "Cow": 1},
		Roles: map[Resource]HerdRole{"Cow": {Race: "Cow", Job: HerdJobMilk, Preferred: true}, "Boomrat": {Race: "Boomrat", Job: HerdJobNone}}}
	if choice := SelectHusbandryMethod(domain.Known([]UpkeepAnimal{}), wild, feedFine, herd, anyTamer); choice.Method != domain.HusbandryTame || choice.Animal != "z-9" {
		t.Fatal("the race the plan wants beats the lowest ID", choice)
	}
}

func TestSelectHusbandryMethodTamesTheMissingSexFirst(t *testing.T) {
	cow := planAnimal("cow-1", "Cow", "Female")
	bull, heifer := wildAnimal("wild-2", "Cow", true, false), wildAnimal("wild-1", "Cow", true, false)
	bull.Gender, heifer.Gender = "Male", "Female"
	wild := domain.Known([]UpkeepAnimal{heifer, bull})
	herd := HerdPolicy{PopulationMin: map[Resource]int64{"Cow": 2},
		Roles: map[Resource]HerdRole{"Cow": {Race: "Cow", Job: HerdJobMilk, Preferred: true, Founder: true, WantMale: true}}}
	choice := SelectHusbandryMethod(domain.Known([]UpkeepAnimal{cow}), wild, feedFine, herd, anyTamer)
	if choice.Method != domain.HusbandryTame || choice.Animal != "wild-2" {
		t.Fatal("a lone cow's missing sex is tamed first", choice)
	}
}

func TestHerdTameLessPrefersSafeAndEasy(t *testing.T) {
	risky, safe := wildAnimal("a", "Cow", true, false), wildAnimal("b", "Cow", true, false)
	risky.Herd.ManhunterOnTameFail, safe.Herd.ManhunterOnTameFail = domain.Known(0.1), domain.Known(0.0)
	if !herdTameLess(HerdRole{}, safe, risky) || herdTameLess(HerdRole{}, risky, safe) {
		t.Fatal("lower tame-fail risk first")
	}
}

func TestSelectHusbandryMethodStillExcludesDangerousRaces(t *testing.T) {
	wolf := wildAnimal("wolf-1", "Wolf", true, false)
	wolf.Herd.Predator = true
	herd := HerdPolicy{PopulationMin: map[Resource]int64{"Wolf": 1}, Roles: map[Resource]HerdRole{"Wolf": {Race: "Wolf", Preferred: true, Job: HerdJobWar}}}
	if choice := SelectHusbandryMethod(domain.Known([]UpkeepAnimal{}), domain.Known([]UpkeepAnimal{wolf}), feedFine, herd, anyTamer); choice.Reason != HusbandryNoDeficit {
		t.Fatal("a predator is never tamed", choice)
	}
}
