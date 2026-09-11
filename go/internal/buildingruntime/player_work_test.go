package buildingruntime

import (
	"context"
	"errors"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func TestPlayerWorkPreferenceReplayDoesNotReadOrEnableNativeControl(t *testing.T) {
	p, _, session, worlds := playerFixture(t)
	sub, _, err := p.Submit(context.Background(), playerSubmission())
	if err != nil {
		t.Fatal(err)
	}
	q := store.WorkPreferenceRequest{RequestID: "work", Plan: sub.Plan, World: sub.Request.World, Overrides: []policy.WorkOverride{{Pawn: "pawn", Work: "Cooking", Priority: 0}}}
	first, err := p.SetWorkPreferences(context.Background(), q)
	if err != nil {
		t.Fatal(err)
	}
	calls := worlds.calls
	worlds.err = errors.New("native unavailable")
	replay, err := p.SetWorkPreferences(context.Background(), q)
	if err != nil || !reflect.DeepEqual(first, replay) || worlds.calls != calls || session.State().Enabled || session.acquires.Load() != 0 {
		t.Fatal(replay, err, worlds.calls)
	}
	worlds.err = nil
	worlds.world.Load = "replacement"
	q.RequestID, q.ExpectedRevision = "stale-world", 1
	if _, err = p.SetWorkPreferences(context.Background(), q); !errors.Is(err, store.ErrConflict) {
		t.Fatal(err)
	}
	got, err := p.WorkPreferences(context.Background(), sub.Plan)
	if err != nil || !reflect.DeepEqual(got, first.Preferences) {
		t.Fatal(got, err)
	}
}
