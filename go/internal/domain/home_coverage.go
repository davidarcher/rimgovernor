package domain

import "errors"

const HomeCoverageAction ActionKind = "home_coverage"

// HomeCoverage is explicit intent to extend the native Home area to cover the
// exact bounded footprint of one already-observed autonomously owned
// facility or stockpile zone. It reuses the native ExtendHome operation, the
// same one the legacy JSON home/upkeep_home tool drives: native only adds
// cells missing from Home within the target's exact scope, never overriding
// a player or pre-observation exclusion. Native reachability, current
// geometry and pending deficit are established at inspection, not here.
type HomeCoverage struct {
	target string
	shape  string
}

func NewHomeCoverage(target, shape string) (HomeCoverage, error) {
	if !validID(target) || !validID(shape) {
		return HomeCoverage{}, errors.New("home coverage requires a valid target and shape token")
	}
	return HomeCoverage{target: target, shape: shape}, nil
}

func (h HomeCoverage) Target() string { return h.target }
func (h HomeCoverage) Shape() string  { return h.shape }

func NewHomeCoverageAction(id ActionID, coverage HomeCoverage) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewHomeCoverage(coverage.target, coverage.shape); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: HomeCoverageAction, homeCoverage: coverage}, nil
}

func (a Action) HomeCoverage() (HomeCoverage, bool) {
	return a.homeCoverage, a.kind == HomeCoverageAction
}
