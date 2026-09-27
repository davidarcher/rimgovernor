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
	if h := HerdFor(animals, domain.Unknown[WealthFacts](), domain.Unknown[[]PenGrazing]()); len(h.PopulationMax) != 0 {
		t.Fatal("unknown wealth caps nothing", h)
	}
	if h := HerdFor(animals, domain.Known(WealthFacts{Total: 100000}), domain.Unknown[[]PenGrazing]()); h.PopulationMax["Muffalo"] != 15 {
		t.Fatal(h)
	}
}

func TestHerdSurplusSlaughtersFirstAndKeepsBreedingPair(t *testing.T) {
	rows := []UpkeepAnimal{herdAnimal("m1", "Male", true, true), herdAnimal("m2", "Male", true, true),
		herdAnimal("f1", "Female", true, true), herdAnimal("f2", "Female", true, true), herdAnimal("f3", "Female", true, true)}
	limits := map[Resource]int64{"Muffalo": 1}
	got, unknown := herdSurplusCandidates(rows, limits, false)
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
	if got, _ := herdSurplusCandidates(rows, limits, false); len(got) != 1 || got[0].animal.ID != "m1" {
		t.Fatal("unknown sex is kept", got)
	}
}

func TestHerdSurplusReleasesOnlyWhenSlaughterRefused(t *testing.T) {
	rows := []UpkeepAnimal{herdAnimal("m1", "Male", false, true), herdAnimal("m2", "Male", false, false), herdAnimal("f1", "Female", true, true),
		herdAnimal("f2", "Female", true, true)}
	got, _ := herdSurplusCandidates(rows, map[Resource]int64{"Muffalo": 2}, false)
	if len(got) != 1 || got[0].animal.ID != "m1" || got[0].method != domain.HusbandryRelease {
		t.Fatal(got)
	}
	rows[1].SafeToSlaughter = domain.Unknown[bool]()
	if _, unknown := herdSurplusCandidates(rows, map[Resource]int64{"Muffalo": 2}, false); !unknown {
		t.Fatal("unknown eligibility stays unknown")
	}
	if _, unknown := herdSurplusCandidates(rows, map[Resource]int64{"Alpaca": 2}, false); unknown {
		t.Fatal("an untracked race's facts never block")
	}
}

func TestHerdSurplusRanksTrainedLast(t *testing.T) {
	rows := []UpkeepAnimal{herdAnimal("m1", "Male", true, true), herdAnimal("m2", "Male", true, true), herdAnimal("m3", "Male", true, true)}
	rows[0].Training = []HusbandryTrainable{trainable("Haul", true, true)}
	rows[1].Training = []HusbandryTrainable{trainable("Obedience", true, true)}
	got, _ := herdSurplusCandidates(rows, map[Resource]int64{"Muffalo": 2}, false)
	if len(got) != 1 || got[0].animal.ID != "m2" {
		t.Fatal("the hauler goes last", got)
	}
}

func TestHerdCullOrder(t *testing.T) {
	rows := []UpkeepAnimal{herdAnimal("a-bull", "Male", true, true), herdAnimal("b-bull", "Male", true, true),
		herdAnimal("c-old", "Female", true, true), herdAnimal("d-cow", "Female", true, true), herdAnimal("e-cow", "Female", true, true),
		herdAnimal("f-cow", "Female", true, true), herdAnimal("g-calf", "Female", true, true)}
	rows[2].Herd.AgeYears, rows[2].Herd.LifeExpectancy = domain.Known(13.0), domain.Known(15.0)
	rows[4].Herd.FeedPerDay, rows[4].Herd.MeatNutrition = domain.Known(2.0), domain.Known(10.0)
	rows[5].Herd.FeedPerDay, rows[5].Herd.MeatNutrition = domain.Known(1.0), domain.Known(10.0)
	rows[6].Herd.Adult = domain.Known(false)
	order := func(limit int64, juveniles bool) []PawnID {
		got, _ := herdSurplusCandidates(rows, map[Resource]int64{"Muffalo": limit}, juveniles)
		var ids []PawnID
		for _, r := range got {
			ids = append(ids, r.animal.ID)
		}
		return ids
	}
	// Old first, then the extra bull (5 females need one), then the hungriest cow.
	if got := order(4, false); len(got) != 3 || got[0] != "a-bull" || got[1] != "c-old" || got[2] != "e-cow" {
		t.Fatal(got)
	}
	// The pair (1 male, 2 females) stays; the calf goes only when feed is short.
	if got := order(0, false); len(got) != 4 || got[3] == "g-calf" {
		t.Fatal(got)
	}
	blobs := []UpkeepAnimal{herdAnimal("adult", "None", true, true), herdAnimal("young", "None", true, true)}
	blobs[1].Herd.Adult = domain.Known(false)
	for juveniles, want := range map[bool]int{false: 1, true: 2} {
		if got, _ := herdSurplusCandidates(blobs, map[Resource]int64{"Muffalo": 0}, juveniles); len(got) != want {
			t.Fatal(juveniles, got)
		}
	}
	rows[3].Herd.SlaughterBarred = domain.Known(true)
	if m, _ := herdRemovalMethod(rows[3]); m != domain.HusbandryRelease {
		t.Fatal("a barred slaughter releases", m)
	}
}

func TestHerdForFeedCapAndProducts(t *testing.T) {
	rows := []UpkeepAnimal{herdAnimal("a", "Male", true, true), herdAnimal("b", "Female", true, true), herdAnimal("c", "Female", true, true), herdAnimal("d", "Female", true, true)}
	for i := range rows {
		rows[i].RequiresPen = domain.Known(true)
	}
	rows[0].Herd.Product = true
	pens := domain.Known([]PenGrazing{{ID: "pen", DemandPerDay: domain.Known(4.0), PasturePerDay: domain.Known(2.0), StoredNutrition: domain.Known(15.0)}})
	h := HerdFor(domain.Known(rows), domain.Known(WealthFacts{Total: 0}), pens)
	// B = 2 + 15/15 = 3 against D = 4: floor(4 × 0.75) = 3.
	if h.PopulationMax["Muffalo"] != 3 || !h.FeedShort || h.PopulationMin["Muffalo"] != herdPairSize {
		t.Fatal(h)
	}
	if h := HerdFor(domain.Known(rows), domain.Known(WealthFacts{Total: 0}), domain.Unknown[[]PenGrazing]()); h.PopulationMax["Muffalo"] != 30 || h.FeedShort {
		t.Fatal("unknown pens cap nothing", h)
	}
}

func TestHerdTamesNoDangerousRaceAndStopsAtMax(t *testing.T) {
	wolf := wildAnimal("wolf", "Wolf_Timber", true, false)
	wolf.Herd.Predator = true
	bear := wildAnimal("bear", "Bear_Grizzly", true, false)
	bear.Herd.Predator = true
	emu := wildAnimal("emu", "Emu", true, false)
	emu.Herd.ManhunterOnTameFail = domain.Known(1.0)
	herd := HerdPolicy{PopulationMin: map[Resource]int64{"Wolf_Timber": 1, "Bear_Grizzly": 1, "Emu": 1}}
	got, _ := herdTameCandidates(nil, []UpkeepAnimal{wolf, bear, emu}, herd)
	if len(got) != 1 || got[0].ID != "bear" {
		t.Fatal(got)
	}
	herd.PopulationMax = map[Resource]int64{"Bear_Grizzly": 0}
	if got, _ := herdTameCandidates(nil, []UpkeepAnimal{bear}, herd); len(got) != 0 {
		t.Fatal("no feed room", got)
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
