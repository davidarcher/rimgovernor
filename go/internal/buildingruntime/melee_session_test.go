package buildingruntime

import (
	"context"
	"path/filepath"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/draft"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/melee"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/runtimeowner"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
)

type meleeSessionNative struct{ *melee.Fixture }

func (f meleeSessionNative) ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	return bridge.EmergencyObservation{Context: proto.Clone(f.Ctx).(*c.ObservationContext), Facts: policy.EmergencyFacts{ColonistsComplete: domain.Known(true), ThreatsComplete: domain.Known(true), Colonists: []policy.EmergencyPawn{{ID: "pawn", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false)}}, Threats: []policy.EmergencyThreat{{ID: "target", Kind: policy.Hostile, Dead: domain.Known(false), Downed: domain.Known(false)}}}}, bridge.Result{}, nil
}

func TestMeleeSessionCompositionAndWorkerDraftRetention(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	dir := t.TempDir()
	journal, err := store.Open(ctx, filepath.Join(dir, "state.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer journal.Close()
	_, f, dispatch := melee.NewFixture(t)
	native := meleeSessionNative{f}
	id, err := journal.Identity(ctx)
	if err != nil {
		t.Fatal(err)
	}
	f.Receipt.Attempt.ControllerSessionId = proto.String(string(id))
	f.Receipt.AuthorizingOwner.ControllerSessionId = proto.String(string(id))
	f.Row.DraftClaim.GetOwned().Owner.ControllerSessionId = proto.String(string(id))
	f.Progress.Attempt.ControllerSessionId = proto.String(string(id))
	boundary.ReceiptJob(f.Receipt).DraftOwner = proto.String(string(id))
	f.Progress.GetCompleted().Evidence.GetJob().DraftOwner = proto.String(string(id))
	pawnDraft, _ := domain.NewOwnedDraft("pawn")
	d, _ := domain.NewOwnedDraftAction("action", pawnDraft)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{d, dispatch.Attempt.Action})
	if err != nil {
		t.Fatal(err)
	}
	if err = journal.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	_, building := boundary.NewFixture(t)
	config := SessionConfig{Control: ControlConfig{ProfileDirectory: dir, LeaseDuration: time.Second, CallTimeout: time.Second}, Executor: executor.Limits{MaxAge: time.Second, RunTimeout: time.Second, JournalTimeout: time.Second}, Draft: &draft.DraftCapabilities{Native: f.Fixture, Writer: f.Fixture, Cleanup: f.Fixture}, Melee: &melee.MeleeCapabilities{Native: native, Writer: native}}
	s, err := NewSession(ctx, config, journal, sessionNative{building}, &controlNative{generation: 1}, sessionNative{building}, boundary.FixedClock{})
	if err != nil {
		t.Fatal(err)
	}
	defer s.Close(ctx)
	if s.State().Enabled || f.Writes != 0 {
		t.Fatal("constructor granted or wrote")
	}
	snapshot, err := s.Acquire(ctx, dispatch.Attempt.Snapshot)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = journal.PrepareDraft(ctx, plan.ID(), d.ID(), store.DraftAdmission{Snapshot: snapshot, Tick: 10, Pawn: "pawn", PawnSnapshotToken: "cas"}); err != nil {
		t.Fatal(err)
	}
	if _, err = journal.Dispatch(ctx, plan.ID(), d.ID(), snapshot, 10); err != nil {
		t.Fatal(err)
	}
	claim := dispatch.Admission.DraftClaim
	claim.Session = domain.ControllerSessionID(id)
	if _, err = journal.ObserveDraft(ctx, plan.ID(), domain.Observation{Action: d.ID(), Attempt: 1, Snapshot: snapshot, Tick: 10, Causality: domain.AfterDispatch, Effect: domain.EffectCompleted}, snapshot, domain.Known(claim)); err != nil {
		t.Fatal(err)
	}
	state, err := journal.LoadPlan(ctx, plan.ID())
	if err != nil {
		t.Fatal(err)
	}
	if !workerEligible(state, state.Progress[1].View(), s.State(), playerWorld(snapshot)) || workerCleanupEligible(state, state.Progress[0].View(), s.State(), playerWorld(snapshot)) {
		t.Fatal("pending melee did not retain draft")
	}
	r, err := s.Run(ctx, plan.ID(), dispatch.Attempt.Action.ID())
	if err != nil || !r.NativeCalled || f.Writes != 1 {
		t.Fatal(r, err)
	}
	r, err = s.Run(ctx, plan.ID(), dispatch.Attempt.Action.ID())
	if err != nil || r.Progress.View().Stage != domain.Completed {
		t.Fatal(r, err)
	}
	state, err = journal.LoadPlan(ctx, plan.ID())
	if err != nil {
		t.Fatal(err)
	}
	if !workerCleanupEligible(state, state.Progress[0].View(), s.State(), playerWorld(snapshot)) {
		t.Fatal("completed melee retained draft")
	}
	if err = s.Manual(ctx); err != nil {
		t.Fatal(err)
	}
	if f.Releases != 1 || s.State().Enabled {
		t.Fatal("manual did not release original draft")
	}
}

func TestMeleeSessionRejectsIncompleteCapabilitiesBeforeOwnership(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	dir := t.TempDir()
	journal, err := store.Open(ctx, filepath.Join(dir, "state.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer journal.Close()
	_, native, _ := melee.NewFixture(t)
	_, building := boundary.NewFixture(t)
	for _, mode := range []string{"missing-draft", "missing-native", "missing-writer"} {
		config := SessionConfig{Control: ControlConfig{ProfileDirectory: dir, LeaseDuration: time.Second, CallTimeout: time.Second}, Executor: executor.Limits{MaxAge: time.Second, RunTimeout: time.Second, JournalTimeout: time.Second}, Draft: &draft.DraftCapabilities{Native: native.Fixture, Writer: native.Fixture, Cleanup: native.Fixture}, Melee: &melee.MeleeCapabilities{Native: native, Writer: native}}
		switch mode {
		case "missing-draft":
			config.Draft = nil
		case "missing-native":
			config.Melee.Native = nil
		case "missing-writer":
			config.Melee.Writer = nil
		}
		if session, err := NewSession(ctx, config, journal, sessionNative{building}, &controlNative{generation: 1}, sessionNative{building}, boundary.FixedClock{}); err == nil {
			session.Close(ctx)
			t.Fatal("invalid configuration accepted", mode)
		}
		owner, err := runtimeowner.Acquire(ctx, dir)
		if err != nil {
			t.Fatal(err)
		}
		if err = owner.Close(); err != nil {
			t.Fatal(err)
		}
	}
}

func TestMeleeWorkerDisabledOnlyReconcilesOriginalWorld(t *testing.T) {
	t.Parallel()
	_, native, dispatch := melee.NewFixture(t)
	pawnDraft, _ := domain.NewOwnedDraft("pawn")
	d, _ := domain.NewOwnedDraftAction("action", pawnDraft)
	plan, _ := domain.NewPlan("plan", 1, []domain.Action{d, dispatch.Attempt.Action})
	p, _ := domain.NewProgress(plan, "attack")
	state := store.PlanState{Spec: plan, Progress: []domain.Progress{p}}
	world := playerWorld(dispatch.Attempt.Snapshot)
	if workerEligible(state, p.View(), ControlState{}, world) {
		t.Fatal("disabled pending attack eligible")
	}
	p, _ = p.Prepare(dispatch.Attempt.Snapshot, 10)
	p, _ = p.MarkDispatched(dispatch.Attempt.Snapshot, 10)
	if !workerEligible(state, p.View(), ControlState{}, world) {
		t.Fatal("disabled unresolved attack cannot reconcile")
	}
	world.Load = "replacement"
	if workerEligible(state, p.View(), ControlState{}, world) {
		t.Fatal("replacement world retargeted attack")
	}
	if native.Writes != 0 {
		t.Fatal("eligibility wrote native")
	}
}
