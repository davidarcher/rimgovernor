package store

import (
	"context"
	"errors"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func populationDecisionRequest(t *testing.T, id, pawn string, decision domain.PopulationDecision) PopulationDecisionSubmissionRequest {
	t.Helper()
	directive, err := domain.NewPopulationDirective(domain.PawnID(pawn), decision)
	if err != nil {
		t.Fatal(err)
	}
	return PopulationDecisionSubmissionRequest{RequestID: id, World: World{Colony: "colony", Load: "load", Map: 0}, Directive: directive}
}

func TestPopulationDecisionReplayConflictAndOverwrite(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "population-decision.db"))
	world := World{Colony: "colony", Load: "load", Map: 0}
	if _, err := s.CurrentPopulationDecision(ctx, world, "Thing_Human1"); !errors.Is(err, ErrNotFound) {
		t.Fatal("unnamed pawn must report no decision", err)
	}
	if _, _, err := s.SubmitPopulationPolicy(ctx, populationPolicyRequest(t, "policy", 12, 30)); err != nil {
		t.Fatal(err)
	}
	request := populationDecisionRequest(t, "request", "Thing_Human1", domain.PopulationRescue)
	first, created, err := s.SubmitPopulationDecision(ctx, request)
	if err != nil || !created || first.Request != request || first.Current != request.Directive {
		t.Fatal(first, created, err)
	}
	replay, created, err := s.SubmitPopulationDecision(ctx, request)
	if err != nil || created || replay != first {
		t.Fatal(replay, created, err)
	}
	for _, change := range []func(*PopulationDecisionSubmissionRequest){
		func(v *PopulationDecisionSubmissionRequest) { v.World.Map = 1 },
		func(v *PopulationDecisionSubmissionRequest) { v.World.Load = "other" },
		func(v *PopulationDecisionSubmissionRequest) { v.World.Colony = "other" },
		func(v *PopulationDecisionSubmissionRequest) {
			v.Directive = populationDecisionRequest(t, "x", "Thing_Human2", domain.PopulationRescue).Directive
		},
		func(v *PopulationDecisionSubmissionRequest) {
			v.Directive = populationDecisionRequest(t, "x", "Thing_Human1", domain.PopulationCapture).Directive
		},
	} {
		changed := request
		change(&changed)
		if _, _, err := s.SubmitPopulationDecision(ctx, changed); !errors.Is(err, ErrConflict) {
			t.Fatal("same request ID with different fields must conflict", err)
		}
	}
	// A decision is a current value per pawn: a new request ID replaces it.
	next := populationDecisionRequest(t, "request-2", "Thing_Human1", domain.PopulationIgnore)
	second, created, err := s.SubmitPopulationDecision(ctx, next)
	if err != nil || !created || second.Current != next.Directive {
		t.Fatal(second, created, err)
	}
	current, err := s.CurrentPopulationDecision(ctx, world, "Thing_Human1")
	if err != nil || current != next.Directive {
		t.Fatal(current, err)
	}
	// The superseded request stays replayable and reports the newer current.
	old, err := s.LookupPopulationDecisionSubmission(ctx, "request")
	if err != nil || old.Request != request || old.Current != next.Directive {
		t.Fatal(old, err)
	}
	if _, err = s.LookupPopulationDecisionSubmission(ctx, "missing"); !errors.Is(err, ErrNotFound) {
		t.Fatal("unknown request must not be found", err)
	}
	// Another pawn keeps its own direction, and the world lists both.
	other := populationDecisionRequest(t, "request-3", "Thing_Human2", domain.PopulationRecruit)
	if _, _, err = s.SubmitPopulationDecision(ctx, other); err != nil {
		t.Fatal(err)
	}
	all, err := s.PopulationDecisions(ctx, world)
	if err != nil || len(all) != 2 || all[0] != next.Directive || all[1] != other.Directive {
		t.Fatal(all, err)
	}
	// Another world keeps its own decisions.
	elsewhere := populationDecisionRequest(t, "request-4", "Thing_Human1", domain.PopulationIgnore)
	elsewhere.World.Map = 1
	if _, _, err = s.SubmitPopulationDecision(ctx, elsewhere); err != nil {
		t.Fatal(err)
	}
	if all, err = s.PopulationDecisions(ctx, world); err != nil || len(all) != 2 {
		t.Fatal("other world must not widen this world", all, err)
	}
}

func TestPopulationDecisionRequiresPolicyExceptIgnore(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "population-decision-policy.db"))
	world := World{Colony: "colony", Load: "load", Map: 0}
	for _, decision := range []domain.PopulationDecision{domain.PopulationRescue, domain.PopulationCapture, domain.PopulationRecruit} {
		request := populationDecisionRequest(t, "request-"+string(decision), "Thing_Human1", decision)
		if _, _, err := s.SubmitPopulationDecision(ctx, request); !errors.Is(err, ErrNotFound) {
			t.Fatal("custody decisions require an established population policy", decision, err)
		}
	}
	// Ignore withdraws a direction and never requires a policy.
	withdraw := populationDecisionRequest(t, "withdraw", "Thing_Human1", domain.PopulationIgnore)
	if _, created, err := s.SubmitPopulationDecision(ctx, withdraw); err != nil || !created {
		t.Fatal(created, err)
	}
	if _, _, err := s.SubmitPopulationPolicy(ctx, populationPolicyRequest(t, "policy", 12, 30)); err != nil {
		t.Fatal(err)
	}
	admitted := populationDecisionRequest(t, "recruit", "Thing_Human1", domain.PopulationRecruit)
	if _, created, err := s.SubmitPopulationDecision(ctx, admitted); err != nil || !created {
		t.Fatal(created, err)
	}
	current, err := s.CurrentPopulationDecision(ctx, world, "Thing_Human1")
	if err != nil || current != admitted.Directive {
		t.Fatal(current, err)
	}
}

func TestPopulationDecisionRejectsInvalidRequests(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "population-decision-invalid.db"))
	valid := populationDecisionRequest(t, "request", "Thing_Human1", domain.PopulationIgnore)
	for _, invalid := range []PopulationDecisionSubmissionRequest{
		{RequestID: "", World: valid.World, Directive: valid.Directive},
		{RequestID: "request", World: World{}, Directive: valid.Directive},
		{RequestID: "request", World: valid.World},
	} {
		if _, _, err := s.SubmitPopulationDecision(ctx, invalid); err == nil {
			t.Fatal("invalid request must be rejected", invalid)
		}
	}
	if _, err := s.LookupPopulationDecisionSubmission(ctx, ""); err == nil {
		t.Fatal("invalid request ID must be rejected")
	}
	if _, err := s.PopulationDecisions(ctx, World{}); err == nil {
		t.Fatal("invalid world must be rejected")
	}
	if _, err := s.CurrentPopulationDecision(ctx, World{}, "Thing_Human1"); err == nil {
		t.Fatal("invalid world must be rejected")
	}
}
