package buildingruntime

import (
	"context"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"sync/atomic"
	"testing"
)

func playerDraftRequest() store.DraftSubmissionRequest {
	intent, _ := domain.NewOwnedDraft("pawn")
	return store.DraftSubmissionRequest{RequestID: "draft-submit", World: playerSubmission().World, Draft: intent}
}
func TestPlayerDraftSubmissionReplayAndSharedFamilyNamespace(t *testing.T) {
	p, db, s, worlds := playerFixture(t)
	q := playerDraftRequest()
	result, created, err := p.SubmitDraft(context.Background(), q)
	if err != nil || !created {
		t.Fatal(result, created, err)
	}
	state, err := db.LoadPlan(context.Background(), result.Plan)
	if err != nil {
		t.Fatal(err)
	}
	draft, ok := state.Spec.Actions()[0].OwnedDraft()
	if !ok || draft != q.Draft || state.Progress[0].View().Stage != domain.Pending || state.Progress[0].View().Attempt != 0 {
		t.Fatal(state)
	}
	worlds.err = errors.New("world unavailable")
	replay, created, err := p.SubmitDraft(context.Background(), q)
	if err != nil || created || replay != result || worlds.calls != 1 {
		t.Fatal(replay, created, err, worlds.calls)
	}
	changed := q
	changed.Draft, _ = domain.NewOwnedDraft("other-pawn")
	if _, _, err = p.SubmitDraft(context.Background(), changed); !errors.Is(err, store.ErrConflict) {
		t.Fatal(err)
	}
	building := playerSubmission()
	building.RequestID = q.RequestID
	if _, _, err = p.Submit(context.Background(), building); !errors.Is(err, store.ErrConflict) {
		t.Fatal(err)
	}
	worlds.err = nil
	building.RequestID = "building-submit"
	if _, _, err = p.Submit(context.Background(), building); err != nil {
		t.Fatal(err)
	}
	q.RequestID = building.RequestID
	if _, _, err = p.SubmitDraft(context.Background(), q); !errors.Is(err, store.ErrConflict) {
		t.Fatal(err)
	}
	if s.acquires.Load() != 0 || s.manuals.Load() != 0 || p.State().Enabled {
		t.Fatal("submission changed native control")
	}
}
func TestPlayerDraftFreshWorldAndClosedPlayer(t *testing.T) {
	p, db, _, worlds := playerFixture(t)
	q := playerDraftRequest()
	worlds.world.Load = "replacement"
	if _, _, err := p.SubmitDraft(context.Background(), q); !errors.Is(err, store.ErrConflict) {
		t.Fatal(err)
	}
	if _, err := db.LookupDraftSubmission(context.Background(), q.RequestID); !errors.Is(err, store.ErrNotFound) {
		t.Fatal(err)
	}
	worlds.world = q.World
	if err := p.Close(context.Background()); err != nil {
		t.Fatal(err)
	}
	if _, _, err := p.SubmitDraft(context.Background(), q); !errors.Is(err, ErrControl) {
		t.Fatal(err)
	}
}
func TestPlayerManualPreemptsDraftSubmissionWorldRead(t *testing.T) {
	p, db, s, _ := playerFixture(t)
	q := playerDraftRequest()
	entered := make(chan struct{})
	var reads atomic.Int32
	p.worlds = playerWorldFunc(func(ctx context.Context) (store.World, error) {
		if reads.Add(1) == 1 {
			close(entered)
			<-ctx.Done()
			return q.World, nil
		}
		return q.World, nil
	})
	done := make(chan error, 1)
	go func() { _, _, err := p.SubmitDraft(context.Background(), q); done <- err }()
	<-entered
	record, err := p.Manual(context.Background(), store.ControlRequest{RequestID: "stop-draft-submit", Kind: store.ManualControl, World: q.World})
	if err != nil || record.Phase != store.DisabledControl {
		t.Fatal(record, err)
	}
	if err := <-done; err == nil {
		t.Fatal("invalidated submission succeeded")
	}
	if _, err := db.LookupDraftSubmission(context.Background(), q.RequestID); !errors.Is(err, store.ErrNotFound) {
		t.Fatal(err)
	}
	if s.acquires.Load() != 0 {
		t.Fatal("submission acquired")
	}
}
func TestPlayerDraftSubmissionRequiresSeparateExplicitAcquire(t *testing.T) {
	p, _, s, _ := playerFixture(t)
	q := playerDraftRequest()
	submission, _, err := p.SubmitDraft(context.Background(), q)
	if err != nil {
		t.Fatal(err)
	}
	if s.acquires.Load() != 0 {
		t.Fatal("implicit acquire")
	}
	record, err := p.Acquire(context.Background(), store.ControlRequest{RequestID: "acquire-draft", Kind: store.AcquireControl, World: q.World, Plan: submission.Plan, Revision: submission.Revision})
	if err != nil || record.Phase != store.GrantedControl || s.acquires.Load() != 1 {
		t.Fatal(record, err)
	}
}
