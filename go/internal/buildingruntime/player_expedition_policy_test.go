package buildingruntime

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func playerExpeditionPolicyRequest(patch domain.ExpeditionPolicyPatch) store.ExpeditionPolicySubmissionRequest {
	return store.ExpeditionPolicySubmissionRequest{RequestID: "expedition-policy-submit", World: playerSubmission().World, Patch: patch}
}

// An expedition policy commits no plan and no action: it is colony
// configuration, so there is nothing for a worker to admit or dispatch and
// nothing that could acquire native control.
func TestPlayerExpeditionPolicySubmissionCommitsNoPlan(t *testing.T) {
	t.Parallel()
	p, _, s, worlds := playerFixture(t)
	q := playerExpeditionPolicyRequest(domain.ExpeditionPolicyPatch{MaximumTravelDays: domain.Some(9.0)})
	result, created, err := p.SubmitExpeditionPolicy(context.Background(), q)
	if err != nil || !created || result.Request != q || result.Applied != result.Current {
		t.Fatal(result, created, err)
	}
	current, err := p.ExpeditionPolicy(context.Background(), q.World)
	if err != nil || current != result.Applied || current.MaximumTravelDays() != 9 || current.MinimumHomeColonists() != 1 {
		t.Fatal(current, err)
	}
	worlds.err = errors.New("world unavailable")
	replay, created, err := p.SubmitExpeditionPolicy(context.Background(), q)
	if err != nil || created || replay != result || worlds.calls != 1 {
		t.Fatal(replay, created, err, worlds.calls)
	}
	changed := q
	changed.Patch.MaximumTravelDays = domain.Some(8.0)
	if _, _, err = p.SubmitExpeditionPolicy(context.Background(), changed); !errors.Is(err, store.ErrConflict) {
		t.Fatal(err)
	}
	worlds.err = nil
	// A later request merges over the established limits rather than
	// replacing them.
	second := playerExpeditionPolicyRequest(domain.ExpeditionPolicyPatch{MinimumGoodwill: domain.Some(int32(10))})
	second.RequestID = "expedition-policy-submit-2"
	merged, _, err := p.SubmitExpeditionPolicy(context.Background(), second)
	if err != nil || merged.Applied.MaximumTravelDays() != 9 || merged.Applied.MinimumGoodwill() != 10 {
		t.Fatal(merged, err)
	}
	// Expedition policy keeps its own request namespace: it owns no row in
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

func TestPlayerExpeditionPolicyFreshWorldAndClosedPlayer(t *testing.T) {
	t.Parallel()
	p, db, _, worlds := playerFixture(t)
	q := playerExpeditionPolicyRequest(domain.ExpeditionPolicyPatch{MaximumCaravans: domain.Some(int32(4))})
	worlds.world.Load = "replacement"
	if _, _, err := p.SubmitExpeditionPolicy(context.Background(), q); !errors.Is(err, store.ErrConflict) {
		t.Fatal(err)
	}
	if _, err := db.LookupExpeditionPolicySubmission(context.Background(), q.RequestID); !errors.Is(err, store.ErrNotFound) {
		t.Fatal(err)
	}
	worlds.world = q.World
	if err := p.Close(context.Background()); err != nil {
		t.Fatal(err)
	}
	if _, _, err := p.SubmitExpeditionPolicy(context.Background(), q); !errors.Is(err, ErrControl) {
		t.Fatal(err)
	}
}
