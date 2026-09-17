package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func trainable(def string, available, learned bool) HusbandryTrainable {
	return HusbandryTrainable{Def: def, Available: domain.Known(available), Learned: domain.Known(learned)}
}

var noWild = domain.Known([]UpkeepAnimal{})

func herd(allowSlaughter bool, populationMax map[Resource]int64) HerdPolicy {
	return HerdPolicy{AllowSlaughter: allowSlaughter, PopulationMax: populationMax}
}

func wildAnimal(id string, def Resource, tameable, designated bool) UpkeepAnimal {
	return UpkeepAnimal{ID: PawnID(id), Definition: def, Release: domain.Known(false), Slaughter: domain.Known(false), Tameable: domain.Known(tameable), Tame: domain.Known(designated)}
}

func TestAnimalHerdDeficitUnknownCensus(t *testing.T) {
	if v, known := AnimalHerdDeficit(domain.Unknown[[]UpkeepAnimal](), noWild, herd(false, nil)).Value(); known || v {
		t.Fatal("unknown census must not be treated as recovered")
	}
}

func TestAnimalHerdDeficitEmptyHerdRecovered(t *testing.T) {
	deficit := AnimalHerdDeficit(domain.Known([]UpkeepAnimal{}), noWild, herd(false, nil))
	if v, known := deficit.Value(); !known || v {
		t.Fatal("empty herd must be recovered", deficit)
	}
}

func TestAnimalHerdDeficitDetectsUntrainedAvailableTrainable(t *testing.T) {
	animals := domain.Known([]UpkeepAnimal{
		{ID: "muffalo-1", Release: domain.Known(false), Slaughter: domain.Known(false), Training: []HusbandryTrainable{trainable("Obedience", true, false)}},
	})
	if v, known := AnimalHerdDeficit(animals, noWild, herd(false, nil)).Value(); !known || !v {
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
	if v, known := AnimalHerdDeficit(animals, noWild, herd(false, nil)).Value(); !known || v {
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
		if _, known := AnimalHerdDeficit(domain.Known([]UpkeepAnimal{animal}), noWild, herd(false, nil)).Value(); known {
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
	if v, known := AnimalHerdDeficit(animals, noWild, herd(false, populationMax)).Value(); !known || v {
		t.Fatal("AllowSlaughter=false must never register a surplus deficit", v, known)
	}
}

func TestAnimalHerdDeficitDetectsSurplusOnlyWhenOptedIn(t *testing.T) {
	animals := domain.Known([]UpkeepAnimal{
		{ID: "muffalo-1", Definition: "Muffalo", Release: domain.Known(false), Slaughter: domain.Known(false), SafeToSlaughter: domain.Known(true)},
		{ID: "muffalo-2", Definition: "Muffalo", Release: domain.Known(false), Slaughter: domain.Known(false), SafeToSlaughter: domain.Known(true)},
	})
	populationMax := map[Resource]int64{"Muffalo": 1}
	if v, known := AnimalHerdDeficit(animals, noWild, herd(true, populationMax)).Value(); !known || !v {
		t.Fatal("surplus over the configured maximum must be a deficit once opted in", v, known)
	}
	if v, known := AnimalHerdDeficit(animals, noWild, herd(true, nil)).Value(); !known || v {
		t.Fatal("no configured population max must never register a surplus deficit", v, known)
	}
}

func TestAnimalHerdDeficitSurplusIgnoresUntrackedRace(t *testing.T) {
	animals := domain.Known([]UpkeepAnimal{
		{ID: "alpaca-1", Definition: "Alpaca", Release: domain.Known(false), Slaughter: domain.Known(false), SafeToSlaughter: domain.Unknown[bool]()},
	})
	populationMax := map[Resource]int64{"Muffalo": 1}
	if v, known := AnimalHerdDeficit(animals, noWild, herd(true, populationMax)).Value(); !known || v {
		t.Fatal("an untracked race's unknown facts must not block recovery", v, known)
	}
}

func TestAnimalHerdDeficitSurplusUnknownSafetyStaysUnknown(t *testing.T) {
	animals := domain.Known([]UpkeepAnimal{
		{ID: "muffalo-1", Definition: "Muffalo", Release: domain.Known(false), Slaughter: domain.Known(false), SafeToSlaughter: domain.Known(true)},
		{ID: "muffalo-2", Definition: "Muffalo", Release: domain.Known(false), Slaughter: domain.Known(false), SafeToSlaughter: domain.Unknown[bool]()},
	})
	populationMax := map[Resource]int64{"Muffalo": 1}
	if _, known := AnimalHerdDeficit(animals, noWild, herd(true, populationMax)).Value(); known {
		t.Fatal("an unknown safe-to-slaughter fact on a surplus-relevant animal must stay unknown")
	}
}

func TestSelectHusbandryMethodUnknownCensus(t *testing.T) {
	choice := SelectHusbandryMethod(domain.Unknown[[]UpkeepAnimal](), noWild, herd(false, nil))
	if choice.Reason != HusbandryUnknown {
		t.Fatal(choice)
	}
}

func TestSelectHusbandryMethodNoDeficit(t *testing.T) {
	choice := SelectHusbandryMethod(domain.Known([]UpkeepAnimal{
		{ID: "a", Release: domain.Known(false), Slaughter: domain.Known(false), Training: []HusbandryTrainable{trainable("Obedience", true, true)}},
	}), noWild, herd(false, nil))
	if choice.Reason != HusbandryNoDeficit {
		t.Fatal(choice)
	}
}

func TestSelectHusbandryMethodPicksLowestAnimalThenTrainable(t *testing.T) {
	animals := domain.Known([]UpkeepAnimal{
		{ID: "muffalo-2", Release: domain.Known(false), Slaughter: domain.Known(false), Training: []HusbandryTrainable{trainable("Release", true, false), trainable("Obedience", true, false)}},
		{ID: "muffalo-1", Release: domain.Known(false), Slaughter: domain.Known(false), Training: []HusbandryTrainable{trainable("Obedience", true, false)}},
	})
	choice := SelectHusbandryMethod(animals, noWild, herd(false, nil))
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
	choice := SelectHusbandryMethod(animals, noWild, herd(false, nil))
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
	if choice := SelectHusbandryMethod(animals, noWild, herd(false, populationMax)); choice.Reason != HusbandryNoDeficit {
		t.Fatal("AllowSlaughter=false must never propose a slaughter write", choice)
	}
	if choice := SelectHusbandryMethod(animals, noWild, herd(true, nil)); choice.Reason != HusbandryNoDeficit {
		t.Fatal("an empty HerdPopulationMax must never propose a slaughter write", choice)
	}
}

func TestSelectHusbandryMethodPrefersTrainingOverSlaughter(t *testing.T) {
	animals := domain.Known([]UpkeepAnimal{
		{ID: "muffalo-1", Definition: "Muffalo", Release: domain.Known(false), Slaughter: domain.Known(false), SafeToSlaughter: domain.Known(true), Training: []HusbandryTrainable{trainable("Obedience", true, false)}},
		{ID: "muffalo-2", Definition: "Muffalo", Release: domain.Known(false), Slaughter: domain.Known(false), SafeToSlaughter: domain.Known(true)},
	})
	populationMax := map[Resource]int64{"Muffalo": 1}
	choice := SelectHusbandryMethod(animals, noWild, herd(true, populationMax))
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
	choice := SelectHusbandryMethod(animals, noWild, herd(true, populationMax))
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
	choice := SelectHusbandryMethod(animals, noWild, herd(true, populationMax))
	if choice.Method != domain.HusbandrySlaughter || choice.Animal != "muffalo-3" {
		t.Fatal("an unsafe or already-designated animal must never be re-proposed", choice)
	}
}

func playerAnimal(id string, def Resource, safeRelease bool) UpkeepAnimal {
	return UpkeepAnimal{ID: PawnID(id), Definition: def, Release: domain.Known(false), Slaughter: domain.Known(false), SafeToSlaughter: domain.Known(true), SafeToRelease: domain.Known(safeRelease)}
}

func TestSelectHusbandryMethodPrefersReleaseOverSlaughterForSurplus(t *testing.T) {
	animals := domain.Known([]UpkeepAnimal{playerAnimal("muffalo-1", "Muffalo", true), playerAnimal("muffalo-2", "Muffalo", true)})
	populationMax := map[Resource]int64{"Muffalo": 1}
	choice := SelectHusbandryMethod(animals, noWild, HerdPolicy{AllowSlaughter: true, AllowRelease: true, PopulationMax: populationMax})
	if choice.Method != domain.HusbandryRelease || choice.Animal != "muffalo-1" {
		t.Fatal("release must be preferred over slaughter when both are allowed", choice)
	}
	choice = SelectHusbandryMethod(animals, noWild, HerdPolicy{AllowRelease: true, PopulationMax: populationMax})
	if choice.Method != domain.HusbandryRelease {
		t.Fatal("release alone must remove a surplus", choice)
	}
	if v, known := AnimalHerdDeficit(animals, noWild, HerdPolicy{AllowRelease: true, PopulationMax: populationMax}).Value(); !known || !v {
		t.Fatal("a releasable surplus is a deficit")
	}
}

func TestSelectHusbandryMethodReleaseUsesReleaseEligibilityOnly(t *testing.T) {
	animals := domain.Known([]UpkeepAnimal{playerAnimal("muffalo-1", "Muffalo", false), playerAnimal("muffalo-2", "Muffalo", false)})
	populationMax := map[Resource]int64{"Muffalo": 1}
	if choice := SelectHusbandryMethod(animals, noWild, HerdPolicy{AllowRelease: true, PopulationMax: populationMax}); choice.Reason != HusbandryNoDeficit {
		t.Fatal("a release-ineligible surplus must not fall back to slaughter without AllowSlaughter", choice)
	}
	unknown := domain.Known([]UpkeepAnimal{playerAnimal("muffalo-1", "Muffalo", true), {ID: "muffalo-2", Definition: "Muffalo", Release: domain.Known(false), Slaughter: domain.Known(false), SafeToSlaughter: domain.Known(true)}})
	if choice := SelectHusbandryMethod(unknown, noWild, HerdPolicy{AllowRelease: true, PopulationMax: populationMax}); choice.Reason != HusbandryUnknown {
		t.Fatal("an unknown release eligibility must leave the surplus unknown", choice)
	}
}

func TestSelectHusbandryMethodTamesTowardMinimum(t *testing.T) {
	animals := domain.Known([]UpkeepAnimal{playerAnimal("muffalo-1", "Muffalo", true)})
	wild := domain.Known([]UpkeepAnimal{
		wildAnimal("wild-3", "Muffalo", true, false),
		wildAnimal("wild-2", "Muffalo", true, false),
		wildAnimal("wild-1", "Muffalo", false, false),
		wildAnimal("thrumbo-1", "Thrumbo", true, false),
	})
	herd := HerdPolicy{PopulationMin: map[Resource]int64{"Muffalo": 2}}
	choice := SelectHusbandryMethod(animals, wild, herd)
	if choice.Method != domain.HusbandryTame || choice.Animal != "wild-2" || choice.TrainableDef != "" {
		t.Fatal("the lowest-ID tameable wild animal of a tracked race below minimum must be proposed", choice)
	}
	if v, known := AnimalHerdDeficit(animals, wild, herd).Value(); !known || !v {
		t.Fatal("a shortfall with a tame candidate is a deficit")
	}
}

func TestSelectHusbandryMethodTameCountsPendingDesignationsAndLeavingAnimals(t *testing.T) {
	leaving := playerAnimal("muffalo-1", "Muffalo", true)
	leaving.Release = domain.Known(true)
	animals := domain.Known([]UpkeepAnimal{leaving, playerAnimal("muffalo-2", "Muffalo", true)})
	wild := domain.Known([]UpkeepAnimal{wildAnimal("wild-1", "Muffalo", false, true), wildAnimal("wild-2", "Muffalo", true, false)})
	herd := HerdPolicy{PopulationMin: map[Resource]int64{"Muffalo": 2}}
	if choice := SelectHusbandryMethod(animals, wild, herd); choice.Reason != HusbandryNoDeficit {
		t.Fatal("a pending tame designation counts toward the minimum", choice)
	}
	herd.PopulationMin["Muffalo"] = 3
	if choice := SelectHusbandryMethod(animals, wild, herd); choice.Method != domain.HusbandryTame || choice.Animal != "wild-2" {
		t.Fatal("a release-designated animal does not count toward the minimum", choice)
	}
}

func TestSelectHusbandryMethodTameUnknownWildCensus(t *testing.T) {
	animals := domain.Known([]UpkeepAnimal{})
	herd := HerdPolicy{PopulationMin: map[Resource]int64{"Muffalo": 1}}
	if choice := SelectHusbandryMethod(animals, domain.Unknown[[]UpkeepAnimal](), herd); choice.Reason != HusbandryUnknown {
		t.Fatal("an unknown wild census must not be treated as no candidate", choice)
	}
	if _, known := AnimalHerdDeficit(animals, domain.Unknown[[]UpkeepAnimal](), herd).Value(); known {
		t.Fatal("an unknown wild census leaves the deficit unknown")
	}
	if choice := SelectHusbandryMethod(animals, domain.Unknown[[]UpkeepAnimal](), HerdPolicy{}); choice.Reason != HusbandryNoDeficit {
		t.Fatal("without a minimum the wild census is never consulted", choice)
	}
}

func TestRoutinePolicyHerdMinimumMustNotExceedMaximum(t *testing.T) {
	p := DefaultRoutinePolicy()
	p.HerdPopulationMin = map[Resource]int64{"Muffalo": 3}
	p.HerdPopulationMax = map[Resource]int64{"Muffalo": 2}
	if err := p.Validate(); err == nil {
		t.Fatal("minimum above maximum must be rejected")
	}
	p.HerdPopulationMax["Muffalo"] = 3
	if err := p.Validate(); err != nil {
		t.Fatal(err)
	}
}
