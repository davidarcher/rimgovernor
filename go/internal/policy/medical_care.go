package policy

import (
	"errors"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// CarePawn is a fresh native health assessment, independent of urgent tending.
type CarePawn struct {
	ID                                        PawnID
	Conditions                                domain.Fact[[]CareCondition]
	Dead, NeedsRest, NeedsTend, BadConditions domain.Fact[bool]
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
