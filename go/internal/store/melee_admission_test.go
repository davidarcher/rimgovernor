package store

import (
	"context"
	"encoding/json"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func meleeStoreFixture(t *testing.T, completed bool) (*Store, string, MeleeAdmission) {
	t.Helper()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "melee.db")
	s := open(t, path)
	draft, _ := domain.NewOwnedDraft("pawn")
	d, _ := domain.NewOwnedDraftAction("draft", draft)
	attack, _ := domain.NewMeleeAttack("pawn", "hostile", "draft")
	a, _ := domain.NewMeleeAttackAction("attack", attack)
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
	v := MeleeAdmission{Snapshot: snapshot, Tick: 12, Pawn: "pawn", Target: "hostile", PawnSnapshotToken: "pawn-cas", TargetSnapshotToken: "target-cas", DraftClaim: claim}
	if completed {
		meleeCompleteDraft(t, s, v)
	}
	return s, path, v
}

func TestMeleeAdmissionTerminalRejectsFutureEvidence(t *testing.T) {
	t.Parallel()
	for _, effect := range []domain.Effect{domain.EffectCompleted, domain.EffectUnsuccessful} {
		t.Run(string(effect), func(t *testing.T) {
			ctx := context.Background()
			s, _, v := meleeStoreFixture(t, true)
			if _, err := s.PrepareMelee(ctx, "plan", "attack", v); err != nil {
				t.Fatal(err)
			}
			if _, err := s.Dispatch(ctx, "plan", "attack", v.Snapshot, v.Tick); err != nil {
				t.Fatal(err)
			}
			observation := domain.Observation{Action: "attack", Attempt: 1, Snapshot: v.Snapshot, Tick: v.Tick + 1, Causality: domain.AfterDispatch, Effect: effect}
			if effect == domain.EffectUnsuccessful {
				observation.UnsuccessfulReason = domain.NativeFailure
			}
			if _, err := s.Observe(ctx, "plan", observation, v.Snapshot); err != nil {
				t.Fatal(err)
			}
			v.Tick = observation.Tick + 1
			data, err := json.Marshal(v)
			if err != nil {
				t.Fatal(err)
			}
			if _, err = s.db.Exec("UPDATE melee_admissions SET payload=?", data); err != nil {
				t.Fatal(err)
			}
			if _, err = s.LoadPlan(ctx, "plan"); err == nil {
				t.Fatal("terminal action accepted future admission")
			}
		})
	}
}

func TestMeleeAdmissionPreparedRefreshThenCancelRetainsEvidence(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, path, v := meleeStoreFixture(t, true)
	if _, err := s.PrepareMelee(ctx, "plan", "attack", v); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, "plan", "attack", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Observe(ctx, "plan", domain.Observation{Action: "attack", Attempt: 1, Snapshot: v.Snapshot, Tick: 13, Causality: domain.AfterDispatch, Effect: domain.EffectAbsent}, v.Snapshot); err != nil {
		t.Fatal(err)
	}
	v.Tick = 14
	if _, err := s.PrepareMelee(ctx, "plan", "attack", v); err != nil {
		t.Fatal(err)
	}
	v.Tick = 15
	v.PawnSnapshotToken = "refreshed-after-absence"
	if _, err := s.PrepareMelee(ctx, "plan", "attack", v); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Cancel(ctx, "plan", "attack"); err != nil {
		t.Fatal(err)
	}
	if err := s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	state, err := s.LoadPlan(ctx, "plan")
	if err != nil || state.Progress[1].View().Stage != domain.Cancelled || state.MeleeAdmissions[0].Admission != v {
		t.Fatal(state, err)
	}
}

func meleeCompleteDraft(t *testing.T, s *Store, v MeleeAdmission) {
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
	if _, err := s.PrepareMelee(ctx, "plan", "attack", v); err == nil {
		t.Fatal("unknown draft admitted attack")
	}
	ob := domain.Observation{Action: "draft", Attempt: 1, Snapshot: v.Snapshot, Tick: 11, Causality: domain.AfterDispatch, Effect: domain.EffectCompleted}
	if _, err := s.ObserveDraft(ctx, "plan", ob, v.Snapshot, domain.Known(v.DraftClaim)); err != nil {
		t.Fatal(err)
	}
}

func meleeBeginRelease(t *testing.T, s *Store, v MeleeAdmission) domain.DraftRelease {
	t.Helper()
	p, err := s.BeginDraftCleanup(context.Background(), "plan", "draft", domain.DraftReleaseRequest{Claim: v.DraftClaim, PawnSnapshotToken: "release-cas", Observed: v.Snapshot, Tick: 12})
	if err != nil {
		t.Fatal(err)
	}
	cleanup, _ := p.View().DraftCleanup.Value()
	release, _ := cleanup.Release.Value()
	return release
}

func TestMeleeAdmissionReopensAfterPrerequisiteReleased(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, path, v := meleeStoreFixture(t, true)
	if _, err := s.Prepare(ctx, "plan", "attack", v.Snapshot, v.Tick); err == nil {
		t.Fatal("generic prepare bypass")
	}
	if _, err := s.PrepareMelee(ctx, "plan", "attack", v); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, "plan", "attack", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
	if _, err := s.RecordReceipt(ctx, "plan", "attack", 1, domain.ReceiptUnknown); err != nil {
		t.Fatal(err)
	}
	release := meleeBeginRelease(t, s, v)
	if _, err := s.RecordDraftCleanup(ctx, "plan", "draft", release, domain.DraftReleaseConfirmed); err != nil {
		t.Fatal(err)
	}
	if err := s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	state, err := s.LoadPlan(ctx, "plan")
	if err != nil || len(state.MeleeAdmissions) != 1 || state.MeleeAdmissions[0].Admission != v || !state.Progress[1].View().Unresolved {
		t.Fatal(state, err)
	}
	state.MeleeAdmissions[0].Admission.PawnSnapshotToken = "mutated-copy"
	again, err := s.LoadPlan(ctx, "plan")
	if err != nil || again.MeleeAdmissions[0].Admission != v {
		t.Fatal(again, err)
	}
}

func TestMeleeAdmissionRequiresVerifiedMatchingClaimAndFreshTokens(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _, v := meleeStoreFixture(t, false)
	if _, err := s.PrepareMelee(ctx, "plan", "attack", v); err == nil {
		t.Fatal("pending draft admitted attack")
	}
	meleeCompleteDraft(t, s, v)
	changes := []func(*MeleeAdmission){
		func(v *MeleeAdmission) { v.DraftClaim.Claim = "wrong" },
		func(v *MeleeAdmission) { v.DraftClaim.Session = "foreign" },
		func(v *MeleeAdmission) { v.DraftClaim.Attempt++ },
		func(v *MeleeAdmission) { v.Snapshot.Load = "replacement"; v.DraftClaim.Origin = v.Snapshot },
		func(v *MeleeAdmission) { v.Snapshot.Native++; v.DraftClaim.Origin = v.Snapshot },
		func(v *MeleeAdmission) { v.Snapshot.Native++; v.DraftClaim.Origin = v.Snapshot },
		func(v *MeleeAdmission) { v.PawnSnapshotToken = "" },
		func(v *MeleeAdmission) { v.TargetSnapshotToken = "\x00" },
		func(v *MeleeAdmission) { v.Target = "other" },
		func(v *MeleeAdmission) { v.Tick = 10 },
	}
	for i, change := range changes {
		bad := v
		change(&bad)
		if _, err := s.PrepareMelee(ctx, "plan", "attack", bad); err == nil {
			t.Fatal("invalid admission accepted", i)
		}
	}
	if _, err := s.PrepareMelee(ctx, "plan", "attack", v); err != nil {
		t.Fatal(err)
	}
	meleeBeginRelease(t, s, v)
	if _, err := s.Dispatch(ctx, "plan", "attack", v.Snapshot, v.Tick); err == nil {
		t.Fatal("dispatch after cleanup began")
	}
	if _, err := s.PrepareMelee(ctx, "plan", "attack", v); err == nil {
		t.Fatal("revalidation after cleanup began")
	}
}

func TestMeleeAdmissionRefreshOnlyBeforeDispatch(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _, v := meleeStoreFixture(t, true)
	if _, err := s.PrepareMelee(ctx, "plan", "attack", v); err != nil {
		t.Fatal(err)
	}
	fresh := v
	fresh.Tick++
	fresh.PawnSnapshotToken = "fresh-pawn"
	fresh.TargetSnapshotToken = "fresh-target"
	p, err := s.PrepareMelee(ctx, "plan", "attack", fresh)
	if err != nil || p.View().Tick != v.Tick {
		t.Fatal(p, err)
	}
	if _, err = s.PrepareMelee(ctx, "plan", "attack", v); err == nil {
		t.Fatal("observation regressed")
	}
	if _, err = s.Dispatch(ctx, "plan", "attack", fresh.Snapshot, v.Tick); err == nil {
		t.Fatal("dispatch predates refreshed admission")
	}
	if _, err = s.Dispatch(ctx, "plan", "attack", fresh.Snapshot, fresh.Tick); err != nil {
		t.Fatal(err)
	}
	if _, err = s.RecordReceipt(ctx, "plan", "attack", 1, domain.ReceiptUnknown); err != nil {
		t.Fatal(err)
	}
	fresh.Tick++
	if _, err = s.PrepareMelee(ctx, "plan", "attack", fresh); err == nil {
		t.Fatal("unknown attempt replaced")
	}
}

func TestMeleeAdmissionRollbackAndMalformedPersistedRecord(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _, v := meleeStoreFixture(t, true)
	if _, err := s.db.Exec(`CREATE TRIGGER reject_melee_prepare BEFORE INSERT ON transitions WHEN NEW.action_id='attack' BEGIN SELECT RAISE(ABORT,'test'); END`); err != nil {
		t.Fatal(err)
	}
	if _, err := s.PrepareMelee(ctx, "plan", "attack", v); err == nil {
		t.Fatal("failed preparation committed")
	}
	state, err := s.LoadPlan(ctx, "plan")
	if err != nil || len(state.MeleeAdmissions) != 0 || state.Progress[1].View().Stage != domain.Pending {
		t.Fatal(state, err)
	}
	if _, err = s.db.Exec("DROP TRIGGER reject_melee_prepare"); err != nil {
		t.Fatal(err)
	}
	if _, err = s.PrepareMelee(ctx, "plan", "attack", v); err != nil {
		t.Fatal(err)
	}
	var original []byte
	if err = s.db.QueryRow("SELECT payload FROM melee_admissions WHERE action_id='attack'").Scan(&original); err != nil {
		t.Fatal(err)
	}
	for _, corrupt := range [][]byte{[]byte(`{}`), append(append([]byte(nil), original...), []byte(` {}`)...), []byte(`{"Unknown":1}`)} {
		if _, err = s.db.Exec("UPDATE melee_admissions SET payload=?", corrupt); err != nil {
			t.Fatal(err)
		}
		if _, err = s.LoadPlan(ctx, "plan"); err == nil {
			t.Fatal("corrupt admission accepted")
		}
	}
}
