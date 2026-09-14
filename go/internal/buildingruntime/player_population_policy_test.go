package buildingruntime

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func playerPopulationPolicyRequest(maximum int32, foodDays float64) store.PopulationPolicySubmissionRequest {
	policy, _ := domain.NewPopulationPolicy(maximum, foodDays)
	return store.PopulationPolicySubmissionRequest{RequestID: "population-policy-submit", World: playerSubmission().World, Policy: policy}
}

// A population policy commits no plan and no action: it is colony
// configuration, so there is nothing for a worker to admit or dispatch and
// nothing that could acquire native control.
func TestPlayerPopulationPolicySubmissionCommitsNoPlan(t *testing.T) {
	t.Parallel()
	p, _, s, worlds := playerFixture(t)
	q := playerPopulationPolicyRequest(12, 30)
	result, created, err := p.SubmitPopulationPolicy(context.Background(), q)
	if err != nil || !created || result.Request != q || result.Current != q.Policy {
		t.Fatal(result, created, err)
	}
	current, err := p.PopulationPolicy(context.Background(), q.World)
	if err != nil || current != q.Policy {
		t.Fatal(current, err)
	}
	worlds.err = errors.New("world unavailable")
	replay, created, err := p.SubmitPopulationPolicy(context.Background(), q)
	if err != nil || created || replay != result || worlds.calls != 1 {
		t.Fatal(replay, created, err, worlds.calls)
	}
	changed := q
	changed.Policy, _ = domain.NewPopulationPolicy(20, 30)
	if _, _, err = p.SubmitPopulationPolicy(context.Background(), changed); !errors.Is(err, store.ErrConflict) {
		t.Fatal(err)
	}
	worlds.err = nil
	// Population policy keeps its own request namespace: it owns no row in
	// the shared plan-bearing submissions table, so a building submission
	// may reuse the ID without colliding.
	building := playerSubmission()
	building.RequestID = q.RequestID
	if _, _, err = p.Submit(context.Background(), building); err != nil {
		t.Fatal(err)
	}
	if s.acquires.Load() != 0 || s.manuals.Load() != 0 || p.State().Enabled {
		t.Fatal("submission changed native control")
	}
}

func TestPlayerPopulationPolicyFreshWorldAndClosedPlayer(t *testing.T) {
	t.Parallel()
	p, db, _, worlds := playerFixture(t)
	q := playerPopulationPolicyRequest(12, 30)
	worlds.world.Load = "replacement"
	if _, _, err := p.SubmitPopulationPolicy(context.Background(), q); !errors.Is(err, store.ErrConflict) {
		t.Fatal(err)
	}
	if _, err := db.LookupPopulationPolicySubmission(context.Background(), q.RequestID); !errors.Is(err, store.ErrNotFound) {
		t.Fatal(err)
	}
	worlds.world = q.World
	if err := p.Close(context.Background()); err != nil {
		t.Fatal(err)
	}
	if _, _, err := p.SubmitPopulationPolicy(context.Background(), q); !errors.Is(err, ErrControl) {
		t.Fatal(err)
	}
}
