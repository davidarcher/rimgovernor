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
	Capacity  int
	Committed []domain.GoalID
	Rows      []RoutineDevelopmentRow
}
type RoutineDevelopmentRow struct {
	Goal                domain.GoalID
	Score               float64
	Deficit             *float64
	WaitingSince        domain.Tick
	Selected, Committed bool
	Reason              policy.DevelopmentReason
}

func developmentRecord(s policy.DevelopmentState) RoutineDevelopment {
	r := RoutineDevelopment{Snapshot: s.Snapshot, Tick: s.Tick, Capacity: s.Capacity, Committed: append([]domain.GoalID(nil), s.Committed...)}
	if v, k := s.Workers.Value(); k {
		r.Workers = &v
	}
	for _, row := range s.Rows {
		v := RoutineDevelopmentRow{Goal: row.Goal, Score: row.Score, WaitingSince: row.WaitingSince, Selected: row.Selected, Committed: row.Committed, Reason: row.Reason}
		if deficit, k := row.Deficit.Value(); k {
			v.Deficit = &deficit
		}
		r.Rows = append(r.Rows, v)
	}
	return r
}
func (r RoutineDevelopment) state() policy.DevelopmentState {
	s := policy.DevelopmentState{Snapshot: r.Snapshot, Tick: r.Tick, Capacity: r.Capacity, Committed: append([]domain.GoalID(nil), r.Committed...)}
	if r.Workers != nil {
		s.Workers = domain.Known(*r.Workers)
	}
	for _, row := range r.Rows {
		v := policy.DevelopmentRow{Goal: row.Goal, Score: row.Score, WaitingSince: row.WaitingSince, Selected: row.Selected, Committed: row.Committed, Reason: row.Reason}
		if row.Deficit != nil {
			v.Deficit = domain.Known(*row.Deficit)
		}
		s.Rows = append(s.Rows, v)
	}
	return s
}
