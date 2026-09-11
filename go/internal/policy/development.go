package policy

import (
	"errors"
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// GoalID identifies a maintained need, independently of any one executable plan.
type GoalID = domain.GoalID
type GoalSource = domain.GoalSource

const (
	AutopilotGoal = domain.AutopilotGoal
	PlayerGoal    = domain.PlayerGoal
	AdviserGoal   = domain.AdviserGoal
)

type DevelopmentGoal struct {
	ID                          GoalID
	Source                      GoalSource
	Priority                    int
	Deficit                     domain.Fact[float64]
	Cancelled, Blocked, Comfort bool
}

// Commitment refers to existing shared action progress, never a receipt-derived
// claim of completion. All accepted player projects consume optional capacity.
type Commitment struct {
	Goal     GoalID
	Source   GoalSource
	Priority int
	Progress domain.Progress
}

type DevelopmentReason string

const (
	DevelopmentCancelled         DevelopmentReason = "cancelled"
	DevelopmentAdviser           DevelopmentReason = "adviser"
	DevelopmentEmergency         DevelopmentReason = "emergency"
	DevelopmentStartup           DevelopmentReason = "startup_survival"
	DevelopmentBlocked           DevelopmentReason = "blocked"
	DevelopmentCommitted         DevelopmentReason = "existing_commitment"
	DevelopmentWorkersUnknown    DevelopmentReason = "workers_unknown"
	DevelopmentNoWorkers         DevelopmentReason = "no_workers"
	DevelopmentUnknown           DevelopmentReason = "deficit_unknown"
	DevelopmentCapacity          DevelopmentReason = "capacity_committed"
	DevelopmentMethodUnavailable DevelopmentReason = "method_unavailable"
)

type DevelopmentRow struct {
	Goal                GoalID
	Score               float64
	Deficit             domain.Fact[float64]
	WaitingSince        domain.Tick
	Selected, Committed bool
	Reason              DevelopmentReason
}

// DevelopmentState is a value snapshot owned by the review caller. Context and
// direction changes or tick rewinds discard age and selection history.
type DevelopmentState struct {
	Snapshot  domain.GenerationSnapshot
	Tick      domain.Tick
	Workers   domain.Fact[int]
	Capacity  int
	Committed []GoalID
	Rows      []DevelopmentRow
}

type DevelopmentRequest struct {
	Snapshot    domain.GenerationSnapshot
	Tick        domain.Tick
	Workers     domain.Fact[int]
	Limit       int
	Goals       []DevelopmentGoal
	Commitments []Commitment
	Previous    DevelopmentState
}

func validGoal(id GoalID, source GoalSource, priority int) bool {
	return validResource(Resource(id)) && (source == AutopilotGoal || source == PlayerGoal || source == AdviserGoal) && priority >= 0 && priority <= 4
}

// RankDevelopment ports development_priorities.arbitrate. It grants selection
// slots only; native admission, shared resource reservations and Hands still apply.
func RankDevelopment(r DevelopmentRequest) (DevelopmentState, error) {
	if r.Snapshot.Validate() != nil || r.Tick < 0 || r.Limit < 1 || r.Limit > 8 || len(r.Goals) > 256 || len(r.Commitments) > 4096 {
		return DevelopmentState{}, errors.New("invalid development review")
	}
	workers, knownWorkers := r.Workers.Value()
	if knownWorkers && (workers < 0 || workers > 4096) {
		return DevelopmentState{}, errors.New("invalid worker count")
	}
	result := DevelopmentState{Snapshot: r.Snapshot, Tick: r.Tick, Workers: r.Workers, Capacity: min(r.Limit, workers)}
	old := map[GoalID]DevelopmentRow{}
	if sameWorld(r.Previous.Snapshot, r.Snapshot) && r.Previous.Snapshot.Direction == r.Snapshot.Direction && r.Tick >= r.Previous.Tick {
		for _, row := range r.Previous.Rows {
			if row.WaitingSince < 0 || row.WaitingSince > r.Previous.Tick {
				return DevelopmentState{}, errors.New("invalid development history")
			}
			if _, duplicate := old[row.Goal]; duplicate {
				return DevelopmentState{}, errors.New("duplicate development history")
			}
			old[row.Goal] = row
		}
	}
	committed := map[GoalID]bool{}
	seenActions := map[domain.ActionID]bool{}
	for _, c := range r.Commitments {
		v := c.Progress.View()
		if !validGoal(c.Goal, c.Source, c.Priority) || v.Stage == "" || seenActions[v.Action] {
			return DevelopmentState{}, errors.New("invalid development commitment")
		}
		seenActions[v.Action] = true
		if c.Source == AdviserGoal || c.Source != PlayerGoal && c.Priority < 3 {
			continue
		}
		// Unknown effects retain capacity even after cancellation. A terminal
		// observed failure/absence releases it; a command receipt never does.
		if v.Unresolved || v.Stage == domain.Pending || v.Stage == domain.Prepared || v.Stage == domain.Dispatched || v.Stage == domain.AwaitingObservation {
			committed[c.Goal] = true
		}
	}
	for id := range committed {
		result.Committed = append(result.Committed, id)
	}
	sort.Slice(result.Committed, func(i, j int) bool { return result.Committed[i] < result.Committed[j] })
	emergency, startup := false, false
	seen := map[GoalID]bool{}
	for _, g := range r.Goals {
		fraction, known := g.Deficit.Value()
		if !validGoal(g.ID, g.Source, g.Priority) || seen[g.ID] || known && (math.IsNaN(fraction) || math.IsInf(fraction, 0) || fraction < 0 || fraction > 1) {
			return DevelopmentState{}, errors.New("invalid development goal")
		}
		seen[g.ID] = true
		emergency = emergency || g.Priority < 2
		startup = startup || g.Priority < 3
	}
	for _, g := range r.Goals {
		if g.Priority < 3 {
			continue
		}
		previous, exists := old[g.ID]
		since := r.Tick
		if exists {
			since = previous.WaitingSince
		}
		fraction, known := g.Deficit.Value()
		score := 100*fraction + float64(r.Tick-since)/2500
		if g.Source == PlayerGoal {
			score += 100
		}
		if previous.Selected {
			score += 20
		}
		row := DevelopmentRow{Goal: g.ID, Score: math.RoundToEven(score*1000) / 1000, Deficit: g.Deficit, WaitingSince: since, Committed: committed[g.ID]}
		switch {
		case g.Cancelled:
			row.Reason = DevelopmentCancelled
		case g.Source == AdviserGoal:
			row.Reason = DevelopmentAdviser
		case emergency:
			row.Reason = DevelopmentEmergency
		case g.Comfort && startup:
			row.Reason = DevelopmentStartup
		case g.Blocked:
			row.Reason = DevelopmentBlocked
		case row.Committed:
			row.Reason = DevelopmentCommitted
		case !knownWorkers:
			row.Reason = DevelopmentWorkersUnknown
		case workers == 0:
			row.Reason = DevelopmentNoWorkers
		case !known:
			row.Reason = DevelopmentUnknown
		}
		if row.Committed {
			row.WaitingSince = r.Tick
		}
		result.Rows = append(result.Rows, row)
	}
	sort.Slice(result.Rows, func(i, j int) bool {
		a, b := result.Rows[i], result.Rows[j]
		if a.Score != b.Score {
			return a.Score > b.Score
		}
		return a.Goal < b.Goal
	})
	free := max(0, result.Capacity-len(committed))
	for i := range result.Rows {
		row := &result.Rows[i]
		if row.Reason != "" {
			continue
		}
		if free > 0 {
			row.Selected = true
			free--
		} else {
			row.Reason = DevelopmentCapacity
		}
	}
	return result, nil
}

// YieldDevelopment lets a refused or waiting method hand its unused slot to
// the next eligible goal in this same review, without inflating waiting age.
func YieldDevelopment(state DevelopmentState, goal GoalID) DevelopmentState {
	state.Rows = append([]DevelopmentRow(nil), state.Rows...)
	state.Committed = append([]GoalID(nil), state.Committed...)
	for i := range state.Rows {
		if state.Rows[i].Goal != goal || !state.Rows[i].Selected {
			continue
		}
		state.Rows[i].Selected = false
		state.Rows[i].Reason = DevelopmentMethodUnavailable
		for j := range state.Rows {
			if state.Rows[j].Reason == DevelopmentCapacity {
				state.Rows[j].Selected = true
				state.Rows[j].Reason = ""
				break
			}
		}
		break
	}
	return state
}
