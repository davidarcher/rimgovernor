package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func carryPawn(id PawnID, doctor, field bool, current int) MedicineCarryPawn {
	return MedicineCarryPawn{ID: id, Current: domain.Known(current), Care: domain.Known("NormalOrWorse"), Doctor: doctor, Field: field}
}

// Carry counts by role, capped so colony stock stays at the reserve:
// doctors fill first, one unit a round, then field roles.
func TestMedicineCarryPlanByStock(t *testing.T) {
	pawns := []MedicineCarryPawn{carryPawn("doc", true, false, 0), carryPawn("hunter", false, true, 0), carryPawn("cook", false, false, 2)}
	for name, c := range map[string]struct {
		stock, reserve int64
		want           map[PawnID]int
	}{
		"ample":         {100, 9, map[PawnID]int{"doc": 3, "hunter": 2, "cook": 0}},
		"at reserve":    {9, 9, map[PawnID]int{"doc": 1, "hunter": 1, "cook": 0}}, // cook's 2 carried return to the budget
		"below reserve": {3, 9, map[PawnID]int{"doc": 0, "hunter": 0, "cook": 0}},
		"one spare":     {8, 9, map[PawnID]int{"doc": 1, "hunter": 0, "cook": 0}},
		"four spare":    {11, 9, map[PawnID]int{"doc": 2, "hunter": 2, "cook": 0}},
	} {
		t.Run(name, func(t *testing.T) {
			got, ok := MedicineCarryPlan(pawns, domain.Known(c.stock), domain.Known(c.reserve))
			if !ok {
				t.Fatal("unplanned")
			}
			for id, n := range c.want {
				if got[id] != n {
					t.Fatalf("%s: got %v want %v", id, got, c.want)
				}
			}
		})
	}
}

func TestMedicineCarryTierAndUnknowns(t *testing.T) {
	noMeds := carryPawn("doc", true, false, 1)
	noMeds.Care = domain.Known("NoMeds")
	if got := MedicineCarryChanges([]MedicineCarryPawn{noMeds}, domain.Known(int64(100)), domain.Known(int64(3))); len(got) != 1 {
		t.Fatal("no-meds doctor keeps carrying", got)
	} else if n, carry := got[0].MedicineCarry(); !carry || n != 0 {
		t.Fatal(got)
	}
	if got := MedicineCarryChanges([]MedicineCarryPawn{carryPawn("doc", true, false, 0)}, domain.Unknown[int64](), domain.Known(int64(3))); got != nil {
		t.Fatal("unknown stock planned", got)
	}
	unread := carryPawn("doc", true, false, 0)
	unread.Current = domain.Unknown[int]()
	if got := MedicineCarryChanges([]MedicineCarryPawn{unread}, domain.Known(int64(100)), domain.Known(int64(3))); len(got) != 0 {
		t.Fatal("unread stock written", got)
	}
	held := carryPawn("doc", true, false, 3)
	if got := MedicineCarryChanges([]MedicineCarryPawn{held}, domain.Known(int64(100)), domain.Known(int64(3))); len(got) != 0 {
		t.Fatal("held carry rewritten", got)
	}
}

func TestMedicineCarryPawnRoles(t *testing.T) {
	w := WorkPawn{ID: "p", PolicyInputs: domain.Known(PawnPolicyInputs{InventoryStock: []InventoryStock{{Group: "Medicine", Thing: "MedicineHerbal", Count: 2}}}),
		Work: domain.Known([]WorkPriority{{Work: WorkDoctor, Priority: 2}, {Work: WorkHunting, Priority: 0}})}
	p := w.MedicineCarryPawn()
	if n, _ := p.Current.Value(); n != 2 || !p.Doctor || p.Field {
		t.Fatal(p)
	}
	w.Work = domain.Known([]WorkPriority{{Work: WorkHunting, Priority: 3}})
	if p = w.MedicineCarryPawn(); p.Doctor || !p.Field {
		t.Fatal(p)
	}
	w.Work = domain.Known([]WorkPriority{})
	w.ViolenceCapable, w.Ranged = domain.Known(true), domain.Known(true)
	if p = w.MedicineCarryPawn(); !p.Field {
		t.Fatal("ranged fighter is no field role", p)
	}
}
