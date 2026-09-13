package store

import (
	"context"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func rangedStoreFixture(t *testing.T, completed bool) (*Store, string, MeleeAdmission) {
	t.Helper()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "ranged.db")
	s := open(t, path)
	draft, _ := domain.NewOwnedDraft("pawn")
	d, _ := domain.NewOwnedDraftAction("draft", draft)
	attack, _ := domain.NewRangedAttack("pawn", "hostile", "draft")
	a, _ := domain.NewRangedAttackAction("attack", attack)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{d, a})
	if err != nil {
		t.Fatal(err)
	}
	if err = s.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: "plan", Revision: 1, Direction: 1, Native: 2}
	session, err := s.Identity(ctx)
	if err != nil {
		t.Fatal(err)
	}
	claim := domain.DraftClaim{Action: "draft", Attempt: 1, Pawn: "pawn", Claim: "claim", Session: domain.ControllerSessionID(session), Origin: snapshot}
	v := MeleeAdmission{Snapshot: snapshot, Tick: 12, Pawn: "pawn", Target: "hostile", PawnSnapshotToken: "pawn-cas", TargetSnapshotToken: "target-cas", DraftClaim: claim}
	if completed {
		rangedCompleteDraft(t, s, v)
	}
	return s, path, v
}

func rangedCompleteDraft(t *testing.T, s *Store, v MeleeAdmission) {
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
	if _, err := s.PrepareRangedAttack(ctx, "plan", "attack", v); err == nil {
		t.Fatal("unknown draft admitted attack")
	}
	ob := domain.Observation{Action: "draft", Attempt: 1, Snapshot: v.Snapshot, Tick: 11, Causality: domain.AfterDispatch, Effect: domain.EffectCompleted}
	if _, err := s.ObserveDraft(ctx, "plan", ob, v.Snapshot, domain.Known(v.DraftClaim)); err != nil {
		t.Fatal(err)
	}
}

func rangedBeginRelease(t *testing.T, s *Store, v MeleeAdmission) domain.DraftRelease {
	t.Helper()
	p, err := s.BeginDraftCleanup(context.Background(), "plan", "draft", domain.DraftReleaseRequest{Claim: v.DraftClaim, PawnSnapshotToken: "release-cas", Observed: v.Snapshot, Tick: 12})
	if err != nil {
		t.Fatal(err)
	}
	cleanup, _ := p.View().DraftCleanup.Value()
	release, _ := cleanup.Release.Value()
	return release
}

func TestRangedAdmissionRequiresVerifiedMatchingClaimAndFreshTokens(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _, v := rangedStoreFixture(t, false)
	if _, err := s.PrepareRangedAttack(ctx, "plan", "attack", v); err == nil {
		t.Fatal("pending draft admitted attack")
	}
	rangedCompleteDraft(t, s, v)
	changes := []func(*MeleeAdmission){
		func(v *MeleeAdmission) { v.DraftClaim.Claim = "wrong" },
		func(v *MeleeAdmission) { v.DraftClaim.Session = "foreign" },
		func(v *MeleeAdmission) { v.DraftClaim.Attempt++ },
		func(v *MeleeAdmission) { v.Snapshot.Load = "replacement"; v.DraftClaim.Origin = v.Snapshot },
		func(v *MeleeAdmission) { v.Snapshot.Direction++; v.DraftClaim.Origin = v.Snapshot },
		func(v *MeleeAdmission) { v.Snapshot.Native++; v.DraftClaim.Origin = v.Snapshot },
		func(v *MeleeAdmission) { v.PawnSnapshotToken = "" },
		func(v *MeleeAdmission) { v.TargetSnapshotToken = "\x00" },
		func(v *MeleeAdmission) { v.Target = "other" },
		func(v *MeleeAdmission) { v.Tick = 10 },
	}
	for i, change := range changes {
		bad := v
		change(&bad)
		if _, err := s.PrepareRangedAttack(ctx, "plan", "attack", bad); err == nil {
			t.Fatal("invalid admission accepted", i)
		}
	}
	if _, err := s.PrepareRangedAttack(ctx, "plan", "attack", v); err != nil {
		t.Fatal(err)
	}
	rangedBeginRelease(t, s, v)
	if _, err := s.Dispatch(ctx, "plan", "attack", v.Snapshot, v.Tick); err == nil {
		t.Fatal("dispatch after cleanup began")
	}
	if _, err := s.PrepareRangedAttack(ctx, "plan", "attack", v); err == nil {
		t.Fatal("revalidation after cleanup began")
	}
}

func TestRangedAdmissionReopensAfterPrerequisiteReleased(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, path, v := rangedStoreFixture(t, true)
	if _, err := s.Prepare(ctx, "plan", "attack", v.Snapshot, v.Tick); err == nil {
		t.Fatal("generic prepare bypass")
	}
	if _, err := s.PrepareRangedAttack(ctx, "plan", "attack", v); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, "plan", "attack", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
	if _, err := s.RecordReceipt(ctx, "plan", "attack", 1, domain.ReceiptUnknown); err != nil {
		t.Fatal(err)
	}
	release := rangedBeginRelease(t, s, v)
	if _, err := s.RecordDraftCleanup(ctx, "plan", "draft", release, domain.DraftReleaseConfirmed); err != nil {
		t.Fatal(err)
	}
	if err := s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	state, err := s.LoadPlan(ctx, "plan")
	if err != nil || len(state.RangedAdmissions) != 1 || state.RangedAdmissions[0].Admission != v || !state.Progress[1].View().Unresolved {
		t.Fatal(state, err)
	}
}
