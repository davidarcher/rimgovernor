package domain

import "errors"

// Tend is explicit intent. Neither pawn is drafted; native eligibility, current
// health facts and job availability are established at inspection, not here.
type Tend struct {
	doctor, patient PawnID
}

func NewTend(doctor, patient PawnID) (Tend, error) {
	if !validID(string(doctor)) || !validID(string(patient)) || doctor == patient {
		return Tend{}, errors.New("tend requires distinct valid doctor and patient identities")
	}
	return Tend{doctor: doctor, patient: patient}, nil
}

func (t Tend) Doctor() PawnID  { return t.doctor }
func (t Tend) Patient() PawnID { return t.patient }

func NewTendAction(id ActionID, tend Tend) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewTend(tend.doctor, tend.patient); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: TendAction, tend: tend}, nil
}

func (a Action) Tend() (Tend, bool) { return a.tend, a.kind == TendAction }
