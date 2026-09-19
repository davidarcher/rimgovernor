package domain

import "errors"

const HomeCoverageAction ActionKind = "home_coverage"

// HomeCoverage is explicit intent to extend the native Home area to cover the
// observed bounded batch of an autonomously owned facility's connected
// enclosed rooms, or a stockpile zone. ExtendHome restores missing cells
// under a shape and revision check. Native geometry and the pending deficit
// are established at inspection, not here.
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
