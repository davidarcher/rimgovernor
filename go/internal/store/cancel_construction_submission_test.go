package store

import (
	"context"
	"errors"
	"path/filepath"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func cancelConstructionRequest(id string) CancelConstructionSubmissionRequest {
	return CancelConstructionSubmissionRequest{RequestID: id, World: World{Colony: "colony", Load: "load", Map: 0}, IntentID: "shelter-01"}
}

// roomScope is the authority the committed build-room plan's own placements are
// dispatched under; a placement can only reach a native order through its plan.
func roomScope(plan domain.PlanID) domain.GenerationSnapshot {
	s := scope()
	s.Plan, s.Revision, s.Native = plan, 1, 4
	return s
}

// issueRoom commits a room and drives the first count placements to dispatched,
// which is the only state in which a placement has a native order to withdraw.
func issueRoom(t *testing.T, s *Store, count int) BuildRoomSubmission {
	t.Helper()
	ctx := context.Background()
	room, _, err := s.SubmitBuildRoom(ctx, buildRoomSubmissionRequest(t, "room-request"))
	if err != nil {
		t.Fatal(err)
	}
	placements := buildRoomSubmissionRequest(t, "room-request").Room.Placements()
	for i, id := range room.ActionIDs()[:count] {
		if _, err = s.ReserveAndPrepare(ctx, room.Plan, id, Admission{Snapshot: roomScope(room.Plan), Tick: 10, Costs: []MaterialCost{{Definition: "WoodLog", Count: 5}}, Footprint: []domain.Cell{placements[i].Cell()}}); err != nil {
			t.Fatal(err)
		}
		if _, err = s.Dispatch(ctx, room.Plan, id, roomScope(room.Plan), 10); err != nil {
			t.Fatal(err)
		}
		// A dispatched placement stays unresolved until its receipt lands; an
		// accepted one is the confirmed pending order cancellation targets.
		if _, err = s.RecordReceipt(ctx, room.Plan, id, 1, domain.ReceiptAccepted); err != nil {
			t.Fatal(err)
		}
	}
	return room
}

// Nothing dispatched means nothing native was ever placed, so there is nothing
// to withdraw. Python reports that as observed_absent rather than an error, and
// so does this: the submission commits, carries no plan, and stays replayable.
func TestCancelConstructionObservedAbsentWhenNothingIsPending(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "cancel-absent.db")
	s := open(t, path)
	room := issueRoom(t, s, 0)
	first, created, err := s.SubmitCancelConstruction(ctx, cancelConstructionRequest("request"))
	if err != nil || !created {
		t.Fatal(first, created, err)
	}
	if !first.ObservedAbsent || first.Plan != "" || first.Action != "" || len(first.Targets) != 0 || len(first.ActionIDs()) != 0 {
		t.Fatal("unissued placements must cancel to nothing", first)
	}
	if first.Source != room.Plan || first.Revision != 1 {
		t.Fatal("cancellation lost its source intent", first)
	}
	replay, created, err := s.SubmitCancelConstruction(ctx, cancelConstructionRequest("request"))
	if err != nil || created || !reflect.DeepEqual(replay, first) {
		t.Fatal(replay, created, err)
	}
	if err = s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	found, err := s.LookupCancelConstructionSubmission(ctx, "request")
	if err != nil || !reflect.DeepEqual(found, first) {
		t.Fatal(found, err)
	}
}

// Only the placements that actually reached native are withdrawn, one committed
// cancellation each, carrying that placement's exact definition, cell and
// material -- the triple the executor re-resolves to a live thing ID.
func TestCancelConstructionCommitsOneCancellationPerOpenPlacement(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "cancel-open.db"))
	room := issueRoom(t, s, 3)
	first, created, err := s.SubmitCancelConstruction(ctx, cancelConstructionRequest("request"))
	if err != nil || !created || first.ObservedAbsent {
		t.Fatal(first, created, err)
	}
	if len(first.Targets) != 3 || !reflect.DeepEqual(first.Targets, room.ActionIDs()[:3]) {
		t.Fatal("wrong placements selected", first.Targets)
	}
	ids := first.ActionIDs()
	if len(ids) != 3 || ids[0] != first.Action {
		t.Fatal("derived cancellation identities", ids)
	}
	state, err := s.LoadPlan(ctx, first.Plan)
	if err != nil || len(state.Spec.Actions()) != 3 {
		t.Fatal(state, err)
	}
	placements := buildRoomSubmissionRequest(t, "room-request").Room.Placements()
	for i, action := range state.Spec.Actions() {
		cancel, ok := action.ConstructionCancel()
		if !ok || action.ID() != ids[i] {
			t.Fatal("committed action is not a cancellation", i, action)
		}
		if !cancel.Matches(placements[i]) {
			t.Fatal("cancellation does not describe its placement", i, cancel)
		}
		if state.Progress[i].View().Stage != domain.Pending {
			t.Fatal("committed cancellation is not pending", i)
		}
	}
	found, err := s.LookupCancelConstructionSubmission(ctx, "request")
	if err != nil || !reflect.DeepEqual(found, first) {
		t.Fatal(found, err)
	}
	_ = room
}

// A finished building is never touched, and an already-cancelled or failed
// order has nothing left to remove -- Python's "completed buildings and already
// removed orders are preserved".
func TestCancelConstructionPreservesResolvedPlacements(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "cancel-preserve.db"))
	room := issueRoom(t, s, 3)
	ids := room.ActionIDs()
	if _, err := s.Observe(ctx, room.Plan, domain.Observation{Action: ids[0], Attempt: 1, Snapshot: roomScope(room.Plan), Tick: 11, Effect: domain.EffectCompleted}, roomScope(room.Plan)); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Observe(ctx, room.Plan, domain.Observation{Action: ids[1], Attempt: 1, Snapshot: roomScope(room.Plan), Tick: 11, Effect: domain.EffectUnsuccessful, UnsuccessfulReason: domain.OutcomeNotAchieved}, roomScope(room.Plan)); err != nil {
		t.Fatal(err)
	}
	v, created, err := s.SubmitCancelConstruction(ctx, cancelConstructionRequest("request"))
	if err != nil || !created {
		t.Fatal(v, created, err)
	}
	if len(v.Targets) != 1 || v.Targets[0] != ids[2] {
		t.Fatal("resolved placements were not preserved", v.Targets)
	}
}

// An uncertain receipt is the one case that refuses outright: cancelling
// against a write whose outcome is unknown could remove whatever stands there
// now rather than the order the player meant.
func TestCancelConstructionRefusesUncertainPlacements(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "cancel-uncertain.db"))
	room := issueRoom(t, s, 1)
	// A second placement dispatched with an unknown receipt: native may or may
	// not hold an order for it, so the whole cancellation refuses.
	next := room.ActionIDs()[1]
	placement := buildRoomSubmissionRequest(t, "room-request").Room.Placements()[1]
	if _, err := s.ReserveAndPrepare(ctx, room.Plan, next, Admission{Snapshot: roomScope(room.Plan), Tick: 10, Costs: []MaterialCost{{Definition: "WoodLog", Count: 5}}, Footprint: []domain.Cell{placement.Cell()}}); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, room.Plan, next, roomScope(room.Plan), 10); err != nil {
		t.Fatal(err)
	}
	if _, _, err := s.SubmitCancelConstruction(ctx, cancelConstructionRequest("request")); err == nil {
		t.Fatal("placement awaiting a receipt accepted")
	}
	if _, err := s.RecordReceipt(ctx, room.Plan, next, 1, domain.ReceiptUnknown); err != nil {
		t.Fatal(err)
	}
	if _, _, err := s.SubmitCancelConstruction(ctx, cancelConstructionRequest("request")); err == nil {
		t.Fatal("uncertain placement accepted")
	}
	var count int
	if err := s.db.QueryRow("SELECT count(*) FROM cancel_construction_submissions").Scan(&count); err != nil || count != 0 {
		t.Fatal("refused cancellation left a record", count, err)
	}
}

func TestCancelConstructionSubmissionValidationAndScope(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "cancel-validation.db"))
	issueRoom(t, s, 2)
	for _, change := range []func(*CancelConstructionSubmissionRequest){
		func(v *CancelConstructionSubmissionRequest) { v.RequestID = "bad\x00id" },
		func(v *CancelConstructionSubmissionRequest) { v.World.Map = -1 },
		func(v *CancelConstructionSubmissionRequest) { v.World.Load = "" },
		func(v *CancelConstructionSubmissionRequest) { v.IntentID = "" },
		func(v *CancelConstructionSubmissionRequest) { v.IntentID = "not a valid intent" },
	} {
		request := cancelConstructionRequest("request")
		change(&request)
		if _, _, err := s.SubmitCancelConstruction(ctx, request); err == nil {
			t.Fatal("invalid request accepted")
		}
	}
	// An intent nobody submitted, and one submitted in another world, are both
	// simply absent: cancellation resolves within exactly one world.
	unknown := cancelConstructionRequest("unknown-intent")
	unknown.IntentID = "shelter-99"
	if _, _, err := s.SubmitCancelConstruction(ctx, unknown); !errors.Is(err, ErrNotFound) {
		t.Fatal("unknown intent resolved", err)
	}
	elsewhere := cancelConstructionRequest("other-world")
	elsewhere.World.Map = 1
	if _, _, err := s.SubmitCancelConstruction(ctx, elsewhere); !errors.Is(err, ErrNotFound) {
		t.Fatal("intent leaked across worlds", err)
	}
	if _, err := s.LookupCancelConstructionSubmission(ctx, ""); err == nil {
		t.Fatal("invalid lookup id accepted")
	}
	if _, err := s.LookupCancelConstructionSubmission(ctx, "absent"); !errors.Is(err, ErrNotFound) {
		t.Fatal("absent lookup", err)
	}
	// Replaying one request id with different content is a conflict, and one
	// world-scoped intent may only be cancelled once.
	if _, _, err := s.SubmitCancelConstruction(ctx, cancelConstructionRequest("request")); err != nil {
		t.Fatal(err)
	}
	changed := cancelConstructionRequest("request")
	changed.World.Map = 1
	if _, _, err := s.SubmitCancelConstruction(ctx, changed); !errors.Is(err, ErrConflict) {
		t.Fatal("semantic conflict accepted", err)
	}
	if _, _, err := s.SubmitCancelConstruction(ctx, cancelConstructionRequest("second-request")); !errors.Is(err, ErrConflict) {
		t.Fatal("duplicate world-scoped cancellation accepted", err)
	}
}

func TestCancelConstructionSubmissionAtomicFailure(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "cancel-atomic.db"))
	issueRoom(t, s, 2)
	var plans, actions int
	if err := s.db.QueryRow("SELECT (SELECT count(*) FROM plans),(SELECT count(*) FROM actions)").Scan(&plans, &actions); err != nil {
		t.Fatal(err)
	}
	if _, err := s.db.Exec("CREATE TRIGGER fail_cancel_construction BEFORE INSERT ON cancel_construction_submissions BEGIN SELECT RAISE(ABORT,'fixture failure'); END"); err != nil {
		t.Fatal(err)
	}
	if _, _, err := s.SubmitCancelConstruction(ctx, cancelConstructionRequest("request")); err == nil {
		t.Fatal("trigger did not fail")
	}
	var nowPlans, nowActions int
	if err := s.db.QueryRow("SELECT (SELECT count(*) FROM plans),(SELECT count(*) FROM actions)").Scan(&nowPlans, &nowActions); err != nil {
		t.Fatal(err)
	}
	if nowPlans != plans || nowActions != actions {
		t.Fatal("partial transaction", nowPlans, nowActions)
	}
	if _, err := s.db.Exec("DROP TRIGGER fail_cancel_construction"); err != nil {
		t.Fatal(err)
	}
	if _, created, err := s.SubmitCancelConstruction(ctx, cancelConstructionRequest("request")); err != nil || !created {
		t.Fatal(created, err)
	}
}

// Dispatching a committed cancellation requires the typed admission that
// records the exact native target one inspection resolved; the untyped
// transition path cannot stand in for it.
func TestConstructionCancelRequiresTypedAdmission(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "cancel-admission.db"))
	issueRoom(t, s, 1)
	v, _, err := s.SubmitCancelConstruction(ctx, cancelConstructionRequest("request"))
	if err != nil {
		t.Fatal(err)
	}
	action := v.ActionIDs()[0]
	target := roomScope(v.Plan)
	if _, err = s.Prepare(ctx, v.Plan, action, target, 10); err == nil {
		t.Fatal("untyped preparation accepted")
	}
	if _, err = s.Dispatch(ctx, v.Plan, action, target, 10); err == nil {
		t.Fatal("dispatch without admission accepted")
	}
	placement := buildRoomSubmissionRequest(t, "room-request").Room.Placements()[0]
	admission := ConstructionCancelAdmission{Snapshot: target, Tick: 10, ThingID: "Thing_1", SnapshotToken: "token-1", Def: placement.Definition(), Stuff: placement.Stuff()}
	if _, err = s.PrepareConstructionCancel(ctx, v.Plan, action, admission); err != nil {
		t.Fatal(err)
	}
	if _, err = s.Dispatch(ctx, v.Plan, action, target, 10); err != nil {
		t.Fatal(err)
	}
	// An admission describing a different placement is not this action's.
	wrong := admission
	wrong.Def = "Other"
	if _, err = s.PrepareConstructionCancel(ctx, v.Plan, action, wrong); err == nil {
		t.Fatal("mismatched admission accepted")
	}
}
