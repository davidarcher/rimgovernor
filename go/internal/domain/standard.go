package domain

import "errors"

type ConcernID string
type MethodID string
type StandardStatus string

const (
	StandardOpen    StandardStatus = "active"
	StandardSettled StandardStatus = "satisfied"
	StandardVoided  StandardStatus = "invalidated"
)

type NeedState string

const (
	NeedUnknown   NeedState = "unknown"
	NeedDeficit   NeedState = "deficit"
	NeedRecovered NeedState = "recovered"
)

// Standard records a maintained outcome. Executable methods reference ordinary shared
// plans; their receipts, progress and uncertainty stay in those plans.
type Standard struct {
	ID               ConcernID
	Priority         int
	Snapshot         GenerationSnapshot
	Tick             Tick
	Episode          uint64 `json:"Epoch"`
	Status           StandardStatus
	Need             NeedState
	RecoveryObserved bool
	// Record is the goal's own durable intent, JSON the goal's planner
	// writes and reads (empty for most goals): what the world cannot show,
	// saved with the goal in the GovernorState blob.
	Record string `json:",omitempty"`
}

// MaxStandardRecord bounds Standard.Record in bytes.
const MaxStandardRecord = 4096

func NewStandard(id ConcernID, priority int, snapshot GenerationSnapshot, tick Tick) (Standard, error) {
	g := Standard{ID: id, Priority: priority, Snapshot: snapshot, Tick: tick, Status: StandardOpen, Need: NeedUnknown}
	return g, g.Validate()
}
func (g Standard) Validate() error {
	if !validID(string(g.ID)) || g.Priority < 0 || g.Priority > 4 || g.Tick < 0 || g.Snapshot.Validate() != nil {
		return errors.New("invalid standard")
	}
	if len(g.Record) > MaxStandardRecord {
		return errors.New("standard record exceeds bound")
	}
	switch g.Status {
	case StandardOpen, StandardSettled, StandardVoided:
	default:
		return errors.New("invalid standard status")
	}
	switch g.Need {
	case NeedUnknown, NeedDeficit, NeedRecovered:
	default:
		return errors.New("invalid standard need")
	}
	if g.Status == StandardSettled && (g.Need != NeedRecovered || !g.RecoveryObserved) {
		return errors.New("settled standard needs observed recovery")
	}
	return nil
}

// ReviewStandard invalidates captured work on world,
// direction or tick replacement. OpenWork is derived from linked shared plans,
// including cancelled actions whose effects are still unresolved. A deficit
// measured after a recovery starts a new Episode once no work is open:
// either the recovery was observed as satisfaction, or the previous review
// measured it while a plan's effects were still unresolved and the world has
// regressed since (a lamp removed behind a lit bench), so the settled
// episode's methods may be proposed again. Priority orders work only: an
// emergency or a pause vetoes proposals through the policy Safeguards (#1017).
// Projects are not goals (Project, ReviewProject).
func ReviewStandard(g Standard, current GenerationSnapshot, tick Tick, need NeedState, openWork bool) (Standard, error) {
	original := g
	if err := g.Validate(); err != nil {
		return g, err
	}
	if current.Validate() != nil || tick < 0 {
		return g, errors.New("invalid standard review scope")
	}
	switch need {
	case NeedUnknown, NeedDeficit, NeedRecovered:
	default:
		return g, errors.New("invalid observed need")
	}
	if g.Status == StandardVoided {
		return g, nil
	}
	if !g.Snapshot.sameColonyMap(current) || tick < g.Tick {
		g.Status = StandardVoided
		return g, nil
	}
	// Review revisions may advance without changing the player's direction.
	g.Snapshot = current
	g.Tick = tick
	if need == NeedUnknown {
		if g.Status == StandardSettled {
			g.Status = StandardOpen
		}
		g.Need = need
		return g, nil
	}
	if need == NeedDeficit && (g.RecoveryObserved || g.Need == NeedRecovered) && !openWork {
		if g.Episode == ^uint64(0) {
			return original, errors.New("standard episode exhausted")
		}
		g.Episode++
		g.RecoveryObserved = false
	}
	g.Need = need
	if need == NeedRecovered && !openWork {
		g.Status = StandardSettled
		g.RecoveryObserved = true
	} else {
		g.Status = StandardOpen
	}
	return g, nil
}

// Method binds a selected method to its original Episode and executable plan.
// Renewed deficits get a new Episode; the old plan remains available for readback.
type Method struct {
	// Owner is the Standard, Project or Incident id the method binds to.
	Owner   ConcernID `json:"Goal"`
	Episode uint64    `json:"Epoch"`
	Method  MethodID
	Plan    PlanID
}

func (m Method) Validate() error {
	if !validID(string(m.Owner)) || !validID(string(m.Method)) || !validID(string(m.Plan)) {
		return errors.New("invalid method")
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
