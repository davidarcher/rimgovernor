package store

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func expeditionPolicyRequest(id string, patch domain.ExpeditionPolicyPatch) ExpeditionPolicySubmissionRequest {
	return ExpeditionPolicySubmissionRequest{RequestID: id, World: World{Colony: "colony", Load: "load", Map: 0}, Patch: patch}
}

func TestExpeditionPolicyPartialPatchMergeReplayAndConflict(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	world := World{Colony: "colony", Load: "load", Map: 0}
	// An unset world is governed by the contract defaults rather than by no
	// policy, so the first partial patch has a base to merge onto.
	current, err := s.CurrentExpeditionPolicy(ctx, world)
	if err != nil || current != domain.DefaultExpeditionPolicy() {
		t.Fatal(current, err)
	}
	request := expeditionPolicyRequest("request", domain.ExpeditionPolicyPatch{
		MaximumTravelDays: domain.Some(9.0),
		KeepHomeDoctor:    domain.Some(false),
	})
	first, created, err := s.SubmitExpeditionPolicy(ctx, request)
	if err != nil || !created || first.Request != request || first.Applied != first.Current {
		t.Fatal(first, created, err)
	}
	if first.Applied.MaximumTravelDays() != 9 || first.Applied.KeepHomeDoctor() ||
		first.Applied.MinimumHomeColonists() != 1 || !first.Applied.RequireReturnStorage() {
		t.Fatal("unnamed limits must keep their default", first.Applied)
	}
	replay, created, err := s.SubmitExpeditionPolicy(ctx, request)
	if err != nil || created || replay != first {
		t.Fatal(replay, created, err)
	}
	for _, change := range []func(*ExpeditionPolicySubmissionRequest){
		func(v *ExpeditionPolicySubmissionRequest) { v.World.Map = 1 },
		func(v *ExpeditionPolicySubmissionRequest) { v.World.Load = "other" },
		func(v *ExpeditionPolicySubmissionRequest) { v.World.Colony = "other" },
		func(v *ExpeditionPolicySubmissionRequest) { v.Patch.MaximumTravelDays = domain.Some(8.0) },
		// Dropping a field the original request named is a different
		// request even though the merged result would be identical.
		func(v *ExpeditionPolicySubmissionRequest) { v.Patch.KeepHomeDoctor = domain.Optional[bool]{} },
	} {
		changed := request
		change(&changed)
		if _, _, err := s.SubmitExpeditionPolicy(ctx, changed); !errors.Is(err, ErrConflict) {
			t.Fatal("same request ID with different fields must conflict", err)
		}
	}
	// A second partial patch merges over the first rather than replacing it.
	next := expeditionPolicyRequest("request-2", domain.ExpeditionPolicyPatch{MinimumHomeColonists: domain.Some(int32(4))})
	second, created, err := s.SubmitExpeditionPolicy(ctx, next)
	if err != nil || !created {
		t.Fatal(second, created, err)
	}
	if second.Applied.MinimumHomeColonists() != 4 || second.Applied.MaximumTravelDays() != 9 || second.Applied.KeepHomeDoctor() {
		t.Fatal("the earlier request's limits must survive", second.Applied)
	}
	if current, err = s.CurrentExpeditionPolicy(ctx, world); err != nil || current != second.Applied {
		t.Fatal(current, err)
	}
	// The superseded request stays replayable, still reports what it applied
	// and reports the newer current alongside it.
	old, err := s.LookupExpeditionPolicySubmission(ctx, "request")
	if err != nil || old.Request != request || old.Applied != first.Applied || old.Current != second.Applied {
		t.Fatal(old, err)
	}
	// Replaying it must not re-apply it on top of the newer current.
	stale, created, err := s.SubmitExpeditionPolicy(ctx, request)
	if err != nil || created || stale != old {
		t.Fatal(stale, created, err)
	}
	if current, err = s.CurrentExpeditionPolicy(ctx, world); err != nil || current != second.Applied {
		t.Fatal("a replay must not move the current policy", current, err)
	}
	if _, err = s.LookupExpeditionPolicySubmission(ctx, "missing"); !errors.Is(err, ErrNotFound) {
		t.Fatal("unknown request must not be found", err)
	}
	// Another world merges over its own base.
	other := expeditionPolicyRequest("request-3", domain.ExpeditionPolicyPatch{MinimumHomeColonists: domain.Some(int32(9))})
	other.World.Map = 1
	result, _, err := s.SubmitExpeditionPolicy(ctx, other)
	if err != nil || result.Applied.MaximumTravelDays() != 3 || !result.Applied.KeepHomeDoctor() {
		t.Fatal("another world must start from the defaults", result, err)
	}
	if current, err = s.CurrentExpeditionPolicy(ctx, world); err != nil || current != second.Applied {
		t.Fatal("other world must not overwrite this world", current, err)
	}
}

// Every field round trips through the row and the stored patch payload.
func TestExpeditionPolicyStoresEveryField(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	patch := domain.ExpeditionPolicyPatch{
		MinimumHomeColonists:          domain.Some(int32(6)),
		MinimumHomeFoodDays:           domain.Some(12.25),
		TravelFoodMarginDays:          domain.Some(2.5),
		MaximumTravelDays:             domain.Some(11.5),
		MaximumCaravans:               domain.Some(int32(5)),
		MinimumGoodwill:               domain.Some(int32(20)),
		MinimumDestinationTemperature: domain.Some(-30.5),
		MaximumDestinationTemperature: domain.Some(45.5),
		KeepHomeDoctor:                domain.Some(false),
		RequireReturnStorage:          domain.Some(false),
	}
	request := expeditionPolicyRequest("request", patch)
	written, _, err := s.SubmitExpeditionPolicy(ctx, request)
	if err != nil {
		t.Fatal(err)
	}
	expected, err := patch.Apply(domain.ExpeditionPolicy{})
	if err != nil || written.Applied != expected {
		t.Fatal(written.Applied, expected, err)
	}
	read, err := s.LookupExpeditionPolicySubmission(ctx, "request")
	if err != nil || read != written {
		t.Fatal(read, written, err)
	}
	current, err := s.CurrentExpeditionPolicy(ctx, request.World)
	if err != nil || current != expected {
		t.Fatal(current, err)
	}
}

func TestExpeditionPolicySubmissionRejectsInvalidRequests(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	valid := expeditionPolicyRequest("request", domain.ExpeditionPolicyPatch{MaximumCaravans: domain.Some(int32(3))})
	for _, invalid := range []ExpeditionPolicySubmissionRequest{
		{RequestID: "", World: valid.World, Patch: valid.Patch},
		{RequestID: "request", World: World{}, Patch: valid.Patch},
		// An empty patch changes nothing and is not a request.
		{RequestID: "request", World: valid.World},
		{RequestID: "request", World: valid.World, Patch: domain.ExpeditionPolicyPatch{MaximumCaravans: domain.Some(int32(0))}},
		{RequestID: "request", World: valid.World, Patch: domain.ExpeditionPolicyPatch{
			MinimumDestinationTemperature: domain.Some(40.0),
			MaximumDestinationTemperature: domain.Some(10.0),
		}},
	} {
		if _, _, err := s.SubmitExpeditionPolicy(ctx, invalid); err == nil {
			t.Fatal("invalid request must be rejected", invalid)
		}
	}
	// A lone temperature end that reverses the established window is refused
	// at merge time rather than at field-range time.
	if _, _, err := s.SubmitExpeditionPolicy(ctx, expeditionPolicyRequest("narrow", domain.ExpeditionPolicyPatch{MaximumDestinationTemperature: domain.Some(-20.0)})); err == nil {
		t.Fatal("a merge that reverses the window must be rejected")
	}
	if _, err := s.LookupExpeditionPolicySubmission(ctx, ""); err == nil {
		t.Fatal("invalid request ID must be rejected")
	}
	if _, err := s.CurrentExpeditionPolicy(ctx, World{}); err == nil {
		t.Fatal("invalid world must be rejected")
	}
}
