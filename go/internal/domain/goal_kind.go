package domain

import "errors"

// GoalKind is one of the maintained outcomes CreateGoal lets the player
// force-activate. Every kind here is already an autopilot-managed goal with its
// own deficit assessment in the policy package (policy.EnsureFoodSupply and
// friends, whose GoalID constants these string values match exactly; see
// policy's PlayerGoalKindsMatchRoutineGoals test). CreateGoal invents no new
// outcome: it asserts, on explicit player direction, that one of these is in
// deficit right now, overriding whatever the autopilot's own review currently
// observes.
//
// The whitelist is the set of kinds a player may create. domain.Goal carries
// no free-form target dict, so per-goal configuration -- EnsureFoodSupply's food_days,
// MaintainResource's resource/quantity/deep_extraction and MaintainWaste's
// unwanted/bury -- is deliberately NOT carried here. Their equivalents
// (policy.RoutinePolicy's FoodTargetDays/FoodMinDays and ResourceTargets) are
// process-level operator configuration captured once when the routine reviewer
// is constructed, not per-world stored state the player can move, and
// MaintainWaste has no Go target at all (native authority owns waste
// eligibility; see policy.WasteItem.Eligible). Making any of them
// player-settable means converting RoutinePolicy from immutable process config
// into per-world stored config the routine review re-reads -- a change to the
// autopilot's own configuration model larger than this command, tracked
// separately. See go/README.md's G01.08 entry.
type GoalKind string

const (
	EnsureFoodSupplyGoal        GoalKind = "EnsureFoodSupply"
	EnsureInitialShelterGoal    GoalKind = "EnsureInitialShelter"
	EnsureFoodStorageGoal       GoalKind = "EnsureFoodStorage"
	EnsureCookingGoal           GoalKind = "EnsureCooking"
	EnsureTemperatureSafetyGoal GoalKind = "EnsureTemperatureSafety"
	EnsureBasicPowerGoal        GoalKind = "EnsureBasicPower"
	EnsureBasicDefenseGoal      GoalKind = "EnsureBasicDefense"
	MaintainWoodGoal            GoalKind = "MaintainWood"
	MaintainResourceGoal        GoalKind = "MaintainResource"
	MaintainWasteGoal           GoalKind = "MaintainWaste"
	EnsureDefensiveLayoutGoal   GoalKind = "EnsureDefensiveLayout"
)

// GoalKinds is every kind CreateGoal accepts, in declaration order.
// Callers must not retain or mutate the returned slice's backing
// array; it is freshly allocated per call.
func GoalKinds() []GoalKind {
	return []GoalKind{EnsureFoodSupplyGoal, EnsureInitialShelterGoal, EnsureFoodStorageGoal, EnsureCookingGoal,
		EnsureTemperatureSafetyGoal, EnsureBasicPowerGoal, EnsureBasicDefenseGoal, MaintainWoodGoal, MaintainResourceGoal, MaintainWasteGoal, EnsureDefensiveLayoutGoal}
}

// NewGoalKind validates one requested kind against the whitelist.
func NewGoalKind(name string) (GoalKind, error) {
	kind := GoalKind(name)
	if err := kind.Validate(); err != nil {
		return "", err
	}
	return kind, nil
}

func (k GoalKind) Validate() error {
	for _, known := range GoalKinds() {
		if k == known {
			return nil
		}
	}
	return errors.New("unsupported player goal kind")
}

// Set reports whether a kind was requested at all, the same absence test
// PopulationDirective.Set offers for its own optional Proposal field.
func (k GoalKind) Set() bool { return k != "" }

// Priority is the goal priority a player-created goal of this kind starts at:
// priority_class 2 for every kind, and 3
// for MaintainWaste alone. Goal priority is a scheduling class, not authority;
// see Goal.Validate for its permitted range.
func (k GoalKind) Priority() int {
	if k == MaintainWasteGoal || k == EnsureDefensiveLayoutGoal {
		return 3
	}
	return 2
}
