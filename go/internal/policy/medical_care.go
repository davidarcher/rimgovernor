package policy

import (
	"errors"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// CarePawn is a fresh native health assessment, independent of urgent tending.
type CarePawn struct {
	ID                                        PawnID
	Care                                      domain.Fact[string]
	LifeThreatening                           domain.Fact[bool]
	Conditions                                domain.Fact[[]CareCondition]
	Dead, NeedsRest, NeedsTend, BadConditions domain.Fact[bool]
	// Surgery facts (#1161) ride on the same health read. Native computes
	// every value through vanilla; nothing here is recomputed in Go.
	MissingParts domain.Fact[[]MissingPart]
	Operations   domain.Fact[[]SurgeryOperation]
}

// MissingPart is a missing or destroyed body part at its common missing
// ancestor; its descendants are implied.
type MissingPart struct {
	PartIndex, ParentIndex     domain.Fact[int]
	PartDefName, ParentDefName domain.Fact[string]
	Vital                      domain.Fact[bool]
}

// SurgeryKind classifies an operation the way the surgery planner ranks it.
type SurgeryKind string

const (
	SurgeryUnknown  SurgeryKind = ""
	SurgeryRestore  SurgeryKind = "restore"
	SurgeryCure     SurgeryKind = "cure"
	SurgeryAmputate SurgeryKind = "amputate"
	SurgeryInstall  SurgeryKind = "install"
	SurgeryHarvest  SurgeryKind = "harvest"
	SurgeryOther    SurgeryKind = "other"
)

// SurgeryOperation is one available medical recipe on one target part.
// SuccessChance is vanilla's, for the best eligible doctor, bed and medicine;
// it is unknown when no doctor is eligible.
type SurgeryOperation struct {
	Recipe, PartDefName         domain.Fact[string]
	PartIndex                   domain.Fact[int]
	Kind                        SurgeryKind
	SuccessChance               domain.Fact[float64]
	EligibleDoctors             domain.Fact[int]
	IngredientsOnMap, Violation domain.Fact[bool]
	Lethal                      domain.Fact[bool]
}

// CareCondition retains native disease evidence without estimating missing values.
// Rates are instantaneous per game day, and may be negative during recovery.
type CareCondition struct {
	DefName                                            domain.Fact[string]
	Severity, SeverityPerDay, Immunity, ImmunityPerDay domain.Fact[float64]
	Tended                                             domain.Fact[bool]
	TendQuality                                        domain.Fact[float64]
}

// MedicalCareHistory retains unresolved patient identities across reviews and
// restart. Missing patients and deaths do not constitute observed recovery.
type MedicalCareHistory struct {
	Resting           []DiseaseRest `json:",omitempty"`
	CensusKnown       bool
	Patients, Unknown []PawnID
}

func (h MedicalCareHistory) Validate() error {
	if len(h.Patients)+len(h.Unknown) > 256 {
		return errors.New("medical care history exceeds patient bound")
	}
	seen := map[PawnID]bool{}
	for _, ids := range [][]PawnID{h.Patients, h.Unknown} {
		for i, id := range ids {
			if !foodID(string(id)) || seen[id] || i > 0 && ids[i-1] >= id {
				return errors.New("invalid medical care patient history")
			}
			seen[id] = true
		}
	}
	return validateDiseaseRest(h.Resting)
}

func (h MedicalCareHistory) Recovered() domain.Fact[bool] {
	if len(h.Patients) > 0 || len(h.Resting) > 0 {
		return domain.Known(false)
	}
	if !h.CensusKnown || len(h.Unknown) > 0 {
		return domain.Unknown[bool]()
	}
	return domain.Known(true)
}

func ReviewMedicalCare(observed domain.Fact[[]CarePawn], previous MedicalCareHistory) (MedicalCareHistory, error) {
	if err := previous.Validate(); err != nil {
		return MedicalCareHistory{}, err
	}
	tracked := map[PawnID]bool{}
	for _, ids := range [][]PawnID{previous.Patients, previous.Unknown} {
		for _, id := range ids {
			tracked[id] = true
		}
	}
	rows, known := observed.Value()
	if len(rows) > 256 {
		return MedicalCareHistory{}, errors.New("medical care census exceeds bound")
	}
	resting, err := ReviewDiseaseRest(observed, previous.Resting)
	if err != nil {
		return MedicalCareHistory{}, err
	}
	r := MedicalCareHistory{CensusKnown: known, Resting: resting}
	seen := map[PawnID]bool{}
	for _, pawn := range rows {
		if !foodID(string(pawn.ID)) || seen[pawn.ID] {
			return MedicalCareHistory{}, errors.New("invalid medical care census")
		}
		seen[pawn.ID] = true
		dead, dk := pawn.Dead.Value()
		if dk && dead {
			continue
		}
		delete(tracked, pawn.ID)
		rest, rk := pawn.NeedsRest.Value()
		_, tk := pawn.NeedsTend.Value()
		bad, bk := pawn.BadConditions.Value()
		if !dk || !rk || !tk || !bk {
			r.Unknown = append(r.Unknown, pawn.ID)
		} else if rest || bad {
			r.Patients = append(r.Patients, pawn.ID)
		}
	}
	for id := range tracked {
		r.Unknown = append(r.Unknown, id)
	}
	sort.Slice(r.Patients, func(i, j int) bool { return r.Patients[i] < r.Patients[j] })
	sort.Slice(r.Unknown, func(i, j int) bool { return r.Unknown[i] < r.Unknown[j] })
	return r, r.Validate()
}

// MaintainMedicalReserves' Phase is the step a review leaves owed:
// care for the sick (rest and a hospital bed) before the medicine stock.
const (
	MedicalCare     Phase = "care"
	MedicalReserves Phase = "reserves"
)

// Restocks reports whether the medicine bill and herb harvest may act. They
// run in both phases: a sick colonist never pauses restocking. Only the
// hospital and medicine-tier steps are care-only.
func (p Phase) Restocks() bool { return p != "" }
