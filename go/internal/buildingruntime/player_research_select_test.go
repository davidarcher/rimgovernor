package buildingruntime

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func playerResearchSelectRequest() store.ResearchSelectSubmissionRequest {
	value, _ := domain.NewResearchSelect("project")
	return store.ResearchSelectSubmissionRequest{RequestID: "research-select-submit", World: playerSubmission().World, Select: value}
}

// Research selection is player-command-driven, unlike the fixed-priority a-e
// routine families: submission alone commits a plan and never acquires
// authority or issues a native command, the same shape SubmitQuestAccept
// already proves.
func TestPlayerResearchSelectSubmissionReplayAndSharedFamilyNamespace(t *testing.T) {
	t.Parallel()
	p, db, s, worlds := playerFixture(t)
	q := playerResearchSelectRequest()
	result, created, err := p.SubmitResearchSelect(context.Background(), q)
	if err != nil || !created {
		t.Fatal(result, created, err)
	}
	state, err := db.LoadPlan(context.Background(), result.Plan)
	if err != nil {
		t.Fatal(err)
	}
	value, ok := state.Spec.Actions()[0].ResearchSelect()
	if !ok || value != q.Select || state.Progress[0].View().Stage != domain.Pending || state.Progress[0].View().Attempt != 0 {
		t.Fatal(state)
	}
	worlds.err = errors.New("world unavailable")
	replay, created, err := p.SubmitResearchSelect(context.Background(), q)
	if err != nil || created || replay != result || worlds.calls != 1 {
		t.Fatal(replay, created, err, worlds.calls)
	}
	changed := q
	changed.Select, _ = domain.NewResearchSelect("other-project")
	if _, _, err = p.SubmitResearchSelect(context.Background(), changed); !errors.Is(err, store.ErrConflict) {
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
	if _, _, err = p.SubmitResearchSelect(context.Background(), q); !errors.Is(err, store.ErrConflict) {
		t.Fatal(err)
	}
	if s.acquires.Load() != 0 || s.manuals.Load() != 0 || p.State().Enabled {
		t.Fatal("submission changed native control")
	}
}
func TestPlayerResearchSelectFreshWorldAndClosedPlayer(t *testing.T) {
	t.Parallel()
	p, db, _, worlds := playerFixture(t)
	q := playerResearchSelectRequest()
	worlds.world.Load = "replacement"
	if _, _, err := p.SubmitResearchSelect(context.Background(), q); !errors.Is(err, store.ErrConflict) {
		t.Fatal(err)
	}
	if _, err := db.LookupResearchSelectSubmission(context.Background(), q.RequestID); !errors.Is(err, store.ErrNotFound) {
		t.Fatal(err)
	}
	worlds.world = q.World
	if err := p.Close(context.Background()); err != nil {
		t.Fatal(err)
	}
	if _, _, err := p.SubmitResearchSelect(context.Background(), q); !errors.Is(err, ErrControl) {
		t.Fatal(err)
	}
}
func TestPlayerResearchSelectSubmissionRequiresSeparateExplicitAcquire(t *testing.T) {
	t.Parallel()
	p, _, s, _ := playerFixture(t)
	q := playerResearchSelectRequest()
	if _, _, err := p.SubmitResearchSelect(context.Background(), q); err != nil {
		t.Fatal(err)
	}
	if s.acquires.Load() != 0 {
		t.Fatal("implicit acquire")
	}
	record, err := p.Resume(context.Background(), store.ControlRequest{RequestID: "acquire-research-select", Kind: store.ResumeControl, World: q.World})
	if err != nil || record.Phase != store.RunningControl || s.acquires.Load() != 1 {
		t.Fatal(record, err)
	}
}
