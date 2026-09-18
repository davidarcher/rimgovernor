package store

import (
	"context"
	"encoding/json"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"reflect"
	"testing"
)

func TestRoutineSuppliesRestartManualAndLaterForbids(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := memoryPath(t)
	s := open(t, path)
	r := routineRequest()
	a := policy.StartingSupply{Thing: "a", Definition: "Steel", Cell: domain.Cell{X: 1, Z: 1}}
	b := policy.StartingSupply{Thing: "b", Definition: "Steel", Cell: domain.Cell{X: 2, Z: 2}}
	later := policy.StartingSupply{Thing: "c", Definition: "Steel", Cell: domain.Cell{X: 3, Z: 3}}
	r.Facts.StartingSupplies = domain.Known([]policy.StartingSupply{a, b})
	out := reviewRoutine(t, s, &r)
	if routineGoal(t, out, policy.AllowStartingSupplies).Goal.Need != domain.NeedDeficit {
		t.Fatal(out)
	}
	s.Close()
	s = open(t, path)
	r.Facts.StartingSupplies = domain.Known([]policy.StartingSupply{b, later})
	out = reviewRoutine(t, s, &r)
	if !reflect.DeepEqual(out.Review.StartingSupplies.Pending, []policy.StartingSupply{b}) {
		t.Fatal(out)
	}
	r.Facts.StartingSupplies = domain.Unknown[[]policy.StartingSupply]()
	r.Facts.ForbiddenSupplies = domain.Known(false)
	out = reviewRoutine(t, s, &r)
	if routineGoal(t, out, policy.AllowStartingSupplies).Goal.Need != domain.NeedUnknown || len(out.Review.StartingSupplies.Pending) != 1 {
		t.Fatal("aggregate overrode unknown census", out)
	}
	r.Enabled = false
	out = reviewRoutine(t, s, &r)
	if len(out.Review.StartingSupplies.Pending) != 1 {
		t.Fatal("Manual discarded cohort", out)
	}
	r.Enabled = true
	r.Current.Native++
	r.Facts.StartingSupplies = domain.Known([]policy.StartingSupply{a, b, later})
	out = reviewRoutine(t, s, &r)
	if !reflect.DeepEqual(out.Review.StartingSupplies.Pending, []policy.StartingSupply{b}) {
		t.Fatal("new direction adopted later forbid", out)
	}
	r.Facts.StartingSupplies = domain.Known([]policy.StartingSupply{a, later})
	out = reviewRoutine(t, s, &r)
	if routineGoal(t, out, policy.AllowStartingSupplies).Goal.Status != domain.GoalSatisfied {
		t.Fatal(out)
	}
	s.Close()
	s = open(t, path)
	r.Facts.StartingSupplies = domain.Known([]policy.StartingSupply{a, b, later})
	out = reviewRoutine(t, s, &r)
	if routineGoal(t, out, policy.AllowStartingSupplies).Goal.Need != domain.NeedRecovered || len(out.Review.StartingSupplies.Pending) != 0 {
		t.Fatal("restart adopted player forbid", out)
	}
	loaded, err := s.LoadRoutineReview(ctx)
	if err != nil || !loaded.StartingSupplies.Initialized {
		t.Fatal(loaded, err)
	}
}

func TestRoutineSuppliesResetAndCorruptHistory(t *testing.T) {
	t.Parallel()
	for _, test := range []struct {
		name   string
		change func(*RoutineReviewRequest)
	}{
		{"world", func(r *RoutineReviewRequest) { r.Current.Load = "other" }},
		{"rewind", func(r *RoutineReviewRequest) { r.Tick = 1 }},
	} {
		t.Run(test.name, func(t *testing.T) {
			s := open(t, memoryPath(t))
			r := routineRequest()
			r.Facts.StartingSupplies = domain.Known([]policy.StartingSupply{})
			reviewRoutine(t, s, &r)
			test.change(&r)
			r.Facts.StartingSupplies = domain.Known([]policy.StartingSupply{{Thing: "a", Definition: "Steel", Cell: domain.Cell{X: 1, Z: 1}}})
			out := reviewRoutine(t, s, &r)
			if len(out.Review.StartingSupplies.Pending) != 1 {
				t.Fatal("new world failed to initialize", out)
			}
			r.Enabled = false
			out = reviewRoutine(t, s, &r)
			out.Review.StartingSupplies.Initialized = false
			data, err := json.Marshal(out.Review)
			if err != nil {
				t.Fatal(err)
			}
			if _, err = s.db.Exec("UPDATE routine_review SET payload=? WHERE singleton=1", data); err != nil {
				t.Fatal(err)
			}
			if _, err = s.LoadRoutineReview(context.Background()); err == nil {
				t.Fatal("invalid disabled cohort accepted")
			}
		})
	}
}
