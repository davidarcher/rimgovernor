package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
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
