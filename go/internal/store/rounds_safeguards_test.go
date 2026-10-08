package store

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Emergency findings do not suspend ordinary work; manual pause still does.
func TestRoundsEmergencyAllowsAdmissionAndDispatchButPauseVetoes(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	defer s.Close()
	r := roundsRequest()
	r.Facts.Workers = domain.Known(0)
	r.Facts.Labor = domain.Known(map[policy.WorkType]int{})
	r.Facts.Upkeep.Fires = domain.Known([]policy.UpkeepFire{{ID: "fire", Home: true, Size: domain.Known(.5)}})
	out := reviewRounds(t, s, &r)
	if len(out.Review.Emergency) != 1 || out.Review.Emergency[0] != policy.MaintainFireSafety {
		t.Fatal("review did not record the emergency", out.Review.Emergency)
	}
	loaded, err := s.LoadRounds(ctx)
	if err != nil || len(loaded.Emergency) != 1 {
		t.Fatal("emergency not journalled with the review", loaded.Emergency, err)
	}
	g := roundsGoal(t, out, policy.MaintainResource)
	if g.Standard.Status != domain.StandardOpen {
		t.Fatal("an emergency must not change the goal's status", g.Standard.Status)
	}
	p := plan(t, "p", "a", "b")
	if _, err = s.CommitMethod(ctx, g.Standard.ID, g.Revision, "wood", p); err != nil {
		t.Fatal("emergency blocked ordinary work", err)
	}
	if _, err = s.Prepare(ctx, "p", "a", scope(), r.Tick); err != nil {
		t.Fatal(err)
	}
	if _, err = s.Prepare(ctx, "p", "b", scope(), r.Tick); err != nil {
		t.Fatal(err)
	}
	if _, err = s.Dispatch(ctx, "p", "a", scope(), r.Tick); err != nil {
		t.Fatal("emergency blocked dispatch", err)
	}
	r.Facts.Upkeep.Fires = domain.Known([]policy.UpkeepFire{})
	r.Tick++
	out = reviewRounds(t, s, &r)
	if len(out.Review.Emergency) != 0 {
		t.Fatal("emergency did not clear", out.Review.Emergency)
	}
	r.Enabled = false
	r.Tick++
	out = reviewRounds(t, s, &r)
	g = roundsGoal(t, out, policy.MaintainResource)
	if reason := out.Review.Veto(g.Standard); reason != "control paused" {
		t.Fatal("pause did not veto routine work", reason)
	}
	if _, err = s.CommitMethod(ctx, g.Standard.ID, g.Revision, "wood3", plan(t, "p3", "a3")); !errors.Is(err, ErrNotAdmitted) {
		t.Fatal("pause admitted a routine method", err)
	}
	if _, err = s.Dispatch(ctx, "p", "b", scope(), r.Tick); !errors.Is(err, ErrNotAdmitted) {
		t.Fatal("pause dispatched a prepared plan", err)
	}
}
