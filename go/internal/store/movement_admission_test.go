package store

import (
	"context"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func movementStoreFixture(t *testing.T, completed bool) (*Store, string, MovementAdmission) {
	t.Helper()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "movement.db")
	s := open(t, path)
	draft, _ := domain.NewOwnedDraft("pawn")
	d, _ := domain.NewOwnedDraftAction("draft", draft)
	move, _ := domain.NewMovement("pawn", domain.Cell{X: 3, Z: 4}, "draft")
	a, _ := domain.NewMovementAction("move", move)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{d, a})
	if err != nil {
		t.Fatal(err)
	}
	if err = s.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: "plan", Revision: 1, Native: 2}
	session, err := s.Identity(ctx)
	if err != nil {
		t.Fatal(err)
	}
	claim := domain.DraftClaim{Action: "draft", Attempt: 1, Pawn: "pawn", Claim: "claim", Session: domain.ControllerSessionID(session), Origin: snapshot}
	v := MovementAdmission{Snapshot: snapshot, Tick: 12, Pawn: "pawn", Destination: domain.Cell{X: 3, Z: 4}, PawnSnapshotToken: "pawn-cas", DraftClaim: claim}
	if completed {
		movementCompleteDraft(t, s, v)
	}
	return s, path, v
}

func movementCompleteDraft(t *testing.T, s *Store, v MovementAdmission) {
	t.Helper()
	ctx := context.Background()
	if _, err := s.PrepareDraft(ctx, "plan", "draft", DraftAdmission{Snapshot: v.Snapshot, Tick: 10, Pawn: "pawn", PawnSnapshotToken: "draft-cas"}); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, "plan", "draft", v.Snapshot, 10); err != nil {
		t.Fatal(err)
	}
	if _, err := s.RecordDraftReceipt(ctx, "plan", "draft", 1, domain.ReceiptUnknown, domain.Unknown[domain.DraftClaim]()); err != nil {
		t.Fatal(err)
	}
	if _, err := s.PrepareMovement(ctx, "plan", "move", v); err == nil {
		t.Fatal("unknown draft admitted movement")
	}
	ob := domain.Observation{Action: "draft", Attempt: 1, Snapshot: v.Snapshot, Tick: 11, Causality: domain.AfterDispatch, Effect: domain.EffectCompleted}
	if _, err := s.ObserveDraft(ctx, "plan", ob, v.Snapshot, domain.Known(v.DraftClaim)); err != nil {
		t.Fatal(err)
	}
}

func movementBeginRelease(t *testing.T, s *Store, v MovementAdmission) domain.DraftRelease {
	t.Helper()
	p, err := s.BeginDraftCleanup(context.Background(), "plan", "draft", domain.DraftReleaseRequest{Claim: v.DraftClaim, PawnSnapshotToken: "release-cas", Observed: v.Snapshot, Tick: 12})
	if err != nil {
		t.Fatal(err)
	}
	cleanup, _ := p.View().DraftCleanup.Value()
	release, _ := cleanup.Release.Value()
	return release
}

func TestMovementAdmissionRequiresVerifiedMatchingClaimAndFreshTokens(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _, v := movementStoreFixture(t, false)
	if _, err := s.PrepareMovement(ctx, "plan", "move", v); err == nil {
		t.Fatal("pending draft admitted movement")
	}
	movementCompleteDraft(t, s, v)
	changes := []func(*MovementAdmission){
		func(v *MovementAdmission) { v.DraftClaim.Claim = "wrong" },
		func(v *MovementAdmission) { v.DraftClaim.Session = "foreign" },
		func(v *MovementAdmission) { v.DraftClaim.Attempt++ },
		func(v *MovementAdmission) { v.Snapshot.Load = "replacement"; v.DraftClaim.Origin = v.Snapshot },
		func(v *MovementAdmission) { v.Snapshot.Native++; v.DraftClaim.Origin = v.Snapshot },
		func(v *MovementAdmission) { v.PawnSnapshotToken = "" },
		func(v *MovementAdmission) { v.Destination = domain.Cell{X: 9, Z: 9} },
		func(v *MovementAdmission) { v.Tick = 10 },
	}
	for i, change := range changes {
		bad := v
		change(&bad)
		if _, err := s.PrepareMovement(ctx, "plan", "move", bad); err == nil {
			t.Fatal("invalid admission accepted", i)
		}
	}
	if _, err := s.PrepareMovement(ctx, "plan", "move", v); err != nil {
		t.Fatal(err)
	}
	movementBeginRelease(t, s, v)
	if _, err := s.Dispatch(ctx, "plan", "move", v.Snapshot, v.Tick); err == nil {
		t.Fatal("dispatch after cleanup began")
	}
	if _, err := s.PrepareMovement(ctx, "plan", "move", v); err == nil {
		t.Fatal("revalidation after cleanup began")
	}
}

func TestMovementAdmissionReopensAfterPrerequisiteReleased(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, path, v := movementStoreFixture(t, true)
	if _, err := s.Prepare(ctx, "plan", "move", v.Snapshot, v.Tick); err == nil {
		t.Fatal("generic prepare bypass")
	}
	if _, err := s.PrepareMovement(ctx, "plan", "move", v); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, "plan", "move", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
	if _, err := s.RecordReceipt(ctx, "plan", "move", 1, domain.ReceiptUnknown); err != nil {
		t.Fatal(err)
	}
	release := movementBeginRelease(t, s, v)
	if _, err := s.RecordDraftCleanup(ctx, "plan", "draft", release, domain.DraftReleaseConfirmed); err != nil {
		t.Fatal(err)
	}
	if err := s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	state, err := s.LoadPlan(ctx, "plan")
	if err != nil || len(state.MovementAdmissions) != 1 || state.MovementAdmissions[0].Admission != v || !state.Progress[1].View().Unresolved {
		t.Fatal(state, err)
	}
}
