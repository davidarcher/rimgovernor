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

// ConstructionTier is the rung of the construction ladder a blueprint or frame
// is built for (#2522). Absent means ungated vanilla construction.
type ConstructionTier int

const (
	TierSurvive ConstructionTier = iota
	TierSustain
	TierComfort
	TierProduce
	TierExpand
	TierSecure
)

func (t ConstructionTier) Valid() bool { return t >= TierSurvive && t <= TierSecure }

// WithTier attaches the construction tier. A non-empty target re-tiers that
// exact blueprint or frame (an exact-ID adoption, the set-tier operation); it
// must agree with any target the finishing skill already names.
func (a Action) WithTier(tier ConstructionTier, target string) (Action, error) {
	if a.kind != BuildingAction || !tier.Valid() || !nativeText(target, true) || a.constructionTarget != "" && target != "" && target != a.constructionTarget {
		return Action{}, errors.New("invalid construction tier")
	}
	a.tier = Known(tier)
	if target != "" {
		a.constructionTarget = target
	}
	return a, nil
}

func (a Action) Tier() Fact[ConstructionTier] { return a.tier }

// WithWallReplacement marks a building action as an in-place swap of the
// built wall at the cell (#2529): a vanilla replacement blueprint, the cell
// impassable at every tick of the swap. Native refuses when no built wall
// stands there, placement is refused, or the swap would drop a roof's last
// holder. It never names an existing blueprint or frame to adopt.
func (a Action) WithWallReplacement() (Action, error) {
	if a.kind != BuildingAction || a.constructionTarget != "" {
		return Action{}, errors.New("invalid wall replacement")
	}
	a.replaceWall = true
	return a, nil
}

func (a Action) ReplacesWall() bool { return a.replaceWall }
