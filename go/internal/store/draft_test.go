package store

import (
	"context"
	"encoding/json"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"path/filepath"
	"strings"
	"testing"
)

// draftPlan names the plan/action a draft fixture created directly through
// CreatePlan, the way routine defense composes owned drafts.
type draftPlan struct {
	Plan   domain.PlanID
	Action domain.ActionID
}

func draftFixture(t *testing.T) (*Store, string, draftPlan, DraftAdmission) {
	t.Helper()
	path := filepath.Join(t.TempDir(), "draft.db")
	s := open(t, path)
	d, e := domain.NewOwnedDraft("pawn")
	if e != nil {
		t.Fatal(e)
	}
	action, e := domain.NewOwnedDraftAction("draft-action", d)
	if e != nil {
		t.Fatal(e)
	}
	plan, e := domain.NewPlan("draft-plan", 1, []domain.Action{action})
	if e != nil {
		t.Fatal(e)
	}
	if e = s.CreatePlan(context.Background(), plan); e != nil {
		t.Fatal(e)
	}
	v := draftPlan{Plan: "draft-plan", Action: "draft-action"}
	return s, path, v, DraftAdmission{Snapshot: domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: v.Plan, Revision: 1, Direction: 1, Native: 2}, Tick: 10, Pawn: "pawn", PawnSnapshotToken: "cas"}
}
func draftDispatch(t *testing.T, s *Store, v draftPlan, a DraftAdmission) domain.DraftClaim {
	t.Helper()
	ctx := context.Background()
	if _, e := s.PrepareDraft(ctx, v.Plan, v.Action, a); e != nil {
		t.Fatal(e)
	}
	if _, e := s.Dispatch(ctx, v.Plan, v.Action, a.Snapshot, a.Tick); e != nil {
		t.Fatal(e)
	}
	id, e := s.Identity(ctx)
	if e != nil {
		t.Fatal(e)
	}
	return domain.DraftClaim{Action: v.Action, Attempt: 1, Pawn: a.Pawn, Claim: "claim", Session: domain.ControllerSessionID(id), Origin: a.Snapshot}
}
func draftProgress(t *testing.T, s *Store, v draftPlan) domain.Progress {
	t.Helper()
	state, e := s.LoadPlan(context.Background(), v.Plan)
	if e != nil {
		t.Fatal(e)
	}
	return state.Progress[0]
}
func TestDraftCleanupReopenPreservesExactRequestAndSequence(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, path, v, a := draftFixture(t)
	claim := draftDispatch(t, s, v, a)
	if _, e := s.RecordDraftReceipt(ctx, v.Plan, v.Action, 1, domain.ReceiptUnknown, domain.Unknown[domain.DraftClaim]()); e != nil {
		t.Fatal(e)
	}
	s.Close()
	s = open(t, path)
	ob := domain.Observation{Action: v.Action, Attempt: 1, Snapshot: a.Snapshot, Tick: 10, Causality: domain.AfterDispatch, Effect: domain.EffectCompleted}
	if _, e := s.ObserveDraft(ctx, v.Plan, ob, a.Snapshot, domain.Known(claim)); e != nil {
		t.Fatal(e)
	}
	request := domain.DraftReleaseRequest{Claim: claim, PawnSnapshotToken: "exact-cas", Observed: a.Snapshot, Tick: 10}
	p, e := s.BeginDraftCleanup(ctx, v.Plan, v.Action, request)
	if e != nil {
		t.Fatal(e)
	}
	cleanup, _ := p.View().DraftCleanup.Value()
	release, _ := cleanup.Release.Value()
	if release.Sequence != 1 || release.Request != request {
		t.Fatal(release)
	}
	// Simulate losing the caller's commit response: reload rather than reissue.
	s.Close()
	s = open(t, path)
	p = draftProgress(t, s, v)
	cleanup, _ = p.View().DraftCleanup.Value()
	persisted, _ := cleanup.Release.Value()
	if persisted != release || cleanup.Stage != domain.DraftCleanupDispatched || p.View().Attempt != 1 {
		t.Fatal(p.View())
	}
	if _, e = s.BeginDraftCleanup(ctx, v.Plan, v.Action, request); e == nil {
		t.Fatal("blind cleanup redispatch")
	}
	if _, e = s.RecordDraftCleanup(ctx, v.Plan, v.Action, release, domain.DraftReleaseUncertain); e != nil {
		t.Fatal(e)
	}
	request.PawnSnapshotToken = "fresh-cas"
	p, e = s.BeginDraftCleanup(ctx, v.Plan, v.Action, request)
	if e != nil {
		t.Fatal(e)
	}
	cleanup, _ = p.View().DraftCleanup.Value()
	second, _ := cleanup.Release.Value()
	if second.Sequence != 2 {
		t.Fatal(second)
	}
	if _, e = s.RecordDraftCleanup(ctx, v.Plan, v.Action, release, domain.DraftReleaseConfirmed); e == nil {
		t.Fatal("old result accepted")
	}
	if _, e = s.RecordDraftCleanup(ctx, v.Plan, v.Action, second, domain.DraftReleaseConfirmed); e != nil {
		t.Fatal(e)
	}
	s.Close()
	s = open(t, path)
	p = draftProgress(t, s, v)
	cleanup, _ = p.View().DraftCleanup.Value()
	c, known := cleanup.Claim.Value()
	if !known || c != claim || cleanup.Stage != domain.DraftReleased || p.View().Stage != domain.Completed {
		t.Fatal(p.View())
	}
	state, e := s.LoadPlan(ctx, v.Plan)
	if e != nil || len(state.DraftAdmissions) != 1 || len(state.Admissions) != 0 {
		t.Fatal(state, e)
	}
}
func TestDraftAtomicClaimAndAdmissionGuards(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _, v, a := draftFixture(t)
	if _, e := s.Prepare(ctx, v.Plan, v.Action, a.Snapshot, 10); e == nil {
		t.Fatal("generic prepare bypass")
	}
	wrong := a
	wrong.Pawn = "other"
	if _, e := s.PrepareDraft(ctx, v.Plan, v.Action, wrong); e == nil {
		t.Fatal("wrong pawn")
	}
	claim := draftDispatch(t, s, v, a)
	before := draftProgress(t, s, v).View()
	bad := claim
	bad.Session = "foreign"
	if _, e := s.RecordDraftReceipt(ctx, v.Plan, v.Action, 1, domain.ReceiptAccepted, domain.Known(bad)); e == nil {
		t.Fatal("foreign session")
	}
	if draftProgress(t, s, v).View() != before {
		t.Fatal("partial receipt")
	}
	if _, e := s.db.Exec("CREATE TRIGGER fail_draft_event BEFORE INSERT ON transitions BEGIN SELECT RAISE(ABORT,'injected'); END"); e != nil {
		t.Fatal(e)
	}
	if _, e := s.RecordDraftReceipt(ctx, v.Plan, v.Action, 1, domain.ReceiptAccepted, domain.Known(claim)); e == nil {
		t.Fatal("expected rollback")
	}
	if draftProgress(t, s, v).View() != before {
		t.Fatal("claim survived failed receipt")
	}
	if _, e := s.db.Exec("DROP TRIGGER fail_draft_event"); e != nil {
		t.Fatal(e)
	}
	if _, e := s.RecordDraftReceipt(ctx, v.Plan, v.Action, 1, domain.ReceiptRefused, domain.Unknown[domain.DraftClaim]()); e != nil {
		t.Fatal(e)
	}
	cleanup, _ := draftProgress(t, s, v).View().DraftCleanup.Value()
	if cleanup.Stage != domain.DraftNotAcquired {
		t.Fatal(cleanup)
	}
}
func TestDraftCanonicalEventCorruption(t *testing.T) {
	t.Parallel()
	for _, kind := range []string{"zero-sequence", "claim-session", "extra-arm", "unknown-field", "oversized"} {
		t.Run(kind, func(t *testing.T) {
			ctx := context.Background()
			s, _, v, a := draftFixture(t)
			c := draftDispatch(t, s, v, a)
			if _, e := s.RecordDraftReceipt(ctx, v.Plan, v.Action, 1, domain.ReceiptAccepted, domain.Known(c)); e != nil {
				t.Fatal(e)
			}
			if _, e := s.BeginDraftCleanup(ctx, v.Plan, v.Action, domain.DraftReleaseRequest{Claim: c, PawnSnapshotToken: "token", Observed: a.Snapshot, Tick: 10}); e != nil {
				t.Fatal(e)
			}
			var seq int
			var data []byte
			if e := s.db.QueryRow("SELECT sequence,payload FROM transitions ORDER BY sequence DESC LIMIT 1").Scan(&seq, &data); e != nil {
				t.Fatal(e)
			}
			var event transition
			if e := json.Unmarshal(data, &event); e != nil {
				t.Fatal(e)
			}
			switch kind {
			case "zero-sequence":
				event.DraftBegin.Sequence = 0
			case "claim-session":
				event.DraftBegin.Request.Claim.Session = "foreign"
			case "extra-arm":
				event.DraftResult = &draftResultEvent{}
			case "unknown-field":
				data = append(data[:len(data)-1], []byte(",\"unexpected\":true}")...)
			case "oversized":
				data = []byte(strings.Repeat(" ", 32769))
			}
			if kind != "unknown-field" && kind != "oversized" {
				data, _ = json.Marshal(event)
			}
			if _, e := s.db.Exec("UPDATE transitions SET payload=? WHERE sequence=?", data, seq); e != nil {
				t.Fatal(e)
			}
			if _, e := s.LoadPlan(ctx, v.Plan); e == nil {
				t.Fatal("corrupt event accepted")
			}
		})
	}
}
func TestDraftSchemaFiveRejected(t *testing.T) {
	t.Parallel()
	s, path, _, _ := draftFixture(t)
	if _, e := s.db.Exec("PRAGMA user_version=5"); e != nil {
		t.Fatal(e)
	}
	s.Close()
	if reopened, e := Open(context.Background(), path); e == nil {
		reopened.Close()
		t.Fatal("old schema accepted")
	}
}

func TestDraftPreparedAdmissionRefreshAndCancellation(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, path, v, a := draftFixture(t)
	if _, e := s.PrepareDraft(ctx, v.Plan, v.Action, a); e != nil {
		t.Fatal(e)
	}
	s.Close()
	s = open(t, path)
	// Durable preparation preserves evidence. Freshness and lease authorization
	// belong to the executor; this caller supplies a newly inspected CAS token.
	fresh := a
	fresh.Tick++
	fresh.PawnSnapshotToken = "new-token"
	if _, e := s.PrepareDraft(ctx, v.Plan, v.Action, fresh); e != nil {
		t.Fatal(e)
	}
	wrong := fresh
	wrong.Snapshot.Direction++
	if _, e := s.PrepareDraft(ctx, v.Plan, v.Action, wrong); e == nil {
		t.Fatal("changed authority")
	}
	if _, e := s.Dispatch(ctx, v.Plan, v.Action, a.Snapshot, a.Tick); e == nil {
		t.Fatal("old admission tick")
	}
	if _, e := s.Dispatch(ctx, v.Plan, v.Action, fresh.Snapshot, fresh.Tick); e != nil {
		t.Fatal(e)
	}
	id, _ := s.Identity(ctx)
	claim := domain.DraftClaim{Action: v.Action, Attempt: 1, Pawn: a.Pawn, Claim: "claim", Session: domain.ControllerSessionID(id), Origin: a.Snapshot}
	if _, e := s.Cancel(ctx, v.Plan, v.Action); e != nil {
		t.Fatal(e)
	}
	if _, e := s.RecordDraftReceipt(ctx, v.Plan, v.Action, 1, domain.ReceiptAccepted, domain.Known(claim)); e != nil {
		t.Fatal(e)
	}
	current := a.Snapshot
	current.Native++
	if _, e := s.ObserveDraftCleanup(ctx, v.Plan, v.Action, domain.DraftCleanupObservation{Claim: claim, Observed: current, Tick: fresh.Tick, Outcome: domain.DraftReleaseSuperseded}); e != nil {
		t.Fatal(e)
	}
	s.Close()
	s = open(t, path)
	p := draftProgress(t, s, v)
	cleanup, _ := p.View().DraftCleanup.Value()
	if p.View().Stage != domain.Cancelled || cleanup.Stage != domain.DraftSuperseded {
		t.Fatal(p.View())
	}
}
