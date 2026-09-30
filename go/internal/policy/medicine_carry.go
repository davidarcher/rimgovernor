package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Medicine carry (#1307, epic #1292): the bot owns every colonist's
// Medicine inventory-stock count. Doctors carry 1-3 so they tend without
// walking to stock; field roles carry 1-2 so a doctor can tend in the
// field. Everyone else carries none. The tier is the pawn's own medical
// care (native stocks the best medicine it allows); a care that allows no
// medicine carries none. The total carried is capped so colony stock stays
// at or above the medical reserve target: medicine already carried counts
// back into the budget, so the decision does not flip once pawns pick it
// up.

const (
	DoctorMaxCarry = domain.MaxMedicineCarry
	FieldMaxCarry  = 2
)

// MedicineCarryPawn is one colonist's carry inputs.
type MedicineCarryPawn struct {
	ID PawnID
	// Current is the Medicine stock count; unknown where the read did not
	// carry the pawn's inventory stock, which is never written.
	Current domain.Fact[int]
	// Care is the pawn's MedicalCareCategory name; unknown carries nothing
	// new.
	Care domain.Fact[string]
	// Doctor is Doctor work enabled; Field is a hunter or an armed ranged
	// fighter.
	Doctor, Field bool
}

// careAllowsMedicine reports a MedicalCareCategory that allows any medicine.
func careAllowsMedicine(care string) bool {
	switch care {
	case "HerbalOrWorse", "NormalOrWorse", "Best":
		return true
	}
	return false
}

// MedicineCarryPlan is each pawn's wanted count. stock is the colony's
// usable medicine on the map, reserve the target it must stay at. Unknown
// stock or reserve plans nothing.
func MedicineCarryPlan(pawns []MedicineCarryPawn, stock, reserve domain.Fact[int64]) (map[PawnID]int, bool) {
	s, sk := stock.Value()
	r, rk := reserve.Value()
	if !sk || !rk {
		return nil, false
	}
	budget := s - r
	for _, p := range pawns {
		if n, ok := p.Current.Value(); ok && n > 0 {
			budget += int64(n)
		}
	}
	want := map[PawnID]int{}
	var doctors, field []PawnID
	for _, p := range pawns {
		want[p.ID] = 0
		care, ok := p.Care.Value()
		if !ok || !careAllowsMedicine(care) {
			continue
		}
		if p.Doctor {
			doctors = append(doctors, p.ID)
		} else if p.Field {
			field = append(field, p.ID)
		}
	}
	sort.Slice(doctors, func(i, j int) bool { return doctors[i] < doctors[j] })
	sort.Slice(field, func(i, j int) bool { return field[i] < field[j] })
	// One unit a round: every doctor, then every field pawn, so the first
	// units spread across roles before anyone tops up.
	for round := 1; round <= DoctorMaxCarry; round++ {
		for _, group := range []struct {
			ids []PawnID
			max int
		}{{doctors, DoctorMaxCarry}, {field, FieldMaxCarry}} {
			if round > group.max {
				continue
			}
			for _, id := range group.ids {
				if budget <= 0 {
					return want, true
				}
				want[id]++
				budget--
			}
		}
	}
	return want, true
}

// MedicineCarryChanges are the settings to write: every pawn with a known
// count that differs from its planned one.
func MedicineCarryChanges(pawns []MedicineCarryPawn, stock, reserve domain.Fact[int64]) []domain.PawnSettings {
	want, ok := MedicineCarryPlan(pawns, stock, reserve)
	if !ok {
		return nil
	}
	var out []domain.PawnSettings
	for _, p := range pawns {
		current, ck := p.Current.Value()
		if !ck || current == want[p.ID] {
			continue
		}
		if s, err := domain.NewMedicineCarrySetting(domain.PawnID(p.ID), want[p.ID]); err == nil {
			out = append(out, s)
		}
	}
	return out
}

// MedicineCarryOwed is the review's MedicineCarryOwed fact.
func MedicineCarryOwed(pawns domain.Fact[[]WorkPawn], reserve MedicalReserveReview) domain.Fact[bool] {
	rows, known := pawns.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	return domain.Known(len(MedicineCarryChanges(MedicineCarryRows(rows), reserve.Stock, reserve.Target)) > 0)
}

// MedicineCarryRows lifts the work census into carry rows.
func MedicineCarryRows(pawns []WorkPawn) []MedicineCarryPawn {
	out := make([]MedicineCarryPawn, 0, len(pawns))
	for _, w := range pawns {
		out = append(out, w.MedicineCarryPawn())
	}
	return out
}

// MedicineCarryPawn is the pawn's carry inputs: the Medicine stock entry
// from the policy inputs, the medical care setting, Doctor or Hunting
// work enabled, and a violence-capable pawn with a ranged primary.
func (w WorkPawn) MedicineCarryPawn() MedicineCarryPawn {
	p := MedicineCarryPawn{ID: w.ID, Care: w.MedicalCare}
	if inputs, ok := w.PolicyInputs.Value(); ok {
		p.Current = domain.Known(0)
		for _, s := range inputs.InventoryStock {
			if s.Group == "Medicine" {
				p.Current = domain.Known(s.Count)
			}
		}
	}
	work, _ := w.Work.Value()
	hunter := false
	for _, x := range work {
		if x.Disabled || x.Priority <= 0 {
			continue
		}
		p.Doctor = p.Doctor || x.Work == WorkDoctor
		hunter = hunter || x.Work == WorkHunting
	}
	violent, vk := w.ViolenceCapable.Value()
	ranged, rk := w.Ranged.Value()
	p.Field = hunter || vk && violent && rk && ranged
	return p
}
