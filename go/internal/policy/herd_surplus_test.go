package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func herdAnimal(id, gender string, slaughter, release bool) UpkeepAnimal {
	return UpkeepAnimal{ID: PawnID(id), Definition: "Muffalo", Gender: gender, Release: domain.Known(false), Slaughter: domain.Known(false), SafeToSlaughter: domain.Known(slaughter), SafeToRelease: domain.Known(release)}
}

func playerAnimal(id string, def Resource, safeRelease bool) UpkeepAnimal {
	return UpkeepAnimal{ID: PawnID(id), Definition: def, Release: domain.Known(false), Slaughter: domain.Known(false), SafeToSlaughter: domain.Known(true), SafeToRelease: domain.Known(safeRelease)}
}

func TestHerdWealthCap(t *testing.T) {
	for wealth, want := range map[float64]int64{0: 30, 50000: 30, 100000: 15, 150000: 10, 250000: 6, 1e7: 6} {
		if got := HerdWealthCap(wealth); got != want {
			t.Errorf("%v: %d want %d", wealth, got, want)
		}
	}
	animals := domain.Known([]UpkeepAnimal{herdAnimal("a", "Male", true, true)})
	if h := HerdFor(animals, domain.Unknown[WealthFacts]()); len(h.PopulationMax) != 0 {
		t.Fatal("unknown wealth caps nothing", h)
	}
	if h := HerdFor(animals, domain.Known(WealthFacts{Total: 100000})); h.PopulationMax["Muffalo"] != 15 {
		t.Fatal(h)
	}
}

func TestHerdSurplusSlaughtersFirstAndKeepsBreedingPair(t *testing.T) {
	rows := []UpkeepAnimal{herdAnimal("m1", "Male", true, true), herdAnimal("m2", "Male", true, true),
		herdAnimal("f1", "Female", true, true), herdAnimal("f2", "Female", true, true), herdAnimal("f3", "Female", true, true)}
	limits := map[Resource]int64{"Muffalo": 1}
	got, unknown := herdSurplusCandidates(rows, limits)
	// Surplus is 4 but only one male and one female may go.
	if unknown || len(got) != 2 || got[0].animal.ID != "f1" || got[1].animal.ID != "m1" || got[0].method != domain.HusbandrySlaughter {
		t.Fatal(got, unknown)
	}
	if c := SelectHusbandryMethod(domain.Known(rows), noWild, feedFine, HerdPolicy{PopulationMax: limits}, anyTamer); c.Method != domain.HusbandrySlaughter || c.Animal != "f1" {
		t.Fatal(c)
	}
	if v, known := AnimalHerdDeficit(domain.Known(rows), noWild, feedFine, HerdPolicy{PopulationMax: limits}).Value(); !known || !v {
		t.Fatal("surplus is a deficit")
	}
	if v, known := AnimalHerdDeficit(domain.Known(rows), noWild, feedFine, HerdPolicy{}).Value(); !known || v {
		t.Fatal("an uncapped race is never surplus")
	}
	rows[2].Gender = ""
	rows[3].Gender = ""
	if got, _ := herdSurplusCandidates(rows, limits); len(got) != 1 || got[0].animal.ID != "m1" {
		t.Fatal("unknown sex is kept", got)
	}
}

func TestHerdSurplusReleasesOnlyWhenSlaughterRefused(t *testing.T) {
	rows := []UpkeepAnimal{herdAnimal("m1", "Male", false, true), herdAnimal("m2", "Male", false, false), herdAnimal("f1", "Female", true, true),
		herdAnimal("f2", "Female", true, true)}
	got, _ := herdSurplusCandidates(rows, map[Resource]int64{"Muffalo": 2})
	if len(got) != 1 || got[0].animal.ID != "m1" || got[0].method != domain.HusbandryRelease {
		t.Fatal(got)
	}
	rows[1].SafeToSlaughter = domain.Unknown[bool]()
	if _, unknown := herdSurplusCandidates(rows, map[Resource]int64{"Muffalo": 2}); !unknown {
		t.Fatal("unknown eligibility stays unknown")
	}
	if _, unknown := herdSurplusCandidates(rows, map[Resource]int64{"Alpaca": 2}); unknown {
		t.Fatal("an untracked race's facts never block")
	}
}

func TestHerdSurplusRanksTrainedLast(t *testing.T) {
	rows := []UpkeepAnimal{herdAnimal("m1", "Male", true, true), herdAnimal("m2", "Male", true, true), herdAnimal("m3", "Male", true, true)}
	rows[0].Training = []HusbandryTrainable{trainable("Haul", true, true)}
	rows[1].Training = []HusbandryTrainable{trainable("Obedience", true, true)}
	got, _ := herdSurplusCandidates(rows, map[Resource]int64{"Muffalo": 2})
	if len(got) != 1 || got[0].animal.ID != "m2" {
		t.Fatal("the hauler goes last", got)
	}
}

func TestHerdRemovalUnknownCapKeepsDesignation(t *testing.T) {
	rows := []UpkeepAnimal{herdAnimal("m1", "Male", true, true), herdAnimal("f1", "Female", true, true), herdAnimal("f2", "Female", true, true), herdAnimal("f3", "Female", true, true)}
	rows[3].Slaughter = domain.Known(true)
	if got := ReconcileHerdRemoval(domain.Known(rows), HerdPolicy{}, domain.Known(FoodPlan{})); got.Reason != HusbandryUnknown {
		t.Fatal(got)
	}
	if got := ReconcileHerdRemoval(domain.Known(rows), HerdPolicy{PopulationMax: map[Resource]int64{"Muffalo": 3}}, domain.Known(FoodPlan{})); got.Reason != HusbandryNoDeficit {
		t.Fatal(got)
	}
}
