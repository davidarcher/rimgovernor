package domain

import "errors"

// BedUse is an immutable, comparable value: a one-shot patch of a
// humanlike bed's use -- its medical flag (Building_Bed.Medical on the
// native side), or, for a prisoners patch (#880), setting it for
// prisoners (a BuildingPatchIntent since #940; native revalidates at
// apply, #991), or, for a slaves patch (#1036), setting it for slaves. There is no pawn/Job involved -- see NativeBedUse.cs and
// bridge/bed_use.go.
type BedUse struct {
	thing     string
	medical   bool
	prisoners bool
	slaves    bool
}

func NewBedMedical(thing string, medical bool) (BedUse, error) {
	if !validID(thing) {
		return BedUse{}, errors.New("invalid bed medical identity")
	}
	return BedUse{thing: thing, medical: medical}, nil
}

// NewBedPrisoners sets one exact bed for prisoners (#880); there is no
// patch back to colonists.
func NewBedPrisoners(thing string) (BedUse, error) {
	b, err := NewBedMedical(thing, false)
	b.prisoners = err == nil
	return b, err
}

// NewBedSlaves sets one exact bed for slaves (#1036, Ideology); like the
// prisoners patch there is no patch back.
func NewBedSlaves(thing string) (BedUse, error) {
	b, err := NewBedMedical(thing, false)
	b.slaves = err == nil
	return b, err
}
func (b BedUse) Slaves() bool    { return b.slaves }
func (b BedUse) Thing() string   { return b.thing }
func (b BedUse) Medical() bool   { return b.medical }
func (b BedUse) Prisoners() bool { return b.prisoners }

func NewBedUseAction(id ActionID, b BedUse) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	canonical, err := NewBedMedical(b.thing, b.medical)
	if b.prisoners {
		canonical, err = NewBedPrisoners(b.thing)
	} else if b.slaves {
		canonical, err = NewBedSlaves(b.thing)
	}
	if err != nil || canonical != b {
		return Action{}, errors.New("invalid bed medical")
	}
	return Action{id: id, kind: BedUseAction, bedUse: b}, nil
}
func (a Action) BedUse() (BedUse, bool) {
	return a.bedUse, a.kind == BedUseAction
}
