package store

import (
	"context"
	"errors"
	"path/filepath"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestWorkPreferencesReplayCASRestartAndClear(t *testing.T) {
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "work.db")
	s := open(t, path)
	sub, _, err := s.SubmitBuilding(ctx, submissionRequest(t, "building"))
	if err != nil {
		t.Fatal(err)
	}
	q := WorkPreferenceRequest{RequestID: "work", Plan: sub.Plan, World: sub.Request.World, Overrides: []policy.WorkOverride{{Pawn: "pawn", Work: "Construction", Priority: 0}}}
	first, err := s.SetWorkPreferences(ctx, q)
	if err != nil || first.Preferences.Revision != 1 {
		t.Fatal(first, err)
	}
	if replay, err := s.SetWorkPreferences(ctx, q); err != nil || !reflect.DeepEqual(replay, first) {
		t.Fatal(replay, err)
	}
	for _, phase := range []string{"reused-id", "stale-revision", "world"} {
		changed := q
		changed.Overrides = []policy.WorkOverride{}
		if phase != "reused-id" {
			changed.RequestID = phase
		}
		if phase == "world" {
			changed.ExpectedRevision = 1
			changed.World.Load = "other"
		}
		if _, err := s.SetWorkPreferences(ctx, changed); !errors.Is(err, ErrConflict) {
			t.Fatal(phase, err)
		}
	}
	if err := s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	got, err := s.LoadWorkPreferences(ctx, sub.Plan)
	if err != nil || !reflect.DeepEqual(got, first.Preferences) {
		t.Fatal(got, err)
	}
	q.RequestID, q.ExpectedRevision, q.Overrides = "clear", 1, []policy.WorkOverride{}
	cleared, err := s.SetWorkPreferences(ctx, q)
	if err != nil || cleared.Preferences.Revision != 2 || len(cleared.Preferences.Overrides) != 0 {
		t.Fatal(cleared, err)
	}
	if old, err := s.LookupWorkPreference(ctx, "work"); err != nil || !reflect.DeepEqual(old, first) {
		t.Fatal(old, err)
	}
	if got, err := s.LoadWorkPreferences(ctx, sub.Plan); err != nil || !reflect.DeepEqual(got, cleared.Preferences) {
		t.Fatal(got, err)
	}
}

func TestWorkPreferencesInvalidateReviewAndRejectStaleInputs(t *testing.T) {
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "work.db"))
	sub, _, err := s.SubmitBuilding(ctx, submissionRequest(t, "building"))
	if err != nil {
		t.Fatal(err)
	}
	r := routineRequest()
	r.Current.Plan, r.Current.Revision = sub.Plan, sub.Revision
	before, err := s.ReviewRoutine(ctx, r)
	if err != nil {
		t.Fatal(err)
	}
	q := WorkPreferenceRequest{RequestID: "work", Plan: sub.Plan, World: sub.Request.World, Overrides: []policy.WorkOverride{{Pawn: "pawn", Work: "Doctor", Priority: 0}}}
	if _, err := s.SetWorkPreferences(ctx, q); err != nil {
		t.Fatal(err)
	}
	stopped, err := s.LoadRoutineReview(ctx)
	if err != nil || stopped.Enabled || stopped.Revision != before.Review.Revision+1 {
		t.Fatal(stopped, err)
	}
	for _, b := range stopped.Goals {
		g, err := s.LoadGoal(ctx, b.Goal)
		if err != nil || g.Goal.Status != domain.GoalInvalidated {
			t.Fatal(g, err)
		}
	}
	r.Revision = stopped.Revision
	if _, err := s.ReviewRoutine(ctx, r); !errors.Is(err, ErrConflict) {
		t.Fatal("stale preference input accepted", err)
	}
	r.WorkPreferenceRevision = 1
	if _, err := s.ReviewRoutine(ctx, r); err != nil {
		t.Fatal(err)
	}
	active, _ := s.LoadRoutineReview(ctx)
	if _, err := s.SetWorkPreferences(ctx, q); err != nil {
		t.Fatal(err)
	}
	after, _ := s.LoadRoutineReview(ctx)
	if !reflect.DeepEqual(active, after) {
		t.Fatal("historical replay invalidated current review")
	}
}

func TestWorkPreferencesRejectMalformedOverrides(t *testing.T) {
	q := WorkPreferenceRequest{RequestID: "work", Plan: "plan", World: World{Colony: "colony", Load: "load"}, Overrides: []policy.WorkOverride{}}
	for _, values := range [][]policy.WorkOverride{nil, {{Pawn: "pawn", Work: "Cooking", Priority: 5}}, {{Pawn: "pawn", Work: "Cooking"}, {Pawn: "pawn", Work: "Cooking"}}, {{Pawn: "", Work: "Cooking"}}} {
		q.Overrides = values
		if q.Validate() == nil {
			t.Fatal(values)
		}
	}
}
