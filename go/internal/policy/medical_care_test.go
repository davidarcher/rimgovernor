package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func carePawn(id PawnID, bad bool) CarePawn {
	return CarePawn{ID: id, Dead: domain.Known(false), NeedsRest: domain.Known(false), NeedsTend: domain.Known(false), BadConditions: domain.Known(bad)}
}

func TestMedicalCareLongitudinalEvidence(t *testing.T) {
	h := MedicalCareHistory{}
	for _, step := range []struct {
		name              string
		input             domain.Fact[[]CarePawn]
		patients, unknown []PawnID
		recovered         domain.Fact[bool]
	}{
		{"chronic and healthy", domain.Known([]CarePawn{carePawn("healthy", false), carePawn("chronic", true)}), []PawnID{"chronic"}, nil, domain.Known(false)},
		{"missing tracked patient", domain.Known([]CarePawn{carePawn("healthy", false)}), nil, []PawnID{"chronic"}, domain.Unknown[bool]()},
		{"unavailable census", domain.Unknown[[]CarePawn](), nil, []PawnID{"chronic"}, domain.Unknown[bool]()},
		{"dead is not recovery", domain.Known([]CarePawn{{ID: "chronic", Dead: domain.Known(true)}}), nil, []PawnID{"chronic"}, domain.Unknown[bool]()},
		{"fresh healed patient", domain.Known([]CarePawn{carePawn("chronic", false)}), nil, nil, domain.Known(true)},
		{"renewed condition", domain.Known([]CarePawn{carePawn("chronic", true)}), []PawnID{"chronic"}, nil, domain.Known(false)},
	} {
		t.Run(step.name, func(t *testing.T) {
			var err error
			h, err = ReviewMedicalCare(step.input, h)
			if err != nil || !reflect.DeepEqual(h.Patients, step.patients) || !reflect.DeepEqual(h.Unknown, step.unknown) || h.Recovered() != step.recovered {
				t.Fatal(h, err)
			}
		})
	}
}

func TestMedicalCareMissingFieldsAndRest(t *testing.T) {
	for _, change := range []func(*CarePawn){
		func(p *CarePawn) { p.Dead = domain.Unknown[bool]() },
		func(p *CarePawn) { p.NeedsRest = domain.Unknown[bool]() },
		func(p *CarePawn) { p.NeedsTend = domain.Unknown[bool]() },
		func(p *CarePawn) { p.BadConditions = domain.Unknown[bool]() },
	} {
		p := carePawn("p", false)
		change(&p)
		h, err := ReviewMedicalCare(domain.Known([]CarePawn{p}), MedicalCareHistory{})
		if err != nil || len(h.Unknown) != 1 || h.Recovered() != domain.Unknown[bool]() {
			t.Fatal(h, err)
		}
	}
	p := carePawn("rest", false)
	p.NeedsRest = domain.Known(true)
	h, err := ReviewMedicalCare(domain.Known([]CarePawn{p, {ID: "unreadable"}}), MedicalCareHistory{})
	if err != nil || h.Recovered() != domain.Known(false) {
		t.Fatal("known patient masked by unknown neighbor", h, err)
	}
	for _, ids := range [][]PawnID{{"a", "a"}, {"z", "a"}, {""}} {
		if (MedicalCareHistory{Patients: ids}).Validate() == nil {
			t.Fatal("invalid history accepted", ids)
		}
	}
	if _, err = ReviewMedicalCare(domain.Known([]CarePawn{p, p}), MedicalCareHistory{}); err == nil {
		t.Fatal("duplicate patient accepted")
	}
}
