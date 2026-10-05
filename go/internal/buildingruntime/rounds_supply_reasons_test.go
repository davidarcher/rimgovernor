package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestMedicalReasonsNameTheMissingThing(t *testing.T) {
	known := domain.Known[int64](3)
	items := domain.Known([]policy.MedicineStack{})
	for _, test := range []struct {
		facts policy.MedicalReserveObservation
		want  string
	}{
		{policy.MedicalReserveObservation{Items: items}, "colonists"},
		{policy.MedicalReserveObservation{Colonists: known}, "medicine_items"},
		{policy.MedicalReserveObservation{Colonists: known, Items: items}, "medicine_stock"},
	} {
		if got := unreadMedicalFact(test.facts); got != test.want {
			t.Fatalf("unread fact %q, want %q", got, test.want)
		}
	}
	for _, test := range []struct {
		kind policy.MedicineMethodKind
		want Verdict
	}{
		{policy.MedicineRecovered, BuildingReasonNoDeficit},
		{policy.MedicineUnknown, fieldUnavailable("bench_recipes")},
		{policy.MedicineBlocked, awaitingPlan("production_bench", "MedicineHerbal")},
		{policy.MedicineWait, waitFor(WaitMethodUsed, "medicine_method")},
	} {
		if got := medicineChoiceVerdict(test.kind, "MedicineHerbal"); got != test.want {
			t.Fatalf("%s reads %v, want %v", test.kind, got, test.want)
		}
	}
}

func TestFoodStorageChoiceReasons(t *testing.T) {
	for kind, want := range map[policy.FoodStorageMethodKind]Verdict{
		policy.FoodStorageRecovered: BuildingReasonNoDeficit,
		policy.FoodStorageUnknown:   fieldUnavailable("food_storage_capacity"),
		policy.FoodStorageBlocked:   waitFor(WaitMethodUsed, "food_storage_method"),
	} {
		if got := foodStorageChoiceVerdict(kind); got != want {
			t.Fatalf("%s reads %v, want %v", kind, got, want)
		}
	}
}

func TestContainmentWaitsReadInPlainEnglish(t *testing.T) {
	for reason, text := range map[policy.AnimalContainmentReason]string{
		policy.ContainmentWaitingHandler:   "no colonist free to do it (animal handler)",
		policy.ContainmentWaitingNativePen: "waiting on native pen (delivery)",
		policy.ContainmentMarkerExhausted:  "waiting on native pen (marker placed)",
		policy.ContainmentExceedsBound:     "waiting on pen (herd exceeds planning limit)",
		policy.ContainmentAwaitingShell:    "waiting on pen shell (completion)",
		policy.ContainmentShellExhausted:   "waiting on pen shell (rebuilds exhausted)",
	} {
		if got := containmentWait(reason).Text(); got != text {
			t.Fatalf("%s reads %q, want %q", reason, got, text)
		}
	}
}

func TestNoBillNamesTheBenchOrTheCensus(t *testing.T) {
	cook := &RoundsBillPlanner{purpose: policy.CookFood}
	butcher := &RoundsBillPlanner{purpose: policy.ButcherFood}
	colonists := domain.Known[int64](4)
	usable := func(butchery bool) domain.Fact[[]policy.ProductionBench] {
		return domain.Known([]policy.ProductionBench{{ID: "bench", Butcher: butchery, Usable: domain.Known(true)}})
	}
	for _, test := range []struct {
		name      string
		planner   *RoundsBillPlanner
		benches   domain.Fact[[]policy.ProductionBench]
		colonists domain.Fact[int64]
		want      Verdict
	}{
		{"benches unread", cook, domain.Unknown[[]policy.ProductionBench](), colonists, fieldUnavailable("production_benches")},
		{"colonists unread", cook, usable(false), domain.Unknown[int64](), fieldUnavailable("colonists")},
		{"no cooking bench", cook, usable(true), colonists, awaitingPlan("cooking_bench", "")},
		{"no butcher bench", butcher, usable(false), colonists, awaitingPlan("butcher_bench", "")},
		{"bills cover it", cook, usable(false), colonists, BuildingReasonNoDeficit},
	} {
		if got := test.planner.noBill(test.benches, test.colonists); got != test.want {
			t.Fatalf("%s reads %v, want %v", test.name, got, test.want)
		}
	}
}

func TestAcquisitionReasonsNameTheMissingThing(t *testing.T) {
	sources := domain.Known([]policy.AcquisitionSource{})
	amount := domain.Known(1.0)
	for _, test := range []struct {
		name            string
		sources         domain.Fact[[]policy.AcquisitionSource]
		deficit, orders domain.Fact[float64]
		withoutDeficit  bool
		want            string
	}{
		{"census unread", domain.Unknown[[]policy.AcquisitionSource](), amount, amount, false, "acquisition_sources"},
		{"deficit unread", sources, domain.Unknown[float64](), amount, false, "acquisition_deficit"},
		{"pending unread", sources, amount, domain.Unknown[float64](), false, "pending_acquisition"},
		{"deficit unused", sources, domain.Unknown[float64](), domain.Unknown[float64](), true, "acquisition_sources"},
	} {
		if got := unreadAcquisitionFact(test.sources, test.deficit, test.orders, test.withoutDeficit); got != test.want {
			t.Fatalf("%s reads %q, want %q", test.name, got, test.want)
		}
	}
	covered, owed := domain.Known(2.0), domain.Known(1.5)
	for _, test := range []struct {
		name     string
		need     policy.ConcernID
		noHunter bool
		deficit  domain.Fact[float64]
		pending  domain.Fact[float64]
		want     Verdict
	}{
		{"pests wait for a hunter", policy.ClearPests, true, owed, covered, noWorker("hunter")},
		{"every pest is hunted", policy.ClearPests, false, owed, covered, BuildingReasonNoDeficit},
		{"food in flight covers it", policy.EnsureFoodSupply, false, owed, covered, BuildingReasonExistingWork},
		{"no source", policy.EnsureFoodSupply, false, covered, owed, awaitingPlan("acquisition_source", "EnsureFoodSupply")},
	} {
		if got := noAcquisition(test.need, test.noHunter, test.deficit, test.pending); got != test.want {
			t.Fatalf("%s reads %v, want %v", test.name, got, test.want)
		}
	}
}

// A shell lost after completion is rebuilt under its own method: a method
// binds once per Episode, so reusing pen-shell failed every cycle.
func TestAnimalShellMethodForNamesEachRebuild(t *testing.T) {
	seen := map[domain.MethodID]bool{}
	for lost := 0; lost < maxAnimalShellRebuilds; lost++ {
		m := animalShellMethodFor(lost)
		if seen[m] {
			t.Fatalf("attempt %d reused %s", lost, m)
		}
		seen[m] = true
	}
	if animalShellMethodFor(0) != animalShellMethod {
		t.Fatal("first shell renamed")
	}
}
