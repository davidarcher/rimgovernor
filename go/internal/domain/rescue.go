package domain

import "errors"

// Rescue is explicit intent. Neither pawn is drafted; native eligibility,
// current position/bed facts and job availability are established at
// inspection, not here.
type Rescue struct {
	rescuer, patient PawnID
}

func NewRescue(rescuer, patient PawnID) (Rescue, error) {
	if !validID(string(rescuer)) || !validID(string(patient)) || rescuer == patient {
		return Rescue{}, errors.New("rescue requires distinct valid rescuer and patient identities")
	}
	return Rescue{rescuer: rescuer, patient: patient}, nil
}

func (r Rescue) Rescuer() PawnID { return r.rescuer }
func (r Rescue) Patient() PawnID { return r.patient }

func NewRescueAction(id ActionID, rescue Rescue) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewRescue(rescue.rescuer, rescue.patient); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: RescueAction, rescue: rescue}, nil
}

func (a Action) Rescue() (Rescue, bool) { return a.rescue, a.kind == RescueAction }
