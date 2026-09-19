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
}
type RoutineDevelopmentRow struct {
	Goal                domain.GoalID
	Score               float64
	Deficit             *float64
	WaitingSince        domain.Tick
	Selected, Committed bool
	Reason              policy.DevelopmentReason
	Bottleneck          policy.WorkType `json:",omitempty"`
	Risk                *float64        `json:",omitempty"`
	Idle                bool            `json:",omitempty"`
}

func developmentRecord(s policy.DevelopmentState) RoutineDevelopment {
	r := RoutineDevelopment{Snapshot: s.Snapshot, Tick: s.Tick, Capacity: s.Capacity, Committed: append([]domain.GoalID(nil), s.Committed...), Partial: s.Partial}
	if v, k := s.Workers.Value(); k {
		r.Workers = &v
	}
	if labor, k := s.Labor.Value(); k {
		r.Labor = map[policy.WorkType]int{}
		for w, n := range labor {
			r.Labor[w] = n
		}
	}
	for _, row := range s.Rows {
		v := RoutineDevelopmentRow{Goal: row.Goal, Score: row.Score, WaitingSince: row.WaitingSince, Selected: row.Selected, Committed: row.Committed, Reason: row.Reason, Bottleneck: row.Bottleneck, Idle: row.Idle}
		if deficit, k := row.Deficit.Value(); k {
			v.Deficit = &deficit
		}
		if risk, k := row.Risk.Value(); k {
			v.Risk = &risk
		}
		r.Rows = append(r.Rows, v)
	}
	return r
}

// State rebuilds the policy ranking this record persisted.
func (r RoutineDevelopment) State() policy.DevelopmentState {
	s := policy.DevelopmentState{Snapshot: r.Snapshot, Tick: r.Tick, Capacity: r.Capacity, Committed: append([]domain.GoalID(nil), r.Committed...), Partial: r.Partial}
	if r.Workers != nil {
		s.Workers = domain.Known(*r.Workers)
	}
	if r.Labor != nil {
		labor := map[policy.WorkType]int{}
		for w, n := range r.Labor {
			labor[w] = n
		}
		s.Labor = domain.Known(labor)
	}
	for _, row := range r.Rows {
		v := policy.DevelopmentRow{Goal: row.Goal, Score: row.Score, WaitingSince: row.WaitingSince, Selected: row.Selected, Committed: row.Committed, Reason: row.Reason, Bottleneck: row.Bottleneck, Idle: row.Idle}
		if row.Deficit != nil {
			v.Deficit = domain.Known(*row.Deficit)
		}
		if row.Risk != nil {
			v.Risk = domain.Known(*row.Risk)
		}
		s.Rows = append(s.Rows, v)
	}
	return s
}
