package domain

import "errors"

// BedMedical is an immutable, comparable value: a one-shot patch of a
// humanlike bed's use -- its medical flag (Building_Bed.Medical on the
// native side), or, for a prisoners patch (#880), setting it for
// prisoners -- CAS-gated by an already-observed exact snapshot token the
// same way BuildingTemperature gates a setpoint. The token covers both
// flags and the bed's owner set, since either setter drops every owner;
// there is no pawn/Job involved -- see NativeBedMedical.cs and
// bridge/bed_medical.go.
type BedMedical struct {
	thing     string
	medical   bool
	prisoners bool
	before    string
}

func NewBedMedical(thing string, medical bool, before string) (BedMedical, error) {
	if !validID(thing) || !validID(before) {
		return BedMedical{}, errors.New("invalid bed medical identity")
	}
	return BedMedical{thing: thing, medical: medical, before: before}, nil
}

// NewBedPrisoners sets one exact bed for prisoners (#880); there is no
// patch back to colonists.
func NewBedPrisoners(thing string, before string) (BedMedical, error) {
	b, err := NewBedMedical(thing, false, before)
	b.prisoners = err == nil
	return b, err
}
func (b BedMedical) Thing() string       { return b.thing }
func (b BedMedical) Medical() bool       { return b.medical }
func (b BedMedical) Prisoners() bool     { return b.prisoners }
func (b BedMedical) BeforeToken() string { return b.before }

func NewBedMedicalAction(id ActionID, b BedMedical) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	canonical, err := NewBedMedical(b.thing, b.medical, b.before)
	if b.prisoners {
		canonical, err = NewBedPrisoners(b.thing, b.before)
	}
	if err != nil || canonical != b {
		return Action{}, errors.New("invalid bed medical")
	}
	return Action{id: id, kind: BedMedicalAction, bedMedical: b}, nil
}
func (a Action) BedMedical() (BedMedical, bool) {
	return a.bedMedical, a.kind == BedMedicalAction
}
