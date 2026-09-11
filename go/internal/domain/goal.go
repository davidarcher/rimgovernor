package domain

import "errors"

type GoalID string
type MethodID string
type GoalSource string

const (
	AutopilotGoal GoalSource = "autopilot"
	PlayerGoal    GoalSource = "player"
	AdviserGoal   GoalSource = "adviser"
)

type GoalStatus string

const (
	GoalActive      GoalStatus = "active"
	GoalSatisfied   GoalStatus = "satisfied"
	GoalSuspended   GoalStatus = "suspended"
	GoalCancelled   GoalStatus = "cancelled"
	GoalInvalidated GoalStatus = "invalidated"
)

type NeedState string

const (
	NeedUnknown   NeedState = "unknown"
	NeedDeficit   NeedState = "deficit"
	NeedRecovered NeedState = "recovered"
)

// Goal records a maintained outcome. Executable methods reference ordinary shared
// plans; their receipts, progress and uncertainty stay in those plans.
type Goal struct {
	ID               GoalID
	Source           GoalSource
	Priority         int
	Snapshot         GenerationSnapshot
	Tick             Tick
	Epoch            uint64
	Status           GoalStatus
	Need             NeedState
	RecoveryObserved bool
}

func NewGoal(id GoalID, source GoalSource, priority int, snapshot GenerationSnapshot, tick Tick) (Goal, error) {
	g := Goal{ID: id, Source: source, Priority: priority, Snapshot: snapshot, Tick: tick, Status: GoalActive, Need: NeedUnknown}
	return g, g.Validate()
}
func (g Goal) Validate() error {
	if !validID(string(g.ID)) || g.Priority < 0 || g.Priority > 4 || g.Tick < 0 || g.Snapshot.Validate() != nil {
		return errors.New("invalid maintained goal")
	}
	switch g.Source {
	case AutopilotGoal, PlayerGoal, AdviserGoal:
	default:
		return errors.New("invalid goal source")
	}
	switch g.Status {
	case GoalActive, GoalSatisfied, GoalSuspended, GoalCancelled, GoalInvalidated:
	default:
		return errors.New("invalid goal status")
	}
	switch g.Need {
	case NeedUnknown, NeedDeficit, NeedRecovered:
	default:
		return errors.New("invalid goal need")
	}
	if g.Status == GoalSatisfied && (g.Need != NeedRecovered || !g.RecoveryObserved) {
		return errors.New("satisfied goal needs observed recovery")
	}
	return nil
}

// ReviewGoal preserves cancellation and invalidates captured work on world,
// direction or tick replacement. OpenWork is derived from linked shared plans,
// including cancelled actions whose effects are still unresolved.
func ReviewGoal(g Goal, current GenerationSnapshot, tick Tick, need NeedState, emergency, openWork bool) (Goal, error) {
	original := g
	if err := g.Validate(); err != nil {
		return g, err
	}
	if current.Validate() != nil || tick < 0 {
		return g, errors.New("invalid goal review scope")
	}
	switch need {
	case NeedUnknown, NeedDeficit, NeedRecovered:
	default:
		return g, errors.New("invalid observed need")
	}
	if g.Status == GoalCancelled || g.Status == GoalInvalidated {
		return g, nil
	}
	if !g.Snapshot.sameWorld(current) || g.Snapshot.Direction != current.Direction || tick < g.Tick {
		g.Status = GoalInvalidated
		return g, nil
	}
	// Review revisions may advance without changing the player's direction.
	g.Snapshot = current
	g.Tick = tick
	if need == NeedUnknown {
		if g.Status == GoalSatisfied {
			g.Status = GoalActive
		}
		g.Need = need
		return g, nil
	}
	if need == NeedDeficit && g.RecoveryObserved && !openWork {
		if g.Epoch == ^uint64(0) {
			return original, errors.New("goal epoch exhausted")
		}
		g.Epoch++
		g.RecoveryObserved = false
	}
	g.Need = need
	if need == NeedRecovered && !openWork {
		g.Status = GoalSatisfied
		g.RecoveryObserved = true
	} else if emergency && g.Priority >= 2 {
		g.Status = GoalSuspended
	} else {
		g.Status = GoalActive
	}
	return g, nil
}

func CancelGoal(g Goal) (Goal, error) {
	if err := g.Validate(); err != nil {
		return g, err
	}
	g.Status = GoalCancelled
	return g, nil
}

// GoalMethod binds a selected method to its original epoch and executable plan.
// Renewed deficits get a new epoch; the old plan remains available for readback.
type GoalMethod struct {
	Goal   GoalID
	Epoch  uint64
	Method MethodID
	Plan   PlanID
}

func (m GoalMethod) Validate() error {
	if !validID(string(m.Goal)) || !validID(string(m.Method)) || !validID(string(m.Plan)) {
		return errors.New("invalid goal method")
	}
	return nil
}

func GoalWorkOpen(progress []Progress) bool {
	for _, p := range progress {
		v := p.View()
		if v.Stage == "" || v.Unresolved || v.Stage == Pending || v.Stage == Prepared || v.Stage == Dispatched || v.Stage == AwaitingObservation {
			return true
		}
		if p.draftCleanupOutstanding() {
			return true
		}
	}
	return false
}
