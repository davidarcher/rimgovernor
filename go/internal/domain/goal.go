package domain

import "errors"

type ConcernID string
type MethodID string
type GoalStatus string

const (
	GoalActive      GoalStatus = "active"
	GoalSatisfied   GoalStatus = "satisfied"
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
	ID               ConcernID
	Priority         int
	Snapshot         GenerationSnapshot
	Tick             Tick
	Epoch            uint64
	Status           GoalStatus
	Need             NeedState
	RecoveryObserved bool
	// Record is the goal's own durable intent, JSON the goal's planner
	// writes and reads (empty for most goals): what the world cannot show,
	// saved with the goal in the GovernorState blob.
	Record string `json:",omitempty"`
}

// MaxGoalRecord bounds Goal.Record in bytes.
const MaxGoalRecord = 4096

func NewGoal(id ConcernID, priority int, snapshot GenerationSnapshot, tick Tick) (Goal, error) {
	g := Goal{ID: id, Priority: priority, Snapshot: snapshot, Tick: tick, Status: GoalActive, Need: NeedUnknown}
	return g, g.Validate()
}
func (g Goal) Validate() error {
	if !validID(string(g.ID)) || g.Priority < 0 || g.Priority > 4 || g.Tick < 0 || g.Snapshot.Validate() != nil {
		return errors.New("invalid maintained goal")
	}
	if len(g.Record) > MaxGoalRecord {
		return errors.New("goal record exceeds bound")
	}
	switch g.Status {
	case GoalActive, GoalSatisfied, GoalInvalidated:
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

// ReviewGoal invalidates captured work on world,
// direction or tick replacement. OpenWork is derived from linked shared plans,
// including cancelled actions whose effects are still unresolved. A deficit
// measured after a recovery starts a new method epoch once no work is open:
// either the recovery was observed as satisfaction, or the previous review
// measured it while a plan's effects were still unresolved and the world has
// regressed since (a lamp removed behind a lit bench), so the settled
// epoch's methods may be proposed again. Priority orders work only: an
// emergency or a pause vetoes proposals through the policy Safeguards (#1017).
// Projects are not goals (Project, ReviewProject).
func ReviewGoal(g Goal, current GenerationSnapshot, tick Tick, need NeedState, openWork bool) (Goal, error) {
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
	if g.Status == GoalInvalidated {
		return g, nil
	}
	if !g.Snapshot.sameColonyMap(current) || tick < g.Tick {
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
	if need == NeedDeficit && (g.RecoveryObserved || g.Need == NeedRecovered) && !openWork {
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
	} else {
		g.Status = GoalActive
	}
	return g, nil
}

// GoalMethod binds a selected method to its original epoch and executable plan.
// Renewed deficits get a new epoch; the old plan remains available for readback.
type GoalMethod struct {
	Goal   ConcernID
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
	}
	return false
}

// PopulationTarget is the bot's own colony size target (#1032). The colony
// grows toward it only as fast as policy.JoinerCapacity's bed and food gates
// allow, so in practice the target rises with the colony's means.
const PopulationTarget = 100
