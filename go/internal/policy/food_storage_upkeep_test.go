package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func riskyStock(id string, nutrition float64, stored bool) FoodStorageStock {
	return FoodStorageStock{
		Stock: FoodStock{ID: id, Nutrition: domain.Known(nutrition), Perishable: domain.Known(true), RotTicks: domain.Known(int64(1000))},
		Stored: domain.Known(stored),
	}
}

func TestFoodStorageReviewHysteresis(t *testing.T) {
	active := false
	for _, tc := range []struct {
		name    string
		stocks  []FoodStorageStock
		active  bool
		deficit float64
	}{
		// 100 stored, 0 unstored: fully stored, no deficit.
		{"fully_stored", []FoodStorageStock{riskyStock("a", 100, true)}, false, 0},
		// 40 stored / 60 unstored of 100 total: stored fraction 0.4 < entry 0.5, at-risk exceeds floor.
		// Deficit is only the gap up to the entry threshold (50-40=10), capped by what's unstored.
		{"below_entry", []FoodStorageStock{riskyStock("a", 40, true), riskyStock("b", 60, false)}, true, 10},
		// Still active: exit fraction is 0.9, so 60/100 stored (0.6) is not recovered.
		{"still_below_exit", []FoodStorageStock{riskyStock("a", 60, true), riskyStock("b", 40, false)}, true, 30},
		// Recovers once stored fraction clears the higher exit bar.
		{"recovered", []FoodStorageStock{riskyStock("a", 95, true), riskyStock("b", 5, false)}, false, 0},
	} {
		obs := FoodStorageObservation{Stocks: domain.Known(tc.stocks)}
		r, err := ReviewFoodStorage(obs, active, DefaultFoodStoragePolicy())
		if err != nil {
			t.Fatalf("%s: unexpected error: %v", tc.name, err)
		}
		if r.Active != tc.active {
			t.Fatalf("%s: active = %v, want %v (%+v)", tc.name, r.Active, tc.active, r)
		}
		if d, k := r.Deficit.Value(); !k || d != tc.deficit {
			t.Fatalf("%s: deficit = %v (known=%v), want %v", tc.name, d, k, tc.deficit)
		}
		active = r.Active
	}
}

func TestFoodStorageReviewIgnoresTinyAtRiskAmount(t *testing.T) {
	// stored=4.1 is below the entry threshold (0.5*9=4.5), which alone would
	// activate the deficit -- but unstored=4.9 is under AtRiskNutritionThreshold
	// (5), so the tiny amount must not (re)activate it.
	obs := FoodStorageObservation{Stocks: domain.Known([]FoodStorageStock{
		riskyStock("stored", 4.1, true), riskyStock("unstored", 4.9, false),
	})}
	r, err := ReviewFoodStorage(obs, false, DefaultFoodStoragePolicy())
	if err != nil {
		t.Fatal(err)
	}
	if r.Active {
		t.Fatal("tiny at-risk amount must not activate the deficit", r)
	}
	if d, k := r.Deficit.Value(); !k || d != 0 {
		t.Fatal("inactive review must report zero deficit", r)
	}
}

func TestFoodStorageReviewUnknownFactsPreserveLatch(t *testing.T) {
	obs := FoodStorageObservation{Stocks: domain.Unknown[[]FoodStorageStock]()}
	r, err := ReviewFoodStorage(obs, true, DefaultFoodStoragePolicy())
	if err != nil || !r.Active {
		t.Fatal(r, err)
	}
	if _, known := r.Deficit.Value(); known {
		t.Fatal("unknown census fabricated a deficit")
	}
	r, err = ReviewFoodStorage(obs, false, DefaultFoodStoragePolicy())
	if err != nil || r.Active {
		t.Fatal(r, err)
	}

	// An unknown Stored fact on any at-risk stock also preserves the latch.
	obs = FoodStorageObservation{Stocks: domain.Known([]FoodStorageStock{
		{Stock: FoodStock{ID: "a", Nutrition: domain.Known(50.0), Perishable: domain.Known(true), RotTicks: domain.Known(int64(500))}, Stored: domain.Unknown[bool]()},
	})}
	r, err = ReviewFoodStorage(obs, true, DefaultFoodStoragePolicy())
	if err != nil || !r.Active {
		t.Fatal(r, err)
	}
	if _, known := r.Deficit.Value(); known {
		t.Fatal("unknown storage fact fabricated a deficit")
	}
}

func TestFoodStorageReviewIgnoresNonPerishableAndRotted(t *testing.T) {
	obs := FoodStorageObservation{Stocks: domain.Known([]FoodStorageStock{
		// Non-perishable: never counted.
		{Stock: FoodStock{ID: "meals", Nutrition: domain.Known(1000.0), Perishable: domain.Known(false)}, Stored: domain.Known(false)},
		// Already rotted (RotTicks == 0): never counted.
		{Stock: FoodStock{ID: "rotten", Nutrition: domain.Known(1000.0), Perishable: domain.Known(true), RotTicks: domain.Known(int64(0))}, Stored: domain.Known(false)},
	})}
	r, err := ReviewFoodStorage(obs, false, DefaultFoodStoragePolicy())
	if err != nil {
		t.Fatal(err)
	}
	if total, k := r.TotalNutrition.Value(); !k || total != 0 {
		t.Fatal("non-perishable/rotted stock counted", r)
	}
	if r.Active {
		t.Fatal("no at-risk stock must not activate", r)
	}
}

func TestFoodStorageReviewRejectsInvalidFacts(t *testing.T) {
	if _, err := ReviewFoodStorage(FoodStorageObservation{}, false, FoodStoragePolicy{0.9, 0.5, 5}); err == nil {
		t.Fatal("unordered thresholds must be rejected")
	}
	dup := FoodStorageObservation{Stocks: domain.Known([]FoodStorageStock{
		riskyStock("dup", 10, false), riskyStock("dup", 10, false),
	})}
	if _, err := ReviewFoodStorage(dup, false, DefaultFoodStoragePolicy()); err == nil {
		t.Fatal("duplicate stock IDs must be rejected")
	}
	negTicks := FoodStorageObservation{Stocks: domain.Known([]FoodStorageStock{
		{Stock: FoodStock{ID: "a", Nutrition: domain.Known(10.0), Perishable: domain.Known(true), RotTicks: domain.Known(int64(-1))}, Stored: domain.Known(false)},
	})}
	if _, err := ReviewFoodStorage(negTicks, false, DefaultFoodStoragePolicy()); err == nil {
		t.Fatal("negative rot ticks must be rejected")
	}
}

func foodStorageFixture() FoodStoragePlanningRequest {
	return FoodStoragePlanningRequest{
		Review:   FoodStorageReview{Active: true, Deficit: domain.Known(20.0)},
		Sites:    domain.Known([]FoodStorageSite{{ID: "cellar-b", Capacity: domain.Known(int64(5))}, {ID: "cellar-a", Capacity: domain.Known(int64(30))}}),
		Resource: "MealNutrientPaste",
	}
}

func TestFoodStorageSelectRecovered(t *testing.T) {
	r := foodStorageFixture()
	r.Review.Active = false
	method, err := SelectFoodStorageMethod(r)
	if err != nil || method.Kind != FoodStorageRecovered {
		t.Fatal(method, err)
	}
	r = foodStorageFixture()
	r.Review.Deficit = domain.Known(0.0)
	method, err = SelectFoodStorageMethod(r)
	if err != nil || method.Kind != FoodStorageRecovered {
		t.Fatal(method, err)
	}
}

func TestFoodStorageSelectRelocatePicksLowestIDWithCapacity(t *testing.T) {
	r := foodStorageFixture()
	method, err := SelectFoodStorageMethod(r)
	if err != nil || method.Kind != FoodStorageRelocate || method.Site != "cellar-a" || method.Amount != 20 {
		t.Fatal(method, err)
	}
	// Once seen, that relocation is skipped in favor of the next candidate.
	r.Seen = []domain.MethodID{method.ID}
	method2, err := SelectFoodStorageMethod(r)
	if err != nil || method2.Kind != FoodStorageRelocate || method2.Site != "cellar-b" || method2.Amount != 5 {
		t.Fatal(method2, err)
	}
}

func TestFoodStorageSelectProduceWhenNoSiteHasRoom(t *testing.T) {
	r := foodStorageFixture()
	r.Sites = domain.Known([]FoodStorageSite{{ID: "full", Capacity: domain.Known(int64(0))}})
	method, err := SelectFoodStorageMethod(r)
	if err != nil || method.Kind != FoodStorageProduce || method.Resource != "MealNutrientPaste" || method.Target != 20 {
		t.Fatal(method, err)
	}
	// A produce method already Seen is Blocked rather than repeated.
	r.Seen = []domain.MethodID{method.ID}
	method2, err := SelectFoodStorageMethod(r)
	if err != nil || method2.Kind != FoodStorageBlocked {
		t.Fatal(method2, err)
	}
}

func TestFoodStorageSelectBlockedWithoutResourceOrSites(t *testing.T) {
	r := foodStorageFixture()
	r.Sites = domain.Known([]FoodStorageSite{})
	r.Resource = ""
	method, err := SelectFoodStorageMethod(r)
	if err != nil || method.Kind != FoodStorageBlocked {
		t.Fatal(method, err)
	}
}

func TestFoodStorageSelectUnknownFactsRefuseGuessing(t *testing.T) {
	r := foodStorageFixture()
	r.Review.Deficit = domain.Unknown[float64]()
	method, err := SelectFoodStorageMethod(r)
	if err != nil || method.Kind != FoodStorageUnknown {
		t.Fatal(method, err)
	}
	r = foodStorageFixture()
	r.Sites = domain.Unknown[[]FoodStorageSite]()
	method, err = SelectFoodStorageMethod(r)
	if err != nil || method.Kind != FoodStorageUnknown {
		t.Fatal(method, err)
	}
	r = foodStorageFixture()
	sites, _ := r.Sites.Value()
	sites[0].Capacity = domain.Unknown[int64]()
	sites[1].Capacity = domain.Unknown[int64]()
	r.Sites = domain.Known(sites)
	method, err = SelectFoodStorageMethod(r)
	if err != nil || method.Kind != FoodStorageUnknown {
		t.Fatal(method, err)
	}
}

func TestFoodStorageSelectRejectsInvalidHistoryAndSites(t *testing.T) {
	r := foodStorageFixture()
	r.Seen = []domain.MethodID{""}
	if _, err := SelectFoodStorageMethod(r); err == nil {
		t.Fatal("empty method id in history must be rejected")
	}
	r = foodStorageFixture()
	r.Sites = domain.Known([]FoodStorageSite{{ID: "same", Capacity: domain.Known(int64(1))}, {ID: "same", Capacity: domain.Known(int64(1))}})
	if _, err := SelectFoodStorageMethod(r); err == nil {
		t.Fatal("duplicate site id must be rejected")
	}
	r = foodStorageFixture()
	r.Sites = domain.Known([]FoodStorageSite{{ID: "bad", Capacity: domain.Known(int64(-1))}})
	if _, err := SelectFoodStorageMethod(r); err == nil {
		t.Fatal("negative site capacity must be rejected")
	}
}
