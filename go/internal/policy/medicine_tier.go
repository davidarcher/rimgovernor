package policy

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// MedicineTier is a ceiling for ordinary native tending, never a forced drug use.
type MedicineTier string

const (
	MedicineNoMeds         MedicineTier = "NoMeds"
	MedicineHerbalTier     MedicineTier = "HerbalOrWorse"
	MedicineIndustrialTier MedicineTier = "NormalOrWorse"
)

// SelectMedicineTier requires a usable stock
// census. Unknown clinical evidence does not authorize lowering care. Native
// tending still decides reachability and whether the selected medicine can be used.
func SelectMedicineTier(conditions domain.Fact[[]CareCondition], lifeThreatening domain.Fact[bool], stock domain.Fact[[]Amount]) domain.Fact[MedicineTier] {
	unknown := domain.Unknown[MedicineTier]()
	items, sk := stock.Value()
	rows, ck := conditions.Value()
	life, lk := lifeThreatening.Value()
	if !sk || !ck || !lk {
		return unknown
	}
	herbal, industrial := false, false
	seen := map[Resource]bool{}
	for _, item := range items {
		if item.Count < 0 || seen[item.Resource] {
			return unknown
		}
		seen[item.Resource] = true
		if item.Resource == "MedicineHerbal" {
			herbal = item.Count > 0
		}
		if item.Resource == "MedicineIndustrial" {
			industrial = item.Count > 0
		}
	}
	urgent, disease, incomplete := life, false, false
	for _, row := range rows {
		name, nk := row.DefName.Value()
		switch name {
		case "Flu", "Plague", "Malaria", "SleepingSickness", "WoundInfection":
		default:
			if !nk {
				incomplete = true
			}
			continue
		}
		severity, sv := row.Severity.Value()
		immunity, iv := row.Immunity.Value()
		if !sv || !iv || !finiteUnit(severity) || !finiteUnit(immunity) {
			incomplete = true
			continue
		}
		if immunity >= 1 {
			continue
		}
		disease = true
		if name == "Plague" || name == "Malaria" && severity >= 0.5 {
			urgent = true
		}
		sr, srk := row.SeverityPerDay.Value()
		ir, irk := row.ImmunityPerDay.Value()
		if !srk || !irk || math.IsNaN(sr) || math.IsNaN(ir) || math.IsInf(sr, 0) || math.IsInf(ir, 0) {
			incomplete = true
			continue
		}
		// Ties leave no safety margin; nonpositive immunity gain loses against
		// progressing disease. Nonprogressing severity has no projected deadline.
		if sr > 0 && (ir <= 0 || (1-severity)/sr <= (1-immunity)/ir) {
			urgent = true
		}
	}
	if urgent && industrial {
		return domain.Known(MedicineIndustrialTier)
	}
	if incomplete && !urgent || !disease && !life {
		return unknown
	}
	if herbal {
		return domain.Known(MedicineHerbalTier)
	}
	// Industrial is the usable fallback when herbal is absent. Glitterworld
	// stock never raises the ceiling, including when it is the only stock.
	if industrial {
		return domain.Known(MedicineIndustrialTier)
	}
	return domain.Known(MedicineNoMeds)
}

func finiteUnit(v float64) bool { return !math.IsNaN(v) && !math.IsInf(v, 0) && v >= 0 && v <= 1 }
