package domain

import "errors"

const StripAction ActionKind = "strip"

// Strip places vanilla's Strip designation on one exact spawned pawn or
// corpse; colonists strip it through ordinary Hauling work. Who to
// strip is the caller's decision; native validates the target live.
type Strip struct {
	target string
}

func NewStrip(target string) (Strip, error) {
	if !validID(target) {
		return Strip{}, errors.New("strip requires a valid target identity")
	}
	return Strip{target: target}, nil
}

func (s Strip) Target() string { return s.target }

func NewStripAction(id ActionID, strip Strip) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewStrip(strip.target); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: StripAction, strip: strip}, nil
}

func (a Action) Strip() (Strip, bool) { return a.strip, a.kind == StripAction }
