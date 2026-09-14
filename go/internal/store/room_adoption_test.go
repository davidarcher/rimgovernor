package store

import (
	"context"
	"errors"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func adoption(t *testing.T) domain.RoomAdoption {
	t.Helper()
	r, err := domain.NewRoomAdoption(domain.RoomBounds{X: 10, Z: 10, Width: 5, Height: 5}, domain.South, nil, domain.Cell{}, false)
	if err != nil {
		t.Fatal(err)
	}
	return r
}

func adoptionRequest(t *testing.T, id, intent string) RoomAdoptionSubmissionRequest {
	t.Helper()
	return RoomAdoptionSubmissionRequest{RequestID: id, IntentID: intent, Adoption: adoption(t),
		Evidence: domain.AdoptionEvidence{RoomID: "native-room-1", Cells: 9, Role: "Bedroom", RoleLabel: "bedroom"},
		Snapshot: scope(), Tick: 10}
}

// TestRoomAdoptionCompletesShelterGoal is the heart of this slice: an adoption
// must leave a goal validly satisfied, which domain.Goal only permits with
// observed recovery.
func TestRoomAdoptionCompletesShelterGoal(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "room-adoption.db")
	s := open(t, path)
	q := adoptionRequest(t, "adopt-one", "shelter-a")
	first, created, err := s.SubmitRoomAdoption(ctx, q)
	if err != nil || !created {
		t.Fatal(first, created, err)
	}
	g := first.State.Goal
	if g.Source != domain.PlayerGoal || g.Status != domain.GoalSatisfied || g.Need != domain.NeedRecovered || !g.RecoveryObserved {
		t.Fatal("adoption did not leave the shelter goal satisfied with observed recovery", g)
	}
	if err = g.Validate(); err != nil {
		t.Fatal("satisfied goal violates its own invariant", err)
	}
	if g.Priority != 2 || g.Snapshot != scope() || g.Tick != 10 || first.Goal != g.ID || first.State.Revision == 0 {
		t.Fatal(first)
	}
	// The adoption binds the same per-world, per-kind player goal CreateGoal
	// binds, so the two commands can never disagree about the shelter goal.
	bindings, err := s.PlayerGoals(ctx, q.World())
	if err != nil || bindings[domain.EnsureInitialShelterGoal] != g.ID || len(bindings) != 1 {
		t.Fatal(bindings, err)
	}
	// The adopted room is the world's preferred shelter.
	preferred, err := s.AdoptedShelter(ctx, q.World())
	if err != nil || preferred.Request.RequestID != q.RequestID || preferred.Goal != g.ID {
		t.Fatal(preferred, err)
	}
	replay, created, err := s.SubmitRoomAdoption(ctx, q)
	if err != nil || created || !replay.Request.Same(q) || replay.Goal != first.Goal {
		t.Fatal(replay, created, err)
	}
	for _, change := range []func(*RoomAdoptionSubmissionRequest){
		func(v *RoomAdoptionSubmissionRequest) { v.IntentID = "shelter-b" },
		func(v *RoomAdoptionSubmissionRequest) { v.Tick = 11 },
		func(v *RoomAdoptionSubmissionRequest) { v.Snapshot.Direction = 3 },
		func(v *RoomAdoptionSubmissionRequest) { v.Evidence.RoomID = "native-room-2" },
		func(v *RoomAdoptionSubmissionRequest) {
			r, err := domain.NewRoomAdoption(domain.RoomBounds{X: 20, Z: 20, Width: 5, Height: 5}, domain.South, nil, domain.Cell{}, false)
			if err != nil {
				t.Fatal(err)
			}
			v.Adoption = r
		},
	} {
		changed := q
		change(&changed)
		if _, _, err := s.SubmitRoomAdoption(ctx, changed); !errors.Is(err, ErrConflict) {
			t.Fatal("semantic conflict accepted", err)
		}
	}
	if err = s.Close(); err != nil {
		t.Fatal(err)
	}
	// The record and its completed goal survive a reopen unchanged.
	s = open(t, path)
	found, err := s.LookupRoomAdoptionSubmission(ctx, q.RequestID)
	if err != nil || !found.Request.Same(q) || found.Goal != first.Goal || found.State.Goal.Status != domain.GoalSatisfied {
		t.Fatal(found, err)
	}
}

// TestRoomAdoptionRefusesConstructionIntent ports Python's "Use a new
// room-adoption intent ID; existing construction history must remain intact".
func TestRoomAdoptionRefusesConstructionIntent(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "room-adoption-intent.db"))
	shell, err := domain.NewRoomShell(domain.RoomBounds{X: 10, Z: 10, Width: 5, Height: 5}, "Wall", "Door", "WoodLog", domain.South, domain.ShelterRoom)
	if err != nil {
		t.Fatal(err)
	}
	built, _, err := s.SubmitBuildRoom(ctx, BuildRoomSubmissionRequest{RequestID: "build-one", World: World{Colony: scope().Colony, Load: scope().Load, Map: scope().Map}, IntentID: "shelter-a", Room: shell})
	if err != nil {
		t.Fatal(built, err)
	}
	if _, _, err = s.SubmitRoomAdoption(ctx, adoptionRequest(t, "adopt-one", "shelter-a")); err == nil {
		t.Fatal("adoption overwrote an intent that already carries construction history")
	}
	// A different intent is unaffected: adoption and construction coexist.
	if _, created, err := s.SubmitRoomAdoption(ctx, adoptionRequest(t, "adopt-two", "shelter-b")); err != nil || !created {
		t.Fatal(created, err)
	}
}

// TestRoomAdoptionMovesTheSameGoal checks that adopting a second room re-reviews
// the world's existing shelter goal rather than accumulating a second one.
func TestRoomAdoptionMovesTheSameGoal(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "room-adoption-move.db"))
	first, _, err := s.SubmitRoomAdoption(ctx, adoptionRequest(t, "adopt-one", "shelter-a"))
	if err != nil {
		t.Fatal(err)
	}
	second, created, err := s.SubmitRoomAdoption(ctx, adoptionRequest(t, "adopt-two", "shelter-b"))
	if err != nil || !created {
		t.Fatal(second, created, err)
	}
	if second.Goal != first.Goal {
		t.Fatal("a second adoption created a second shelter goal", first.Goal, second.Goal)
	}
	if second.State.Goal.Status != domain.GoalSatisfied {
		t.Fatal("second adoption did not leave the goal satisfied", second.State.Goal)
	}
	preferred, err := s.AdoptedShelter(ctx, second.Request.World())
	if err != nil || preferred.Request.RequestID != "adopt-two" {
		t.Fatal("preferred shelter did not move to the newly adopted room", preferred, err)
	}
}

// TestRoomAdoptionRefusesOpenShelterWork checks that a shelter goal whose
// construction is still running cannot be declared complete: the player cannot
// have inspected a finished room that is still being built.
func TestRoomAdoptionRefusesOpenShelterWork(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "room-adoption-open.db"))
	activated, _, err := s.SubmitGoalCreate(ctx, goalCreateRequest("create-shelter", domain.EnsureInitialShelterGoal))
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitGoalMethod(ctx, activated.Goal, activated.State.Revision, "method", plan(t, "shelter-plan", "shelter-action")); err != nil {
		t.Fatal(err)
	}
	if _, _, err = s.SubmitRoomAdoption(ctx, adoptionRequest(t, "adopt-one", "shelter-a")); err == nil {
		t.Fatal("adoption completed a shelter goal whose construction is still running")
	}
}

// TestRoomAdoptionRejectsMismatchedEvidence checks the one cross-field rule this
// controller adds: the native cell count must match the interior described.
func TestRoomAdoptionRejectsMismatchedEvidence(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "room-adoption-evidence.db"))
	q := adoptionRequest(t, "adopt-one", "shelter-a")
	q.Evidence.Cells = 8
	if _, _, err := s.SubmitRoomAdoption(ctx, q); err == nil {
		t.Fatal("evidence naming a different room size was accepted")
	}
}

func TestRoomAdoptionNonrectangularRoundTrip(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "room-adoption-shape.db"))
	interior := []domain.Cell{}
	for z := int32(11); z < 14; z++ {
		for x := int32(11); x < 14; x++ {
			interior = append(interior, domain.Cell{X: x, Z: z})
		}
	}
	r, err := domain.NewRoomAdoption(domain.RoomBounds{X: 10, Z: 10, Width: 5, Height: 5}, domain.South, interior, domain.Cell{X: 12, Z: 10}, true)
	if err != nil {
		t.Fatal(err)
	}
	q := adoptionRequest(t, "adopt-shape", "shelter-a")
	q.Adoption = r
	stored, created, err := s.SubmitRoomAdoption(ctx, q)
	if err != nil || !created {
		t.Fatal(stored, created, err)
	}
	found, err := s.LookupRoomAdoptionSubmission(ctx, q.RequestID)
	if err != nil || !found.Request.Same(q) {
		t.Fatal("nonrectangular adoption did not survive storage", found, err)
	}
	if found.Request.Adoption.Rectangular() || !found.Request.Adoption.EntranceCellSet() {
		t.Fatal("stored adoption lost its exact interior or door")
	}
}
