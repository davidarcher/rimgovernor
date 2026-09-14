package store

import (
	"context"
	"errors"
	"path/filepath"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// replacementShell is somewhere else entirely from buildRoomSubmissionRequest's
// 4,6 5x4 room, so relocating to it is a real move rather than a redescription.
func replacementShell(t *testing.T) domain.RoomShell {
	t.Helper()
	room, err := domain.NewRoomShell(domain.RoomBounds{X: 20, Z: 20, Width: 4, Height: 4}, "Wall", "Door", "WoodLog", domain.North, domain.ShelterRoom)
	if err != nil {
		t.Fatal(err)
	}
	return room
}

func relocateRequest(t *testing.T, id string) RelocateConstructionSubmissionRequest {
	t.Helper()
	return RelocateConstructionSubmissionRequest{RequestID: id, World: World{Colony: "colony", Load: "load", Map: 0}, IntentID: "shelter-01", Replacement: replacementShell(t)}
}

// Nothing of the original construction was ever dispatched, so nothing native
// exists to withdraw: Python's early exit re-issues the intent's shape outright,
// and so does this. The committed plan holds the replacement placements alone,
// with no cancellations and no ordering to enforce between them.
func TestRelocateConstructionFastPathWhenNothingWasIssued(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "relocate-fast.db")
	s := open(t, path)
	room := issueRoom(t, s, 0)
	first, created, err := s.SubmitRelocateConstruction(ctx, relocateRequest(t, "request"))
	if err != nil || !created {
		t.Fatal(first, created, err)
	}
	if !first.FastPath || len(first.Targets) != 0 || first.CancelAction != "" || len(first.CancelActionIDs()) != 0 {
		t.Fatal("unissued construction must relocate without cancellations", first)
	}
	if first.Source != room.Plan || first.Revision != 1 {
		t.Fatal("relocation lost its source construction", first)
	}
	state, err := s.LoadPlan(ctx, first.Plan)
	if err != nil {
		t.Fatal(err)
	}
	placements := replacementShell(t).Placements()
	if len(state.Spec.Actions()) != len(placements) || len(state.Spec.Dependencies()) != 0 {
		t.Fatal("fast path committed cancellations or ordering", len(state.Spec.Actions()), state.Spec.Dependencies())
	}
	for i, action := range state.Spec.Actions() {
		building, ok := action.Building()
		if !ok || building != placements[i] || action.ID() != first.BuildActionIDs()[i] {
			t.Fatal("committed placement is not the replacement's", i, action)
		}
	}
	// Every superseded placement is withdrawn from this controller's own work,
	// so nothing goes on to build the old room beside the new one.
	old, err := s.LoadPlan(ctx, room.Plan)
	if err != nil {
		t.Fatal(err)
	}
	for i, p := range old.Progress {
		if p.View().Stage != domain.Cancelled {
			t.Fatal("superseded placement is still live work", i, p.View().Stage)
		}
	}
	replay, created, err := s.SubmitRelocateConstruction(ctx, relocateRequest(t, "request"))
	if err != nil || created || !reflect.DeepEqual(replay, first) {
		t.Fatal(replay, created, err)
	}
	if err = s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	found, err := s.LookupRelocateConstructionSubmission(ctx, "request")
	if err != nil || !reflect.DeepEqual(found, first) {
		t.Fatal(found, err)
	}
}

// Something was issued, so both halves travel in one plan: a withdrawal per live
// order, then the replacement's placements, each of which requires the last
// withdrawal to have completed before it can be admitted.
func TestRelocateConstructionCombinedPathOrdersBuildAfterCancel(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "relocate-combined.db"))
	room := issueRoom(t, s, 3)
	v, created, err := s.SubmitRelocateConstruction(ctx, relocateRequest(t, "request"))
	if err != nil || !created || v.FastPath {
		t.Fatal(v, created, err)
	}
	if !reflect.DeepEqual(v.Targets, room.ActionIDs()[:3]) {
		t.Fatal("wrong placements withdrawn", v.Targets)
	}
	cancels, builds := v.CancelActionIDs(), v.BuildActionIDs()
	placements := replacementShell(t).Placements()
	if len(cancels) != 3 || cancels[0] != v.CancelAction || len(builds) != len(placements) || builds[0] != v.BuildAction {
		t.Fatal("derived identities", cancels, builds)
	}
	state, err := s.LoadPlan(ctx, v.Plan)
	if err != nil {
		t.Fatal(err)
	}
	actions := state.Spec.Actions()
	if len(actions) != len(cancels)+len(builds) {
		t.Fatal("plan does not hold both halves", len(actions))
	}
	source := buildRoomSubmissionRequest(t, "room-request").Room.Placements()
	for i := range cancels {
		cancel, ok := actions[i].ConstructionCancel()
		if !ok || actions[i].ID() != cancels[i] || !cancel.Matches(source[i]) {
			t.Fatal("withdrawal does not describe its superseded placement", i, actions[i])
		}
	}
	for i := range builds {
		building, ok := actions[len(cancels)+i].Building()
		if !ok || actions[len(cancels)+i].ID() != builds[i] || building != placements[i] {
			t.Fatal("replacement placement is wrong", i, actions[len(cancels)+i])
		}
	}
	// The withdrawals run in a chain and every placement requires the last of
	// them, Python's monolithic removal-then-replacement ordering. Pairing each
	// placement to each withdrawal would be 63504 edges at the largest shell,
	// against domain's 4096 bound; this is at most 503.
	want := map[domain.ActionDependency]bool{
		{Action: cancels[1], Requires: cancels[0]}: true,
		{Action: cancels[2], Requires: cancels[1]}: true,
	}
	for _, id := range builds {
		want[domain.ActionDependency{Action: id, Requires: cancels[2]}] = true
	}
	found := state.Spec.Dependencies()
	if len(found) != len(want) {
		t.Fatal("unexpected dependency count", len(found), len(want))
	}
	for _, d := range found {
		if !want[d] {
			t.Fatal("unexpected dependency", d)
		}
	}
	// The gate is real: a replacement placement cannot be admitted while the
	// withdrawals it requires are still open.
	target := roomScope(v.Plan)
	if err = state.Spec.CheckDependencies(builds[0], state.Progress, target, 10); !errors.Is(err, domain.ErrDependency) {
		t.Fatal("replacement admitted before its withdrawals completed", err)
	}
	if err = state.Spec.CheckDependencies(cancels[0], state.Progress, target, 10); err != nil {
		t.Fatal("first withdrawal is gated by nothing and must be admissible", err)
	}
	found2, err := s.LookupRelocateConstructionSubmission(ctx, "request")
	if err != nil || !reflect.DeepEqual(found2, v) {
		t.Fatal(found2, err)
	}
}

// A construction with both issued and never-issued placements relocates on the
// combined path: the issued ones are withdrawn natively and the rest are simply
// dropped from this controller's own work.
func TestRelocateConstructionSupersedesUndispatchedPlacements(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "relocate-mixed.db"))
	room := issueRoom(t, s, 2)
	v, _, err := s.SubmitRelocateConstruction(ctx, relocateRequest(t, "request"))
	if err != nil || v.FastPath || len(v.Targets) != 2 {
		t.Fatal(v, err)
	}
	old, err := s.LoadPlan(ctx, room.Plan)
	if err != nil {
		t.Fatal(err)
	}
	for i, p := range old.Progress {
		stage := p.View().Stage
		if i < 2 && stage != domain.AwaitingObservation {
			t.Fatal("issued placement was locally cancelled instead of withdrawn", i, stage)
		}
		if i >= 2 && stage != domain.Cancelled {
			t.Fatal("unissued placement still live", i, stage)
		}
	}
}

// Python refuses a relocation whose replacement is the construction it already
// is: native churn for no change is far more likely a mistake than a request.
func TestRelocateConstructionRefusesUnchangedGeometry(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "relocate-unchanged.db"))
	issueRoom(t, s, 0)
	request := relocateRequest(t, "request")
	request.Replacement = buildRoomSubmissionRequest(t, "room-request").Room
	if _, _, err := s.SubmitRelocateConstruction(ctx, request); err == nil {
		t.Fatal("unchanged geometry accepted")
	}
	var count int
	if err := s.db.QueryRow("SELECT count(*) FROM relocate_construction_submissions").Scan(&count); err != nil || count != 0 {
		t.Fatal("refused relocation left a record", count, err)
	}
}

// A completed placement cannot be read as consent to duplicate it elsewhere, and
// an uncertain receipt could see the wrong thing removed. Both refuse the whole
// relocation rather than moving part of a room.
func TestRelocateConstructionRefusesPartiallyResolvedConstruction(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "relocate-partial.db"))
	room := issueRoom(t, s, 3)
	ids := room.ActionIDs()
	if _, err := s.Observe(ctx, room.Plan, domain.Observation{Action: ids[0], Attempt: 1, Snapshot: roomScope(room.Plan), Tick: 11, Effect: domain.EffectCompleted}, roomScope(room.Plan)); err != nil {
		t.Fatal(err)
	}
	// Two of the three issued placements are still withdrawable; the completed
	// one is preserved, which makes the relocation partial and so refused.
	if _, _, err := s.SubmitRelocateConstruction(ctx, relocateRequest(t, "request")); err == nil {
		t.Fatal("partially completed construction relocated")
	}
	// The same refusal for an uncertain receipt, via cancellableTargets.
	uncertain := open(t, filepath.Join(t.TempDir(), "relocate-uncertain.db"))
	other := issueRoom(t, uncertain, 1)
	next := other.ActionIDs()[1]
	placement := buildRoomSubmissionRequest(t, "room-request").Room.Placements()[1]
	if _, err := uncertain.ReserveAndPrepare(ctx, other.Plan, next, Admission{Snapshot: roomScope(other.Plan), Tick: 10, Costs: []MaterialCost{{Definition: "WoodLog", Count: 5}}, Footprint: []domain.Cell{placement.Cell()}}); err != nil {
		t.Fatal(err)
	}
	if _, err := uncertain.Dispatch(ctx, other.Plan, next, roomScope(other.Plan), 10); err != nil {
		t.Fatal(err)
	}
	if _, err := uncertain.RecordReceipt(ctx, other.Plan, next, 1, domain.ReceiptUnknown); err != nil {
		t.Fatal(err)
	}
	if _, _, err := uncertain.SubmitRelocateConstruction(ctx, relocateRequest(t, "request")); err == nil {
		t.Fatal("uncertain placement relocated")
	}
}

// An undispatched construction that is no longer whole -- a placement cancelled
// on its own -- does not qualify for the fast path either, mirroring Python's
// requirement that the source step still be pending or blocked.
func TestRelocateConstructionRefusesResolvedUndispatchedConstruction(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "relocate-resolved.db"))
	room := issueRoom(t, s, 0)
	if _, err := s.Cancel(ctx, room.Plan, room.ActionIDs()[0]); err != nil {
		t.Fatal(err)
	}
	if _, _, err := s.SubmitRelocateConstruction(ctx, relocateRequest(t, "request")); err == nil {
		t.Fatal("partly resolved construction relocated")
	}
}

// The intent names the construction where it stands now, so a second relocation
// supersedes the first and a later cancellation withdraws the current orders,
// never the ones at the original location.
func TestRelocateConstructionChainsAndRedirectsTheIntent(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "relocate-chain.db"))
	room := issueRoom(t, s, 0)
	first, _, err := s.SubmitRelocateConstruction(ctx, relocateRequest(t, "first"))
	if err != nil {
		t.Fatal(err)
	}
	head, err := s.LookupConstructionIntent(ctx, first.Request.World, "shelter-01")
	if err != nil || head.Plan != first.Plan || head.Action != first.BuildAction || head.Room != first.Request.Replacement || head.Relocations != 1 {
		t.Fatal("intent did not follow its relocation", head, err)
	}
	further, err := domain.NewRoomShell(domain.RoomBounds{X: 30, Z: 30, Width: 4, Height: 5}, "Wall", "Door", "WoodLog", domain.East, domain.ShelterRoom)
	if err != nil {
		t.Fatal(err)
	}
	second := relocateRequest(t, "second")
	second.Replacement = further
	third, _, err := s.SubmitRelocateConstruction(ctx, second)
	if err != nil || third.Source != first.Plan {
		t.Fatal("second relocation did not supersede the first", third, err)
	}
	head, err = s.LookupConstructionIntent(ctx, second.World, "shelter-01")
	if err != nil || head.Plan != third.Plan || head.Room != further || head.Relocations != 2 {
		t.Fatal("intent did not follow the chain", head, err)
	}
	// The original build_room submission is untouched: relocation supersedes by
	// appending, never by rewriting the record a request ID replays to.
	original, err := s.LookupBuildRoomSubmission(ctx, "room-request")
	if err != nil || original.Plan != room.Plan || original.Request.Room != buildRoomSubmissionRequest(t, "room-request").Room {
		t.Fatal("relocation rewrote the construction it superseded", original, err)
	}
	// A cancellation of the intent now resolves to the current head. Nothing of
	// it was ever issued, so there is nothing left to withdraw.
	cancel, _, err := s.SubmitCancelConstruction(ctx, cancelConstructionRequest("cancel"))
	if err != nil || cancel.Source != third.Plan {
		t.Fatal("cancellation withdrew the superseded construction", cancel, err)
	}
}

func TestRelocateConstructionValidationAndScope(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "relocate-validation.db"))
	issueRoom(t, s, 0)
	for _, change := range []func(*RelocateConstructionSubmissionRequest){
		func(v *RelocateConstructionSubmissionRequest) { v.RequestID = "bad\x00id" },
		func(v *RelocateConstructionSubmissionRequest) { v.World.Map = -1 },
		func(v *RelocateConstructionSubmissionRequest) { v.World.Load = "" },
		func(v *RelocateConstructionSubmissionRequest) { v.IntentID = "" },
		func(v *RelocateConstructionSubmissionRequest) { v.IntentID = "not a valid intent" },
		func(v *RelocateConstructionSubmissionRequest) { v.Replacement = domain.RoomShell{} },
	} {
		request := relocateRequest(t, "request")
		change(&request)
		if _, _, err := s.SubmitRelocateConstruction(ctx, request); err == nil {
			t.Fatal("invalid request accepted")
		}
	}
	unknown := relocateRequest(t, "unknown-intent")
	unknown.IntentID = "shelter-99"
	if _, _, err := s.SubmitRelocateConstruction(ctx, unknown); !errors.Is(err, ErrNotFound) {
		t.Fatal("unknown intent resolved", err)
	}
	elsewhere := relocateRequest(t, "other-world")
	elsewhere.World.Map = 1
	if _, _, err := s.SubmitRelocateConstruction(ctx, elsewhere); !errors.Is(err, ErrNotFound) {
		t.Fatal("intent leaked across worlds", err)
	}
	if _, err := s.LookupRelocateConstructionSubmission(ctx, ""); err == nil {
		t.Fatal("invalid lookup id accepted")
	}
	if _, err := s.LookupRelocateConstructionSubmission(ctx, "absent"); !errors.Is(err, ErrNotFound) {
		t.Fatal("absent lookup", err)
	}
	if _, _, err := s.SubmitRelocateConstruction(ctx, relocateRequest(t, "request")); err != nil {
		t.Fatal(err)
	}
	changed := relocateRequest(t, "request")
	changed.World.Map = 1
	if _, _, err := s.SubmitRelocateConstruction(ctx, changed); !errors.Is(err, ErrConflict) {
		t.Fatal("semantic conflict accepted", err)
	}
	// One committed construction may be superseded exactly once; a second
	// request naming the same source is a conflict, not a silent fork.
	if _, _, err := s.SubmitRelocateConstruction(ctx, relocateRequest(t, "second-request")); err == nil {
		t.Fatal("two relocations superseded the same construction")
	}
}

func TestRelocateConstructionSubmissionAtomicFailure(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "relocate-atomic.db"))
	room := issueRoom(t, s, 0)
	var plans, actions int
	if err := s.db.QueryRow("SELECT (SELECT count(*) FROM plans),(SELECT count(*) FROM actions)").Scan(&plans, &actions); err != nil {
		t.Fatal(err)
	}
	if _, err := s.db.Exec("CREATE TRIGGER fail_relocate BEFORE INSERT ON relocate_construction_submissions BEGIN SELECT RAISE(ABORT,'fixture failure'); END"); err != nil {
		t.Fatal(err)
	}
	if _, _, err := s.SubmitRelocateConstruction(ctx, relocateRequest(t, "request")); err == nil {
		t.Fatal("trigger did not fail")
	}
	var nowPlans, nowActions int
	if err := s.db.QueryRow("SELECT (SELECT count(*) FROM plans),(SELECT count(*) FROM actions)").Scan(&nowPlans, &nowActions); err != nil {
		t.Fatal(err)
	}
	if nowPlans != plans || nowActions != actions {
		t.Fatal("partial transaction", nowPlans, nowActions)
	}
	// The superseding cancellation of the old plan's placements rolled back too.
	old, err := s.LoadPlan(ctx, room.Plan)
	if err != nil {
		t.Fatal(err)
	}
	for i, p := range old.Progress {
		if p.View().Stage != domain.Pending {
			t.Fatal("failed relocation left the superseded construction cancelled", i, p.View().Stage)
		}
	}
	if _, err := s.db.Exec("DROP TRIGGER fail_relocate"); err != nil {
		t.Fatal(err)
	}
	if _, created, err := s.SubmitRelocateConstruction(ctx, relocateRequest(t, "request")); err != nil || !created {
		t.Fatal(created, err)
	}
}
