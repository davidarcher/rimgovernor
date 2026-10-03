package policy

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Medical care cap (#1301, epic #1292): every pawn the colony cares for
// holds a standing MedicalCareCategory by what it is worth, raised one tier
// while a serious condition lasts. EnsureWorkAssignments writes the caps
// through PawnSettingsIntent.
//
//   - colonists: NormalOrWorse while the usable industrial medicine stock
//     meets MedicalReservePolicy.TargetPerColonist per colonist, else
//     HerbalOrWorse; never Best as a standing cap.
//   - prisoners being recruited, broken, converted or enslaved:
//     NormalOrWorse; maintain-only, release, execution and harvest targets:
//     HerbalOrWorse. Harvest and execution targets are never raised.
//   - guests: NormalOrWorse, the recruit class the issue groups them with.
//   - animals: HerbalOrWorse; bonded or combat (Release trained) animals
//     NormalOrWorse.

// CarePatient is one guest's care cap inputs.
type CarePatient struct {
	ID              PawnID
	Care            domain.Fact[string]
	Conditions      domain.Fact[[]CareCondition]
	LifeThreatening domain.Fact[bool]
}

// SeriousCondition reports a condition worth one more medicine tier: a
// native life threat, plague, malaria past half severity, a wound infection
// not yet immune, or any immunizable disease losing its immunity race.
// Unknown evidence is unknown unless a known condition is already serious.
func SeriousCondition(conditions domain.Fact[[]CareCondition], life domain.Fact[bool]) (serious, known bool) {
	if positive(life) {
		return true, true
	}
	rows, ck := conditions.Value()
	_, lk := life.Value()
	incomplete := !ck || !lk
	for _, row := range rows {
		name, nk := row.DefName.Value()
		immunity, ik := row.Immunity.Value()
		if !ik {
			if !nk {
				incomplete = true
			}
			continue
		}
		severity, sk := row.Severity.Value()
		if !nk || !sk || !finiteUnit(severity) || !finiteUnit(immunity) {
			incomplete = true
			continue
		}
		if immunity >= 1 {
			continue
		}
		if name == "Plague" || name == woundInfection || name == "Malaria" && severity >= 0.5 {
			return true, true
		}
		losing, known := LosingImmunityRace(row)
		if losing {
			return true, true
		}
		incomplete = incomplete || !known
	}
	return false, !incomplete
}

// capped is the cap with the serious raise applied; ok is false when the
// raise is owed but the evidence is unknown.
func capped(base domain.MedicalCare, raise bool, conditions domain.Fact[[]CareCondition], life domain.Fact[bool]) (domain.MedicalCare, bool) {
	if !raise {
		return base, true
	}
	serious, known := SeriousCondition(conditions, life)
	if !known {
		return "", false
	}
	if serious {
		return base.Raised(), true
	}
	return base, true
}

// ColonistCareBase is the colonists' standing cap: NormalOrWorse while
// stock of the normal-care medicine (potency rank 1) meets the per-colonist
// target, else HerbalOrWorse. Unknown without the catalog's medicines.
func ColonistCareBase(items ItemFacts, resources domain.Fact[[]Amount], colonists int64, p MedicalReservePolicy) (domain.MedicalCare, bool) {
	rows, known := resources.Value()
	normal, err := items.MedicineAt(1)
	if !known || err != nil {
		return "", false
	}
	var industrial int64
	for _, row := range rows {
		if row.Resource == normal {
			industrial += row.Count
		}
	}
	if colonists > 0 && industrial >= colonists*p.TargetPerColonist {
		return domain.CareNormal, true
	}
	return domain.CareHerbal, true
}

// PrisonerCareBase is a prisoner's standing cap and whether a serious
// condition may raise it.
func PrisonerCareBase(p PrisonerFacts) (base domain.MedicalCare, raise bool) {
	if p.Executing || PrisonerHarvestQueued(p) {
		return domain.CareHerbal, false
	}
	mode, _ := p.CurrentInteraction.Value()
	switch mode {
	case domain.PrisonerInteractionRecruit, domain.PrisonerInteractionReduceResistance, domain.PrisonerInteractionConvert, domain.PrisonerInteractionEnslave:
		return domain.CareNormal, true
	}
	return domain.CareHerbal, true
}

// PrisonerHarvestQueued reports a queued bill whose recipe removes a clean
// natural part (a harvest) from the prisoner.
func PrisonerHarvestQueued(p PrisonerFacts) bool {
	ops, _ := p.Operations.Value()
	for _, queued := range p.QueuedRecipes {
		for _, op := range ops {
			if recipe, _ := op.Recipe.Value(); recipe == queued && op.Kind == SurgeryHarvest {
				return true
			}
		}
	}
	return false
}

// AnimalCareBase is an animal's standing cap: NormalOrWorse when bonded or
// trained in Release, else HerbalOrWorse; unknown without the bond read.
func AnimalCareBase(a UpkeepAnimal) (domain.MedicalCare, bool) {
	bonded, known := a.Bonded.Value()
	if !known {
		return "", false
	}
	combat := false
	for _, t := range a.Training {
		learned, _ := t.Learned.Value()
		combat = combat || t.Def == "Release" && learned
	}
	if bonded || combat {
		return domain.CareNormal, true
	}
	return domain.CareHerbal, true
}

// MedicalCareChanges are the care settings to write: every pawn whose known
// care differs from its cap. A pawn with unknown care, unknown cap inputs
// or an unknown census is left alone.
func MedicalCareChanges(f RoutineFacts, p MedicalReservePolicy) []domain.PawnSettings {
	var out []domain.PawnSettings
	add := func(id PawnID, current domain.Fact[string], want domain.MedicalCare, ok bool) {
		care, known := current.Value()
		if !ok || !known || domain.MedicalCare(care) == want {
			return
		}
		if s, err := domain.NewMedicalCareSetting(domain.PawnID(id), want); err == nil {
			out = append(out, s)
		}
	}
	if colonists, known := f.MedicalPawns.Value(); known {
		living := int64(0)
		for _, c := range colonists {
			if dead, dk := c.Dead.Value(); dk && !dead {
				living++
			}
		}
		if base, ok := ColonistCareBase(f.Items, f.Resources, living, p); ok {
			for _, c := range colonists {
				if dead, dk := c.Dead.Value(); !dk || dead {
					continue
				}
				want, ok := capped(base, true, c.Conditions, c.LifeThreatening)
				add(c.ID, c.Care, want, ok)
			}
		}
	}
	prisoners, _ := f.Prisoners.Value()
	for _, row := range prisoners {
		if dead, dk := row.Dead.Value(); !dk || dead {
			continue
		}
		base, raise := PrisonerCareBase(row)
		want, ok := capped(base, raise, row.Conditions, row.LifeThreatening)
		add(PawnID(row.Pawn), row.MedicalCare, want, ok)
	}
	guests, _ := f.Guests.Value()
	for _, g := range guests {
		want, ok := capped(domain.CareNormal, true, g.Conditions, g.LifeThreatening)
		add(g.ID, g.Care, want, ok)
	}
	animals, _ := f.AnimalUpkeep.Animals.Value()
	for _, a := range animals {
		base, ok := AnimalCareBase(a)
		if !ok {
			continue
		}
		want, ok := capped(base, true, a.Conditions, a.LifeThreatening)
		add(a.ID, a.Care, want, ok)
	}
	return out
}

// MedicalCareOwed is the review's MedicalCareOwed fact: a care change is
// owed. Unknown pawns owe nothing.
func MedicalCareOwed(f RoutineFacts, p MedicalReservePolicy) domain.Fact[bool] {
	return domain.Known(len(MedicalCareChanges(f, p)) > 0)
}

func finiteUnit(v float64) bool { return !math.IsNaN(v) && !math.IsInf(v, 0) && v >= 0 && v <= 1 }
