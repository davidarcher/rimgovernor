package domain

import "errors"

// WithFinishingSkill attaches a target's once-chosen construction setting.
// An existing target is an exact-ID adoption, never permission to replace it.
func (a Action) WithFinishingSkill(minimum int, target string) (Action, error) {
	if a.kind != BuildingAction || minimum < 0 || minimum > 1000 || !nativeText(target, true) {
		return Action{}, errors.New("invalid construction finishing skill")
	}
	a.finishingSkill, a.constructionTarget = Known(minimum), target
	return a, nil
}

func (a Action) FinishingSkill() Fact[int]  { return a.finishingSkill }
func (a Action) ConstructionTarget() string { return a.constructionTarget }
