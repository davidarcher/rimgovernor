package store

import (
	"context"
	"encoding/json"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"path/filepath"
	"reflect"
	"testing"
)

func developmentRow(t *testing.T, r RoutineReview, id domain.GoalID) RoutineDevelopmentRow {
	t.Helper()
	for _, row := range r.Development.Rows {
		if row.Goal == id {
			return row
		}
	}
	t.Fatal("missing development row", id)
	return RoutineDevelopmentRow{}
}
func TestRoutineDevelopmentPersistsAgeAndRechecksPlayerCapacity(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "development.db")
	s := open(t, path)
	r := routineRequest()
	r.Policy.MaxDevelopmentProjects = 1
	first := reviewRoutine(t, s, &r)
	row := developmentRow(t, first.Review, policy.MaintainWood)
	if !row.Selected || row.Deficit == nil || *row.Deficit <= 0 || first.Review.Development.Workers == nil || *first.Review.Development.Workers != 2 {
		t.Fatal(first)
	}
	s.Close()
	s = open(t, path)
	defer s.Close()
	loaded, err := s.LoadRoutineReview(ctx)
	if err != nil || !reflect.DeepEqual(loaded, first.Review) {
		t.Fatal(loaded, err)
	}
	// An accepted player project consumes the slot before a new routine review.
	sub, _, err := s.SubmitBuilding(ctx, submissionRequest(t, "capacity"))
	if err != nil {
		t.Fatal(err)
	}
	g := routineGoal(t, first, policy.MaintainWood)
	if _, err = s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "wood", plan(t, "wood", "wood-action")); !errors.Is(err, ErrConflict) {
		t.Fatal(err)
	}
	r.Tick = 2510
	second := reviewRoutine(t, s, &r)
	row = developmentRow(t, second.Review, policy.MaintainWood)
	if row.Selected || row.Reason != policy.DevelopmentCapacity || row.WaitingSince != 10 || len(second.Review.Development.Committed) != 1 {
		t.Fatal(second)
	}
	if _, err = s.Cancel(ctx, sub.Plan, sub.Action); err != nil {
		t.Fatal(err)
	}
	third := reviewRoutine(t, s, &r)
	row = developmentRow(t, third.Review, policy.MaintainWood)
	if !row.Selected || row.WaitingSince != 10 {
		t.Fatal(third)
	}
	g = routineGoal(t, third, policy.MaintainWood)
	if _, err = s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "wood", plan(t, "wood", "wood-action")); err != nil {
		t.Fatal(err)
	}
	committed := reviewRoutine(t, s, &r)
	row = developmentRow(t, committed.Review, policy.MaintainWood)
	if row.Selected || !row.Committed || row.Reason != policy.DevelopmentCommitted {
		t.Fatal(committed)
	}
	r.Enabled = false
	stopped := reviewRoutine(t, s, &r)
	if len(stopped.Review.Development.Rows) != 0 || stopped.Review.Development.Workers != nil {
		t.Fatal(stopped)
	}
}

func TestRoutineDevelopmentUnknownWorkersAndDirectionReset(t *testing.T) {
	t.Parallel()
	s := open(t, filepath.Join(t.TempDir(), "development.db"))
	defer s.Close()
	r := routineRequest()
	first := reviewRoutine(t, s, &r)
	r.Facts.Workers = domain.Unknown[int]()
	r.Tick = 2510
	unknown := reviewRoutine(t, s, &r)
	row := developmentRow(t, unknown.Review, policy.MaintainWood)
	if row.Selected || row.Reason != policy.DevelopmentWorkersUnknown || row.WaitingSince != first.Review.Tick {
		t.Fatal(unknown)
	}
	r.Facts.Workers = domain.Known(2)
	r.Current.Direction = 1
	changed := reviewRoutine(t, s, &r)
	row = developmentRow(t, changed.Review, policy.MaintainWood)
	if row.WaitingSince != changed.Review.Tick || !row.Selected {
		t.Fatal(changed)
	}
}

func TestRoutineDevelopmentRejectsCorruptDurableSelections(t *testing.T) {
	t.Parallel()
	for _, fault := range []string{"workers", "capacity", "unknown-deficit", "goal", "scope"} {
		t.Run(fault, func(t *testing.T) {
			s := open(t, filepath.Join(t.TempDir(), "development.db"))
			defer s.Close()
			r := routineRequest()
			record := reviewRoutine(t, s, &r).Review
			switch fault {
			case "workers":
				record.Development.Workers = nil
			case "capacity":
				record.Development.Capacity = 9
			case "unknown-deficit":
				for i := range record.Development.Rows {
					record.Development.Rows[i].Deficit = nil
				}
			case "goal":
				record.Development.Rows[0].Goal = "not-a-routine-need"
			case "scope":
				record.Development.Snapshot.Load = "other"
			}
			payload, err := json.Marshal(record)
			if err != nil {
				t.Fatal(err)
			}
			if _, err = s.db.Exec("UPDATE routine_review SET payload=?", payload); err != nil {
				t.Fatal(err)
			}
			if _, err = s.LoadRoutineReview(context.Background()); err == nil {
				t.Fatal("corrupt development history accepted")
			}
		})
	}
}

func TestRoutineDevelopmentCountsCancelledUncertainPlayerWork(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "development.db"))
	defer s.Close()
	r := routineRequest()
	r.Policy.MaxDevelopmentProjects = 1
	request := submissionRequest(t, "uncertain")
	sub, _, err := s.SubmitBuilding(ctx, request)
	if err != nil {
		t.Fatal(err)
	}
	snapshot := r.Current
	snapshot.Plan = sub.Plan
	snapshot.Revision = sub.Revision
	if _, err = s.ReserveAndPrepare(ctx, sub.Plan, sub.Action, Admission{Snapshot: snapshot, Tick: 10, Costs: []MaterialCost{{Definition: "WoodLog", Count: 5}}, Footprint: []domain.Cell{request.Building.Cell()}}); err != nil {
		t.Fatal(err)
	}
	if _, err = s.Dispatch(ctx, sub.Plan, sub.Action, snapshot, 10); err != nil {
		t.Fatal(err)
	}
	if _, err = s.Cancel(ctx, sub.Plan, sub.Action); err != nil {
		t.Fatal(err)
	}
	r.Tick = 11
	held := reviewRoutine(t, s, &r)
	if row := developmentRow(t, held.Review, policy.MaintainWood); row.Selected || row.Reason != policy.DevelopmentCapacity {
		t.Fatal(held)
	}
	if _, err = s.Observe(ctx, sub.Plan, domain.Observation{Action: sub.Action, Attempt: 1, Snapshot: snapshot, Tick: 12, Effect: domain.EffectAbsent}, snapshot); err != nil {
		t.Fatal(err)
	}
	r.Tick = 13
	released := reviewRoutine(t, s, &r)
	if !developmentRow(t, released.Review, policy.MaintainWood).Selected {
		t.Fatal(released)
	}
}
