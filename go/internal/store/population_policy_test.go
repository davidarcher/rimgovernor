package store

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func populationPolicyRequest(t *testing.T, id string, maximum int32, foodDays float64) PopulationPolicySubmissionRequest {
	t.Helper()
	policy, err := domain.NewPopulationPolicy(maximum, foodDays, 0)
	if err != nil {
		t.Fatal(err)
	}
	return PopulationPolicySubmissionRequest{RequestID: id, World: World{Colony: "colony", Load: "load", Map: 0}, Policy: policy}
}

func TestPopulationPolicySubmissionReplayConflictAndOverwrite(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	world := World{Colony: "colony", Load: "load", Map: 0}
	if _, err := s.CurrentPopulationPolicy(ctx, world); !errors.Is(err, ErrNotFound) {
		t.Fatal("unset world must report no policy", err)
	}
	request := populationPolicyRequest(t, "request", 12, 30)
	request.Policy, _ = domain.NewPopulationPolicy(12, 30, 300)
	first, created, err := s.SubmitPopulationPolicy(ctx, request)
	if err != nil || !created || first.Request != request || first.Current != request.Policy {
		t.Fatal(first, created, err)
	}
	replay, created, err := s.SubmitPopulationPolicy(ctx, request)
	if err != nil || created || replay != first {
		t.Fatal(replay, created, err)
	}
	for _, change := range []func(*PopulationPolicySubmissionRequest){
		func(v *PopulationPolicySubmissionRequest) { v.Policy, _ = domain.NewPopulationPolicy(12, 30, 301) },
		func(v *PopulationPolicySubmissionRequest) { v.World.Map = 1 },
		func(v *PopulationPolicySubmissionRequest) { v.World.Load = "other" },
		func(v *PopulationPolicySubmissionRequest) { v.World.Colony = "other" },
		func(v *PopulationPolicySubmissionRequest) {
			v.Policy = populationPolicyRequest(t, "x", 13, 30).Policy
		},
		func(v *PopulationPolicySubmissionRequest) {
			v.Policy = populationPolicyRequest(t, "x", 12, 31).Policy
		},
	} {
		changed := request
		change(&changed)
		if _, _, err := s.SubmitPopulationPolicy(ctx, changed); !errors.Is(err, ErrConflict) {
			t.Fatal("same request ID with different fields must conflict", err)
		}
	}
	// A population policy is a current value: a new request ID with new
	// fields replaces it rather than conflicting.
	next := populationPolicyRequest(t, "request-2", 20, 45)
	next.Policy, _ = domain.NewPopulationPolicy(20, 45, 600)
	second, created, err := s.SubmitPopulationPolicy(ctx, next)
	if err != nil || !created || second.Current != next.Policy {
		t.Fatal(second, created, err)
	}
	current, err := s.CurrentPopulationPolicy(ctx, world)
	if err != nil || current != next.Policy {
		t.Fatal(current, err)
	}
	// The superseded request stays replayable and reports the newer current.
	old, err := s.LookupPopulationPolicySubmission(ctx, "request")
	if err != nil || old.Request != request || old.Current != next.Policy {
		t.Fatal(old, err)
	}
	if _, err = s.LookupPopulationPolicySubmission(ctx, "missing"); !errors.Is(err, ErrNotFound) {
		t.Fatal("unknown request must not be found", err)
	}
	// Another world keeps its own current value.
	other := populationPolicyRequest(t, "request-3", 5, 10)
	other.World.Map = 1
	if _, _, err = s.SubmitPopulationPolicy(ctx, other); err != nil {
		t.Fatal(err)
	}
	if current, err = s.CurrentPopulationPolicy(ctx, world); err != nil || current != next.Policy {
		t.Fatal("other world must not overwrite this world", current, err)
	}
}

func TestPopulationPolicySubmissionRejectsInvalidRequests(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	valid := populationPolicyRequest(t, "request", 12, 30)
	for _, invalid := range []PopulationPolicySubmissionRequest{
		{RequestID: "", World: valid.World, Policy: valid.Policy},
		{RequestID: "request", World: World{}, Policy: valid.Policy},
		{RequestID: "request", World: valid.World},
	} {
		if _, _, err := s.SubmitPopulationPolicy(ctx, invalid); err == nil {
			t.Fatal("invalid request must be rejected", invalid)
		}
	}
	if _, err := s.LookupPopulationPolicySubmission(ctx, ""); err == nil {
		t.Fatal("invalid request ID must be rejected")
	}
	if _, err := s.CurrentPopulationPolicy(ctx, World{}); err == nil {
		t.Fatal("invalid world must be rejected")
	}
}
