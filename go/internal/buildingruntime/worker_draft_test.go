package buildingruntime

import (
	"context"
	"errors"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func workerDraft(t *testing.T, db *store.Store, id string, stage domain.Stage, sibling bool) domain.ProgressView {
	t.Helper()
	ctx := context.Background()
	draft, _ := domain.NewOwnedDraft(domain.PawnID("pawn-" + id))
	action, _ := domain.NewOwnedDraftAction(domain.ActionID(id), draft)
	actions := []domain.Action{action}
	if sibling {
		building, _ := domain.NewBuilding("Wall", domain.Cell{X: 1, Z: 1}, domain.North, "WoodLog")
		other, _ := domain.NewBuildingAction(domain.ActionID(id+"-building"), building)
		actions = append(actions, other)
	}
	plan, _ := domain.NewPlan(domain.PlanID(id+"-plan"), 1, actions)
	if err := db.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: plan.ID(), Revision: 1, Native: 1}
	if stage != domain.Pending {
		if _, err := db.PrepareDraft(ctx, plan.ID(), action.ID(), store.DraftAdmission{Snapshot: snapshot, Tick: 1, Pawn: draft.Pawn(), PawnSnapshotToken: "token"}); err != nil {
			t.Fatal(err)
		}
	}
	if stage != domain.Pending && stage != domain.Prepared {
		if _, err := db.Dispatch(ctx, plan.ID(), action.ID(), snapshot, 1); err != nil {
			t.Fatal(err)
		}
		if stage != domain.Dispatched {
			id, _ := db.Identity(ctx)
			claim := domain.DraftClaim{Action: action.ID(), Attempt: 1, Pawn: draft.Pawn(), Claim: domain.DraftClaimID("claim-" + id), Session: domain.ControllerSessionID(id), Origin: snapshot}
			if _, err := db.RecordDraftReceipt(ctx, plan.ID(), action.ID(), 1, domain.ReceiptAccepted, domain.Known(claim)); err != nil {
				t.Fatal(err)
			}
			if stage == domain.Cancelled {
				if _, err := db.Cancel(ctx, plan.ID(), action.ID()); err != nil {
					t.Fatal(err)
				}
			} else if stage == domain.Completed || stage == domain.Unsuccessful {
				effect := domain.EffectCompleted
				reason := domain.UnsuccessfulReason("")
				if stage == domain.Unsuccessful {
					effect = domain.EffectUnsuccessful
					reason = domain.NativeInterrupted
				}
				if _, err := db.ObserveDraft(ctx, plan.ID(), domain.Observation{Action: action.ID(), Attempt: 1, Snapshot: snapshot, Tick: 1, Causality: domain.AfterDispatch, Effect: effect, UnsuccessfulReason: reason}, snapshot, domain.Known(claim)); err != nil {
					t.Fatal(err)
				}
			}
		}
	}
	state, err := db.LoadPlan(ctx, plan.ID())
	if err != nil {
		t.Fatal(err)
	}
	view := state.Progress[0].View()
	if stage == domain.Pending {
		view.Snapshot = snapshot
	}
	return view
}
func workerDraftResult(db *store.Store, ctx context.Context, plan domain.PlanID, action domain.ActionID) (executor.Result, error) {
	state, err := db.LoadPlan(ctx, plan)
	if err != nil {
		return executor.Result{}, err
	}
	for _, p := range state.Progress {
		if p.View().Action == action {
			return executor.Result{Progress: p}, nil
		}
	}
	return executor.Result{}, store.ErrNotFound
}

func TestWorkerDraftLiveAndCleanupEligibility(t *testing.T) {
	t.Parallel()
	for _, kind := range []string{"pending", "completed", "cancelled", "failed", "disabled", "direction", "world", "plan", "multi-active", "multi-failed"} {
		t.Run(kind, func(t *testing.T) {
			w, f, db := workerFixture(t)
			stage := domain.Completed
			if kind == "pending" {
				stage = domain.Pending
			}
			if kind == "cancelled" {
				stage = domain.Cancelled
			}
			if kind == "failed" {
				stage = domain.Unsuccessful
			}
			if kind == "disabled" || kind == "direction" || kind == "world" || kind == "plan" {
				stage = domain.Dispatched
			}
			multi := kind == "multi-active" || kind == "multi-failed"
			v := workerDraft(t, db, "draft", stage, multi)
			scope := v.Snapshot
			if kind == "direction" {
				scope.Native++
			}
			if kind == "plan" {
				scope.Plan = "another"
			}
			f.state = ControlState{Enabled: kind != "disabled", ObservationKnown: true, Snapshot: scope}
			if kind == "world" {
				w.player.worlds = &playerWorldSource{world: store.World{Colony: "replacement", Load: "load", Map: 0}}
			}
			if kind == "multi-failed" {
				if _, err := db.Cancel(context.Background(), v.Plan, "draft-building"); err != nil {
					t.Fatal(err)
				}
			}
			f.run = func(ctx context.Context, p domain.PlanID, a domain.ActionID) (executor.Result, error) {
				return workerDraftResult(db, ctx, p, a)
			}
			f.cleanup = f.run
			if err := w.step(context.Background(), time.Now()); err != nil {
				t.Fatal(err)
			}
			expectCleanup := kind != "pending" && kind != "multi-active"
			if expectCleanup && f.cleanups.Load() != 1 {
				t.Fatal("cleanup not selected", f.cleanups.Load())
			}
			if !expectCleanup && f.cleanups.Load() != 0 {
				t.Fatal("useful draft cleaned")
			}
			if expectCleanup && (f.observes.Load() != 0 || f.runs.Load() != 0) {
				t.Fatal("cleanup entered ordinary target/run")
			}
			if kind == "pending" && f.runs.Load() != 1 {
				t.Fatal("pending draft not run")
			}
			if f.acquires.Load() != 0 {
				t.Fatal("worker acquired")
			}
		})
	}
}

func TestWorkerCleanupFairnessAndTerminalPruning(t *testing.T) {
	t.Parallel()
	w, f, db := workerFixture(t)
	a := workerDraft(t, db, "a", domain.Dispatched, false)
	b := workerDraft(t, db, "b", domain.Completed, false)
	seen := map[domain.ActionID]int{}
	f.cleanup = func(ctx context.Context, p domain.PlanID, id domain.ActionID) (executor.Result, error) {
		seen[id]++
		result, err := workerDraftResult(db, ctx, p, id)
		if id == a.Action {
			return result, executor.ErrHeld
		}
		return result, err
	}
	now := time.Now()
	for i := 0; i < 2; i++ {
		_ = w.step(context.Background(), now)
	}
	if seen[a.Action] != 1 || seen[b.Action] != 1 {
		t.Fatal("unavailable draft starved sibling", seen)
	}
	for i := 0; i < 10; i++ {
		_ = w.step(context.Background(), now)
	}
	if f.cleanups.Load() != 2 {
		t.Fatal("backoff ignored")
	}
	observed := a.Snapshot
	observed.Load = "replacement"
	if _, err := db.ObserveDraftScopeSupersession(context.Background(), a.Plan, domain.DraftScopeSupersession{Action: a.Action, Attempt: 1, Origin: a.Snapshot, Observed: observed, Tick: 0}); err != nil {
		t.Fatal(err)
	}
	_ = w.step(context.Background(), now)
	if _, ok := w.waits[a.Action]; ok {
		t.Fatal("terminal cleanup wait retained")
	}
	if f.runs.Load() != 0 || f.observes.Load() != 0 {
		t.Fatal("superseded draft resumed ordinary work")
	}
}

func TestWorkerRunToCleanupResetsCooldown(t *testing.T) {
	t.Parallel()
	w, f, db := workerFixture(t)
	v := workerDraft(t, db, "draft", domain.Dispatched, false)
	f.state = ControlState{Enabled: true, ObservationKnown: true, Snapshot: v.Snapshot}
	f.run = func(ctx context.Context, p domain.PlanID, a domain.ActionID) (executor.Result, error) {
		return workerDraftResult(db, ctx, p, a)
	}
	f.cleanup = f.run
	now := time.Now()
	if err := w.step(context.Background(), now); err != nil {
		t.Fatal(err)
	}
	if f.runs.Load() != 1 {
		t.Fatal("run not selected")
	}
	// Change the mode while retaining the same cached authority values. A prior
	// ordinary polling cooldown cannot delay newly required cleanup.
	f.state.Enabled = false
	if err := w.step(context.Background(), now); err != nil {
		t.Fatal(err)
	}
	if f.cleanups.Load() != 1 || f.observes.Load() != 0 {
		t.Fatal("cleanup delayed or retargeted")
	}
}

func TestWorkerFailedWorldReadStillAttemptsIndependentCleanup(t *testing.T) {
	t.Parallel()
	for _, cleanupFails := range []bool{false, true} {
		name := "fresh-cleanup-world"
		if cleanupFails {
			name = "cleanup-identity-unavailable"
		}
		t.Run(name, func(t *testing.T) {
			w, f, db := workerFixture(t)
			v := workerDraft(t, db, "draft", domain.Dispatched, false)
			f.state = ControlState{Enabled: true, ObservationKnown: true, Snapshot: v.Snapshot}
			worldErr := errors.New("shared world unavailable")
			cleanupErr := errors.New("cleanup identity unavailable")
			w.player.worlds = &playerWorldSource{world: store.World{Colony: "colony", Load: "load", Map: 0}, err: worldErr}
			f.cleanup = func(ctx context.Context, p domain.PlanID, a domain.ActionID) (executor.Result, error) {
				if f.State().Enabled {
					t.Fatal("permission retained")
				}
				if cleanupFails {
					return executor.Result{}, cleanupErr
				}
				actual := v.Snapshot
				actual.Load = "fresh-replacement"
				progress, err := db.ObserveDraftScopeSupersession(ctx, p, domain.DraftScopeSupersession{Action: a, Attempt: 1, Origin: v.Snapshot, Observed: actual, Tick: 0})
				return executor.Result{Progress: progress}, err
			}
			err := w.step(context.Background(), time.Now())
			if !errors.Is(err, worldErr) || cleanupFails && !errors.Is(err, cleanupErr) {
				t.Fatal(err)
			}
			if f.cleanups.Load() != 1 || f.runs.Load() != 0 || f.observes.Load() != 0 || f.acquires.Load() != 0 {
				t.Fatal("wrong work path")
			}
			if !cleanupFails {
				state, _ := db.LoadPlan(context.Background(), v.Plan)
				cleanup, _ := state.Progress[0].View().DraftCleanup.Value()
				if cleanup.Stage != domain.DraftSuperseded {
					t.Fatal(cleanup)
				}
			}
		})
	}
}

func TestWorkerUnavailableCleanupDoesNotStarveCurrentBuilding(t *testing.T) {
	t.Parallel()
	w, f, db := workerFixture(t)
	workerDraft(t, db, "old-draft", domain.Completed, false)
	current := workerPending(t, w, "current-building", false)
	f.state = ControlState{Enabled: true, ObservationKnown: true, Snapshot: current.Snapshot}
	f.cleanup = func(context.Context, domain.PlanID, domain.ActionID) (executor.Result, error) {
		return executor.Result{}, executor.ErrHeld
	}
	f.run = func(ctx context.Context, p domain.PlanID, a domain.ActionID) (executor.Result, error) {
		return workerDraftResult(db, ctx, p, a)
	}
	now := time.Now()
	for i := 0; i < 2; i++ {
		_ = w.step(context.Background(), now)
	}
	if f.cleanups.Load() != 1 || f.runs.Load() != 1 || f.observes.Load() != 0 || f.acquires.Load() != 0 {
		t.Fatal("mixed work lost fairness", f.cleanups.Load(), f.runs.Load())
	}
}
