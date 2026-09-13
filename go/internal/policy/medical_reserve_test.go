package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"reflect"
	"testing"
)

func TestMedicineReserveHysteresisAndUsableStockCaps(t *testing.T) {
	f := MedicalReserveObservation{Colonists: domain.Known(int64(3)), Resources: domain.Known([]Amount{{"MedicineHerbal", 20}})}
	active := false
	for _, tc := range []struct {
		count   int64
		active  bool
		missing int64
	}{{2, true, 7}, {3, true, 6}, {8, true, 1}, {9, false, 0}, {3, false, 0}, {2, true, 7}} {
		f.Items = domain.Known([]MedicineStack{{ID: "medicine", Definition: "MedicineHerbal", Count: tc.count, Perishable: domain.Known(false)}})
		r, err := ReviewMedicalReserve(f, active, DefaultMedicalReservePolicy())
		if err != nil || r.Active != tc.active {
			t.Fatal(r, err)
		}
		n, k := r.Replenish.Value()
		if !k || n != tc.missing {
			t.Fatal(r)
		}
		active = r.Active
	}
	f.Items = domain.Known([]MedicineStack{
		{ID: "expired", Definition: "MedicineHerbal", Count: 100, Perishable: domain.Known(true), RotTicks: domain.Known(int64(0))},
		{ID: "forbidden", Definition: "MedicineHerbal", Count: 100, Forbidden: true, Perishable: domain.Known(false)},
		{ID: "usable", Definition: "MedicineHerbal", Count: 30, Perishable: domain.Known(true), RotTicks: domain.Known(int64(100))},
	})
	r, err := ReviewMedicalReserve(f, false, DefaultMedicalReservePolicy())
	if err != nil {
		t.Fatal(err)
	}
	if n, k := r.Stock.Value(); !k || n != 20 {
		t.Fatal(r)
	}
	f.Resources = domain.Known([]Amount{})
	r, err = ReviewMedicalReserve(f, false, DefaultMedicalReservePolicy())
	if err != nil || !r.Active {
		t.Fatal(r, err)
	}
	if n, _ := r.Stock.Value(); n != 0 {
		t.Fatal("uncounted stack credited", r)
	}
	f.Items = domain.Unknown[[]MedicineStack]()
	r, err = ReviewMedicalReserve(f, true, DefaultMedicalReservePolicy())
	if err != nil || !r.Active {
		t.Fatal(r, err)
	}
	if _, known := r.Replenish.Value(); known {
		t.Fatal("unknown reserve fabricated replenishment")
	}
}
func TestMedicineReserveRejectsDuplicateOrOverflowingFacts(t *testing.T) {
	f := MedicalReserveObservation{Colonists: domain.Known(int64(3)), Items: domain.Known([]MedicineStack{}), Resources: domain.Known([]Amount{{"MedicineHerbal", 1}, {"MedicineHerbal", 1}})}
	if _, err := ReviewMedicalReserve(f, false, DefaultMedicalReservePolicy()); err == nil {
		t.Fatal("duplicate stock")
	}
	f.Resources = domain.Known([]Amount{})
	if _, err := ReviewMedicalReserve(f, false, MedicalReservePolicy{1, 1 << 62}); err == nil {
		t.Fatal("overflow")
	}
	f.Colonists = domain.Known(int64(0))
	r, err := ReviewMedicalReserve(f, true, DefaultMedicalReservePolicy())
	if err != nil || r.Active {
		t.Fatal(r, err)
	}
}

func medicineFixture() MedicinePlanningRequest {
	recipe := GearRecipe{Definition: "MakeHerbalMedicine", Products: []Resource{"MedicineHerbal"}, Available: domain.Known(true), AvailableOn: domain.Known(true), Ingredients: domain.Known([][]Amount{{{"MedicalHerb", 5}}}), RequiredWork: domain.Known([]WorkRequirement{{Work: "Doctoring", Skill: "Medicine", Minimum: 4}})}
	bench := GearBench{ID: "bench", Bills: domain.Known([]GearBill{}), Recipes: domain.Known([]GearRecipe{recipe})}
	return MedicinePlanningRequest{
		Review:   MedicalReserveReview{Active: true, Target: domain.Known(int64(9))},
		Resource: "MedicineHerbal",
		Benches:  domain.Known([]GearBench{bench}),
		Stock:    []Stock{{"MedicalHerb", domain.Known(int64(50))}},
	}
}

func TestMedicineProductionSelectsFundedRecipe(t *testing.T) {
	r := medicineFixture()
	before := medicineFixture()
	method, err := SelectMedicineMethod(r)
	if err != nil || method.Kind != MedicineProduce || method.Bench != "bench" || method.Recipe != "MakeHerbalMedicine" || method.Target != 9 || !reflect.DeepEqual(method.Costs, []Amount{{"MedicalHerb", 5}}) || !reflect.DeepEqual(method.Filter, []Resource{"MedicalHerb"}) {
		t.Fatal(method, err)
	}
	if !reflect.DeepEqual(r, before) {
		t.Fatal("selection mutated caller inputs")
	}
	r.Seen = []domain.MethodID{method.ID}
	waiting, err := SelectMedicineMethod(r)
	if err != nil || waiting.Kind != MedicineWait {
		t.Fatal("previously seen medicine method must not repeat", waiting, err)
	}
}

func TestMedicineReviewInactiveOrRecoveredNeedsNoMethod(t *testing.T) {
	r := medicineFixture()
	r.Review.Active = false
	method, err := SelectMedicineMethod(r)
	if err != nil || method.Kind != MedicineRecovered {
		t.Fatal(method, err)
	}
	r.Review.Active = true
	r.Review.Target = domain.Known(int64(0))
	method, err = SelectMedicineMethod(r)
	if err != nil || method.Kind != MedicineRecovered {
		t.Fatal(method, err)
	}
	r.Review.Target = domain.Unknown[int64]()
	method, err = SelectMedicineMethod(r)
	if err != nil || method.Kind != MedicineUnknown {
		t.Fatal(method, err)
	}
	r = medicineFixture()
	r.Resource = ""
	method, err = SelectMedicineMethod(r)
	if err != nil || method.Kind != MedicineUnknown {
		t.Fatal(method, err)
	}
	r = medicineFixture()
	r.Review.Target = domain.Known(int64(20000))
	method, err = SelectMedicineMethod(r)
	if err != nil || method.Kind != MedicineBlocked {
		t.Fatal(method, err)
	}
}

func TestMedicineExistingActiveBillWaits(t *testing.T) {
	r := medicineFixture()
	v, _ := r.Benches.Value()
	v[0].Bills = domain.Known([]GearBill{{Active: domain.Known(true), Products: []Resource{"MedicineHerbal"}}})
	r.Benches = domain.Known(v)
	method, err := SelectMedicineMethod(r)
	if err != nil || method.Kind != MedicineWait {
		t.Fatal(method, err)
	}
}

func TestMedicineUnfundedRecipeIsBlocked(t *testing.T) {
	r := medicineFixture()
	r.Stock = []Stock{{"MedicalHerb", domain.Known(int64(2))}}
	method, err := SelectMedicineMethod(r)
	if err != nil || method.Kind != MedicineBlocked {
		t.Fatal(method, err)
	}
}

func TestMedicineUnknownBenchesOrRecipesRefuseGuessing(t *testing.T) {
	r := medicineFixture()
	r.Benches = domain.Unknown[[]GearBench]()
	if method, err := SelectMedicineMethod(r); err != nil || method.Kind != MedicineUnknown {
		t.Fatal(method, err)
	}
	r = medicineFixture()
	v, _ := r.Benches.Value()
	v[0].Recipes = domain.Unknown[[]GearRecipe]()
	r.Benches = domain.Known(v)
	if method, err := SelectMedicineMethod(r); err != nil || method.Kind != MedicineUnknown {
		t.Fatal(method, err)
	}
}
