package domain

import "errors"

const HackDesignationAction ActionKind = "hack_designation"

// HackDesignation toggles vanilla autohack; it does not force a hacking job.
type HackDesignation struct {
	target  string
	enabled bool
}

func NewHackDesignation(target string, enabled bool) (HackDesignation, error) {
	if !validID(target) {
		return HackDesignation{}, errors.New("hack designation requires an exact target")
	}
	return HackDesignation{target, enabled}, nil
}
func (h HackDesignation) Target() string { return h.target }
func (h HackDesignation) Enabled() bool  { return h.enabled }
func NewHackDesignationAction(id ActionID, h HackDesignation) (Action, error) {
	canonical, err := NewHackDesignation(h.target, h.enabled)
	if !validID(string(id)) || err != nil || canonical != h {
		return Action{}, errors.New("invalid hack designation action")
	}
	return Action{id: id, kind: HackDesignationAction, hackDesignation: h}, nil
}
func (a Action) HackDesignation() (HackDesignation, bool) {
	return a.hackDesignation, a.kind == HackDesignationAction
}
