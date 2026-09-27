package buildingruntime

import (
	"context"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/draft"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/melee"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/store/storetest"
)

func TestMeleeSessionCompositionAndWorkerDraftRetention(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	dir := t.TempDir()
	journal, err := store.Open(ctx, storetest.Path(t))
	if err != nil {
		t.Fatal(err)
	}
	defer journal.Close()
	_, f, dispatch := melee.NewFixture(t)
	id, err := journal.Identity(ctx)
	if err != nil {
		t.Fatal(err)
	}
	pawnDraft, _ := domain.NewOwnedDraft("pawn")
	d, _ := domain.NewOwnedDraftAction("action", pawnDraft)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{d, dispatch.Action})
	if err != nil {
		t.Fatal(err)
	}
	if err = journal.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	_, building := boundary.NewFixture(t)
	config := SessionConfig{Control: ControlConfig{ProfileDirectory: dir, CallTimeout: 5 * time.Second}, Executor: executor.Limits{MaxAge: time.Second, RunTimeout: 5 * time.Second, JournalTimeout: 5 * time.Second}, Draft: &draft.DraftCapabilities{Native: f.Fixture, Writer: f.Fixture, Cleanup: f.Fixture}, Melee: &melee.MeleeCapabilities{Writer: f}}
	s, err := NewSession(ctx, config, journal, sessionNative{building}, &controlNative{generation: 1}, sessionNative{building}, boundary.FixedClock{})
	if err != nil {
		t.Fatal(err)
	}
	defer s.Close(ctx)
	if s.State().Enabled || f.Writes != 0 {
		t.Fatal("constructor granted or wrote")
	}
	snapshot, err := s.Acquire(ctx, dispatch.Snapshot)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = journal.PrepareDraft(ctx, plan.ID(), d.ID(), store.DraftAdmission{Snapshot: snapshot, Tick: 10, Pawn: "pawn", PawnSnapshotToken: "cas"}); err != nil {
		t.Fatal(err)
	}
	if _, err = journal.Dispatch(ctx, plan.ID(), d.ID(), snapshot, 10); err != nil {
		t.Fatal(err)
	}
	claim := domain.DraftClaim{Action: d.ID(), Attempt: 1, Pawn: "pawn", Claim: "claim", Session: domain.ControllerSessionID(id), Origin: snapshot}
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
	r, err := s.Run(ctx, plan.ID(), dispatch.Action.ID())
	if err != nil || !r.NativeCalled || f.Applies != 1 || r.Progress.View().Stage != domain.Completed {
		t.Fatal(r, err)
	}
	// The applied subdue is terminal but native keeps fighting: its draft is
	// held, and the resume sweep leaves it alone.
	state, err = journal.LoadPlan(ctx, plan.ID())
	if err != nil {
		t.Fatal(err)
	}
	world := playerWorld(snapshot)
	if !workerPlanHoldsDraft(state, state.Progress[0].View()) || workerCleanupEligible(state, state.Progress[0].View(), s.State(), world) {
		t.Fatal("completed subdue released its draft")
	}
	if err = s.drafts.run(ctx, true); err != nil || f.Releases != 0 {
		t.Fatal("sweep released a held draft", err)
	}
	if !domain.GoalWorkOpen(state.Progress) {
		t.Fatal("held draft let the goal close")
	}
	// The victim still raging keeps the order; once downed, the break review
	// cancels it, the draft is released and the goal's work closes.
	victim := policy.EmergencyPawn{ID: "target", MentalState: domain.Known(policy.MentalState{DefName: "Berserk", IsAggro: true}), Dead: domain.Known(false), Downed: domain.Known(false)}
	facts := policy.EmergencyFacts{ColonistsComplete: domain.Known(true), Colonists: []policy.EmergencyPawn{{ID: "pawn", Dead: domain.Known(false), Downed: domain.Known(false)}, victim}}
	plans, err := journal.LoadPlans(ctx, 256)
	if err != nil {
		t.Fatal(err)
	}
	if err = releaseBreakWork(ctx, journal, snapshot, facts, plans); err != nil {
		t.Fatal(err)
	}
	if state, err = journal.LoadPlan(ctx, plan.ID()); err != nil || !workerPlanHoldsDraft(state, state.Progress[0].View()) {
		t.Fatal("a raging victim released the subdue", err)
	}
	facts.Colonists[1].Downed = domain.Known(true)
	if err = releaseBreakWork(ctx, journal, snapshot, facts, plans); err != nil {
		t.Fatal(err)
	}
	if state, err = journal.LoadPlan(ctx, plan.ID()); err != nil || state.Progress[1].View().Stage != domain.Cancelled || workerPlanHoldsDraft(state, state.Progress[0].View()) {
		t.Fatal("downed victim did not end the subdue", err)
	}
	for range 3 {
		if err = s.drafts.run(ctx, true); err != nil {
			t.Fatal(err)
		}
	}
	if state, err = journal.LoadPlan(ctx, plan.ID()); err != nil || f.Releases != 1 || domain.GoalWorkOpen(state.Progress) {
		t.Fatal("cancelled subdue did not release its draft and close", f.Releases, err)
	}
}

// A draft the plan still holds survives an authority hold and the resume
// after it: neither the worker's cleanup rotation while disabled nor the
// resume's drain releases it, and the pending order stays eligible once
// authority is back. An explicit Manual still releases it (#228, #318).
func TestMeleeSessionHeldDraftSurvivesHoldAndResume(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	dir := t.TempDir()
	journal, err := store.Open(ctx, storetest.Path(t))
	if err != nil {
		t.Fatal(err)
	}
	defer journal.Close()
	_, f, dispatch := melee.NewFixture(t)
	id, err := journal.Identity(ctx)
	if err != nil {
		t.Fatal(err)
	}
	pawnDraft, _ := domain.NewOwnedDraft("pawn")
	d, _ := domain.NewOwnedDraftAction("action", pawnDraft)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{d, dispatch.Action})
	if err != nil {
		t.Fatal(err)
	}
	if err = journal.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	_, building := boundary.NewFixture(t)
	config := SessionConfig{Control: ControlConfig{ProfileDirectory: dir, CallTimeout: 5 * time.Second}, Executor: executor.Limits{MaxAge: time.Second, RunTimeout: 5 * time.Second, JournalTimeout: 5 * time.Second}, Draft: &draft.DraftCapabilities{Native: f.Fixture, Writer: f.Fixture, Cleanup: f.Fixture}, Melee: &melee.MeleeCapabilities{Writer: f}}
	s, err := NewSession(ctx, config, journal, sessionNative{building}, &controlNative{generation: 1}, sessionNative{building}, boundary.FixedClock{})
	if err != nil {
		t.Fatal(err)
	}
	defer s.Close(ctx)
	snapshot, err := s.Acquire(ctx, dispatch.Snapshot)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = journal.PrepareDraft(ctx, plan.ID(), d.ID(), store.DraftAdmission{Snapshot: snapshot, Tick: 10, Pawn: "pawn", PawnSnapshotToken: "cas"}); err != nil {
		t.Fatal(err)
	}
	if _, err = journal.Dispatch(ctx, plan.ID(), d.ID(), snapshot, 10); err != nil {
		t.Fatal(err)
	}
	claim := domain.DraftClaim{Action: d.ID(), Attempt: 1, Pawn: "pawn", Claim: "claim", Session: domain.ControllerSessionID(id), Origin: snapshot}
	if _, err = journal.ObserveDraft(ctx, plan.ID(), domain.Observation{Action: d.ID(), Attempt: 1, Snapshot: snapshot, Tick: 10, Causality: domain.AfterDispatch, Effect: domain.EffectCompleted}, snapshot, domain.Known(claim)); err != nil {
		t.Fatal(err)
	}
	world := playerWorld(snapshot)
	state, err := journal.LoadPlan(ctx, plan.ID())
	if err != nil {
		t.Fatal(err)
	}
	// A hold: authority disabled locally, the world unchanged.
	if err = s.Disable(); err != nil {
		t.Fatal(err)
	}
	if held := s.State(); held.Enabled || workerCleanupEligible(state, state.Progress[0].View(), held, world) {
		t.Fatal("hold released the held draft", held)
	}
	// The resume's drain keeps it too, and the pending order is eligible
	// again under the new scope.
	resumed, err := s.Acquire(ctx, dispatch.Snapshot)
	if err != nil {
		t.Fatal(err)
	}
	if f.Releases != 0 {
		t.Fatal("resume released the held draft", f.Releases)
	}
	scope := s.State()
	scope.Snapshot = resumed
	if workerCleanupEligible(state, state.Progress[0].View(), scope, world) || !workerEligible(state, state.Progress[1].View(), scope, world) {
		t.Fatal("resumed scope does not carry the held draft", scope)
	}
	// Only a plan that still holds it: a cancelled sibling makes it cleanup.
	if _, err = journal.Cancel(ctx, plan.ID(), dispatch.Action.ID()); err != nil {
		t.Fatal(err)
	}
	if cancelled, err := journal.LoadPlan(ctx, plan.ID()); err != nil || !workerCleanupEligible(cancelled, cancelled.Progress[0].View(), scope, world) {
		t.Fatal("cancelled plan retained draft", err)
	}
	if err = s.Manual(ctx); err != nil {
		t.Fatal(err)
	}
	if f.Releases != 1 || s.State().Enabled {
		t.Fatal("manual did not release the draft", f.Releases)
	}
}

// An open fight's drafts survive an authority flip (#906): the fight plan
// holds only its defenders' drafts, its orders going out at each stop, so
// nothing in the plan is unfinished. The resume's drain keeps them while the
// fight row is open; releasing them dropped the plan out of its goal and
// left re-admission refusing no_eligible_squad. A closed fight releases.
func TestOpenFightDraftsSurviveTheResumeDrain(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	journal, err := store.Open(ctx, storetest.Path(t))
	if err != nil {
		t.Fatal(err)
	}
	defer journal.Close()
	_, f, dispatch := melee.NewFixture(t)
	plan, err := domain.NewPlan("plan", 1, nil)
	if err != nil {
		t.Fatal(err)
	}
	if err = journal.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	_, building := boundary.NewFixture(t)
	config := SessionConfig{Control: ControlConfig{ProfileDirectory: t.TempDir(), CallTimeout: 5 * time.Second}, Executor: executor.Limits{MaxAge: time.Second, RunTimeout: 5 * time.Second, JournalTimeout: 5 * time.Second}, Draft: &draft.DraftCapabilities{Native: f.Fixture, Writer: f.Fixture, Cleanup: f.Fixture}, Melee: &melee.MeleeCapabilities{Writer: f}}
	s, err := NewSession(ctx, config, journal, sessionNative{building}, &controlNative{generation: 1}, sessionNative{building}, boundary.FixedClock{})
	if err != nil {
		t.Fatal(err)
	}
	defer s.Close(ctx)
	snapshot, err := s.Acquire(ctx, dispatch.Snapshot)
	if err != nil {
		t.Fatal(err)
	}
	// The fight's admission batch drafted the pawn under "claim" (#910).
	world := store.World{Colony: snapshot.Colony, Load: snapshot.Load, Map: snapshot.Map}
	if err = journal.OpenCombatFight(ctx, plan.ID(), policy.CombatMemory{}, world, []domain.PawnID{"pawn"}); err != nil {
		t.Fatal(err)
	}
	if err = journal.RecordCombatClaims(ctx, plan.ID(), map[domain.PawnID]string{"pawn": "claim"}, nil); err != nil {
		t.Fatal(err)
	}
	if err = s.Disable(); err != nil {
		t.Fatal(err)
	}
	if _, err = s.Acquire(ctx, dispatch.Snapshot); err != nil {
		t.Fatal(err)
	}
	if f.Releases != 0 {
		t.Fatal("resume released the open fight's draft", f.Releases)
	}
	if fight, _, err := journal.LoadCombatFight(ctx, plan.ID()); err != nil || fight.Claims["pawn"] != "claim" {
		t.Fatal("the fight lost its draft", fight, err)
	}
	// The worker's per-step release keeps an open fight's claims and
	// releases a closed one's (#910).
	if err = s.ReleaseClosedFights(ctx); err != nil || f.Releases != 0 {
		t.Fatal("the worker released an open fight's draft", f.Releases, err)
	}
	if err = journal.CloseCombatFight(ctx, plan.ID()); err != nil {
		t.Fatal(err)
	}
	if err = s.ReleaseClosedFights(ctx); err != nil || f.Releases != 1 {
		t.Fatal("the worker kept a closed fight's draft", f.Releases, err)
	}
	if err = s.Disable(); err != nil {
		t.Fatal(err)
	}
	if _, err = s.Acquire(ctx, dispatch.Snapshot); err != nil {
		t.Fatal(err)
	}
	if fight, _, err := journal.LoadCombatFight(ctx, plan.ID()); err != nil || f.Releases != 1 || f.LastRelease.GetExpectedClaimId() != "claim" || fight.Holds() {
		t.Fatal("resume kept a closed fight's draft", f.Releases, fight, err)
	}
}

// A player resume after an observed revocation revokes first (#916): that
// Manual drains like the Acquire and keeps an open fight's drafts; a pause's
// Manual, and the resume once the fight has closed, release them.
func TestResumeManualKeepsAnOpenFightsDrafts(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	journal, err := store.Open(ctx, storetest.Path(t))
	if err != nil {
		t.Fatal(err)
	}
	defer journal.Close()
	_, f, dispatch := melee.NewFixture(t)
	plan, err := domain.NewPlan("plan", 1, nil)
	if err != nil {
		t.Fatal(err)
	}
	if err = journal.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	_, building := boundary.NewFixture(t)
	config := SessionConfig{Control: ControlConfig{ProfileDirectory: t.TempDir(), CallTimeout: 5 * time.Second}, Executor: executor.Limits{MaxAge: time.Second, RunTimeout: 5 * time.Second, JournalTimeout: 5 * time.Second}, Draft: &draft.DraftCapabilities{Native: f.Fixture, Writer: f.Fixture, Cleanup: f.Fixture}, Melee: &melee.MeleeCapabilities{Writer: f}}
	s, err := NewSession(ctx, config, journal, sessionNative{building}, &controlNative{generation: 1}, sessionNative{building}, boundary.FixedClock{})
	if err != nil {
		t.Fatal(err)
	}
	defer s.Close(ctx)
	snapshot, err := s.Acquire(ctx, dispatch.Snapshot)
	if err != nil {
		t.Fatal(err)
	}
	// The fight's admission batch drafted the pawn under "claim" (#910).
	world := store.World{Colony: snapshot.Colony, Load: snapshot.Load, Map: snapshot.Map}
	if err = journal.OpenCombatFight(ctx, plan.ID(), policy.CombatMemory{}, world, []domain.PawnID{"pawn"}); err != nil {
		t.Fatal(err)
	}
	if err = journal.RecordCombatClaims(ctx, plan.ID(), map[domain.PawnID]string{"pawn": "claim"}, nil); err != nil {
		t.Fatal(err)
	}
	if err = s.ManualForResume(ctx); err != nil {
		t.Fatal(err)
	}
	if f.Releases != 0 || s.State().Enabled {
		t.Fatal("the resume's Manual released the open fight's draft", f.Releases)
	}
	if _, err = s.Acquire(ctx, dispatch.Snapshot); err != nil {
		t.Fatal(err)
	}
	if fight, _, err := journal.LoadCombatFight(ctx, plan.ID()); err != nil || f.Releases != 0 || fight.Claims["pawn"] != "claim" {
		t.Fatal("the fight lost its draft across the resume", f.Releases, fight, err)
	}
	if err = journal.CloseCombatFight(ctx, plan.ID()); err != nil {
		t.Fatal(err)
	}
	if err = s.ManualForResume(ctx); err != nil {
		t.Fatal(err)
	}
	if fight, _, err := journal.LoadCombatFight(ctx, plan.ID()); err != nil || f.Releases != 1 || fight.Holds() {
		t.Fatal("the resume kept a closed fight's draft", f.Releases, fight, err)
	}
}

func TestMeleeSessionRejectsIncompleteCapabilitiesBeforeOwnership(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	dir := t.TempDir()
	journal, err := store.Open(ctx, storetest.Path(t))
	if err != nil {
		t.Fatal(err)
	}
	defer journal.Close()
	_, native, _ := melee.NewFixture(t)
	_, building := boundary.NewFixture(t)
	for _, mode := range []string{"missing-draft", "missing-writer"} {
		config := SessionConfig{Control: ControlConfig{ProfileDirectory: dir, CallTimeout: 5 * time.Second}, Executor: executor.Limits{MaxAge: time.Second, RunTimeout: 5 * time.Second, JournalTimeout: 5 * time.Second}, Draft: &draft.DraftCapabilities{Native: native.Fixture, Writer: native.Fixture, Cleanup: native.Fixture}, Melee: &melee.MeleeCapabilities{Writer: native}}
		switch mode {
		case "missing-draft":
			config.Draft = nil
		case "missing-writer":
			config.Melee.Writer = nil
		}
		if session, err := NewSession(ctx, config, journal, sessionNative{building}, &controlNative{generation: 1}, sessionNative{building}, boundary.FixedClock{}); err == nil {
			session.Close(ctx)
			t.Fatal("invalid configuration accepted", mode)
		}
		owner, err := AcquireProfile(ctx, dir)
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
	plan, _ := domain.NewPlan("plan", 1, []domain.Action{d, dispatch.Action})
	p, _ := domain.NewProgress(plan, "attack")
	state := store.PlanState{Spec: plan, Progress: []domain.Progress{p}}
	world := playerWorld(dispatch.Snapshot)
	if workerEligible(state, p.View(), ControlState{}, world) {
		t.Fatal("disabled pending attack eligible")
	}
	p, _ = p.Prepare(dispatch.Snapshot, 10)
	p, _ = p.MarkDispatched(dispatch.Snapshot, 10)
	if !workerEligible(state, p.View(), ControlState{}, world) {
		t.Fatal("disabled unresolved attack cannot reconcile")
	}
	world.Load = "replacement"
	if workerEligible(state, p.View(), ControlState{}, world) {
		t.Fatal("replacement world retargeted attack")
	}
	if native.Applies != 0 {
		t.Fatal("eligibility wrote native")
	}
}
