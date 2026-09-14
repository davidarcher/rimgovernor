package buildingruntime

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func playerPopulationDecisionRequest(id, pawn string, decision domain.PopulationDecision) store.PopulationDecisionSubmissionRequest {
	directive, _ := domain.NewPopulationDirective(domain.PawnID(pawn), decision)
	return store.PopulationDecisionSubmissionRequest{RequestID: id, World: playerSubmission().World, Directive: directive}
}

// A population decision commits no plan and no action: it is a persistent
// player-sourced record, so there is nothing for a worker to admit or
// dispatch and nothing that could acquire native control.
func TestPlayerPopulationDecisionSubmissionCommitsNoPlan(t *testing.T) {
	t.Parallel()
	p, _, s, worlds := playerFixture(t)
	ctx := context.Background()
	policy := playerPopulationPolicyRequest(12, 30)
	policy.RequestID = "population-decision-policy"
	if _, _, err := p.SubmitPopulationPolicy(ctx, policy); err != nil {
		t.Fatal(err)
	}
	q := playerPopulationDecisionRequest("population-decision-submit", "Thing_Human1", domain.PopulationRescue)
	result, created, err := p.SubmitPopulationDecision(ctx, q)
	if err != nil || !created || result.Request != q || result.Current != q.Directive {
		t.Fatal(result, created, err)
	}
	all, err := p.PopulationDecisions(ctx, q.World)
	if err != nil || len(all) != 1 || all[0] != q.Directive {
		t.Fatal(all, err)
	}
	worlds.err = errors.New("world unavailable")
	replay, created, err := p.SubmitPopulationDecision(ctx, q)
	if err != nil || created || replay != result || worlds.calls != 2 {
		t.Fatal(replay, created, err, worlds.calls)
	}
	changed := q
	changed.Directive, _ = domain.NewPopulationDirective("Thing_Human1", domain.PopulationCapture)
	if _, _, err = p.SubmitPopulationDecision(ctx, changed); !errors.Is(err, store.ErrConflict) {
		t.Fatal(err)
	}
	worlds.err = nil
	if s.acquires.Load() != 0 || s.manuals.Load() != 0 || p.State().Enabled {
		t.Fatal("submission changed native control")
	}
}

func TestPlayerPopulationDecisionFreshWorldAndClosedPlayer(t *testing.T) {
	t.Parallel()
	p, db, _, worlds := playerFixture(t)
	ctx := context.Background()
	q := playerPopulationDecisionRequest("population-decision-withdraw", "Thing_Human1", domain.PopulationIgnore)
	worlds.world.Load = "replacement"
	if _, _, err := p.SubmitPopulationDecision(ctx, q); !errors.Is(err, store.ErrConflict) {
		t.Fatal(err)
	}
	if _, err := db.LookupPopulationDecisionSubmission(ctx, q.RequestID); !errors.Is(err, store.ErrNotFound) {
		t.Fatal(err)
	}
	worlds.world = q.World
	// Ignore needs no population policy, so it is accepted on a world that
	// has never had one.
	if _, created, err := p.SubmitPopulationDecision(ctx, q); err != nil || !created {
		t.Fatal(created, err)
	}
	// The three custody decisions do need one.
	custody := playerPopulationDecisionRequest("population-decision-capture", "Thing_Human2", domain.PopulationCapture)
	if _, _, err := p.SubmitPopulationDecision(ctx, custody); !errors.Is(err, store.ErrNotFound) {
		t.Fatal(err)
	}
	if err := p.Close(ctx); err != nil {
		t.Fatal(err)
	}
	if _, _, err := p.SubmitPopulationDecision(ctx, q); !errors.Is(err, ErrControl) {
		t.Fatal(err)
	}
}
