package store

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Nullable quantities preserve known zero separately from unavailable facts.
type RoutineDevelopment struct {
	Snapshot  domain.GenerationSnapshot
	Tick      domain.Tick
	Workers   *int
	Labor     map[policy.WorkType]int `json:",omitempty"`
	Capacity  int
	Committed []domain.GoalID
	Rows      []RoutineDevelopmentRow
	Partial   bool `json:",omitempty"`
	// Auto and the fields below (#649) are absent from explicit-mode
	// records written before automatic admission existed.
	Auto         bool                        `json:",omitempty"`
	Census       *[]policy.DevelopmentWorker `json:",omitempty"`
	Holds        []policy.DevelopmentHold    `json:",omitempty"`
	StageHold    bool                        `json:",omitempty"`
	Yields       int                         `json:",omitempty"`
	Continuation string                      `json:",omitempty"`
	Unused       *int                        `json:",omitempty"`
	Limiting     policy.DevelopmentReason    `json:",omitempty"`
}
type RoutineDevelopmentRow struct {
	Goal                domain.GoalID
	Score               float64
	Deficit             *float64
	WaitingSince        domain.Tick
	Selected, Committed bool
	Reason              policy.DevelopmentReason
	Bottleneck          policy.WorkType      `json:",omitempty"`
	Risk                *float64             `json:",omitempty"`
	Idle                bool                 `json:",omitempty"`
	Granted             bool                 `json:",omitempty"`
	LaborIdleSince      *domain.Tick         `json:",omitempty"`
	LaborEvidence       policy.LaborEvidence `json:",omitempty"`
	Labor               policy.LaborProfile  `json:",omitempty"`
}

func developmentRecord(s policy.DevelopmentState) RoutineDevelopment {
	r := RoutineDevelopment{Snapshot: s.Snapshot, Tick: s.Tick, Capacity: s.Capacity, Committed: append([]domain.GoalID(nil), s.Committed...), Partial: s.Partial,
		Auto: s.Auto, Holds: append([]policy.DevelopmentHold(nil), s.Holds...), StageHold: s.StageHold, Yields: s.Yields, Continuation: s.Continuation, Limiting: s.Limiting}
	if v, k := s.Workers.Value(); k {
		r.Workers = &v
	}
	if v, k := s.Census.Value(); k {
		census := append([]policy.DevelopmentWorker{}, v...)
		r.Census = &census
	}
	if v, k := s.Unused.Value(); k {
		r.Unused = &v
	}
	if labor, k := s.Labor.Value(); k {
		r.Labor = map[policy.WorkType]int{}
		for w, n := range labor {
			r.Labor[w] = n
		}
	}
	for _, row := range s.Rows {
		v := RoutineDevelopmentRow{Goal: row.Goal, Score: row.Score, WaitingSince: row.WaitingSince, Selected: row.Selected, Committed: row.Committed, Reason: row.Reason, Bottleneck: row.Bottleneck, Idle: row.Idle, Granted: row.Granted, LaborEvidence: row.LaborEvidence, Labor: row.Labor}
		if deficit, k := row.Deficit.Value(); k {
			v.Deficit = &deficit
		}
		if risk, k := row.Risk.Value(); k {
			v.Risk = &risk
		}
		if since, k := row.LaborIdleSince.Value(); k {
			v.LaborIdleSince = &since
		}
		r.Rows = append(r.Rows, v)
	}
	return r
}

// State rebuilds the policy ranking this record persisted.
func (r RoutineDevelopment) State() policy.DevelopmentState {
	s := policy.DevelopmentState{Snapshot: r.Snapshot, Tick: r.Tick, Capacity: r.Capacity, Committed: append([]domain.GoalID(nil), r.Committed...), Partial: r.Partial,
		Auto: r.Auto, Holds: append([]policy.DevelopmentHold(nil), r.Holds...), StageHold: r.StageHold, Yields: r.Yields, Continuation: r.Continuation, Limiting: r.Limiting}
	if r.Workers != nil {
		s.Workers = domain.Known(*r.Workers)
	}
	if r.Census != nil {
		s.Census = domain.Known(append([]policy.DevelopmentWorker{}, (*r.Census)...))
	}
	if r.Unused != nil {
		s.Unused = domain.Known(*r.Unused)
	}
	if r.Labor != nil {
		labor := map[policy.WorkType]int{}
		for w, n := range r.Labor {
			labor[w] = n
		}
		s.Labor = domain.Known(labor)
	}
	for _, row := range r.Rows {
		v := policy.DevelopmentRow{Goal: row.Goal, Score: row.Score, WaitingSince: row.WaitingSince, Selected: row.Selected, Committed: row.Committed, Reason: row.Reason, Bottleneck: row.Bottleneck, Idle: row.Idle, Granted: row.Granted, LaborEvidence: row.LaborEvidence, Labor: row.Labor}
		if row.Deficit != nil {
			v.Deficit = domain.Known(*row.Deficit)
		}
		if row.Risk != nil {
			v.Risk = domain.Known(*row.Risk)
		}
		if row.LaborIdleSince != nil {
			v.LaborIdleSince = domain.Known(*row.LaborIdleSince)
		}
		s.Rows = append(s.Rows, v)
	}
	return s
}
