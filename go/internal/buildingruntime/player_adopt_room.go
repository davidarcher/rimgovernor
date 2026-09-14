package buildingruntime

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// SubmitRoomAdoption records explicit player intent to claim one already-built
// native room as the colony's shelter, under the shared player gate.
//
// This is the only player command in the G01.08 family that completes a goal
// rather than opening work: it issues no native call, composes no plan and
// dispatches nothing, because the room it names is already built. Python says
// the same in its own reply -- "Existing construction preserved; no new
// construction issued".
//
// Like every other player command it acquires no authority. The world half of
// the requested snapshot is checked against live native identity before
// submission, the same gate SubmitGoalCreate uses; the direction, plan revision
// and tick in the snapshot are the caller's.
//
// The room's geometry and the native room it names are the player's own
// inspection, taken on their word exactly as SubmitBuildRoom takes its
// "already-observed rectangle". This gate has no observation seam to check them
// against -- a Player holds a journal and a WorldSource, never a bridge client
// -- so Python's submission-time re-observation has no counterpart here; see
// go/README.md's G01.08 entry.
func (p *Player) SubmitRoomAdoption(ctx context.Context, request store.RoomAdoptionSubmissionRequest) (store.RoomAdoptionSubmission, bool, error) {
	call, epoch, done, err := p.enter(ctx, false)
	if err != nil {
		return store.RoomAdoptionSubmission{}, false, err
	}
	defer done()
	old, err := p.journal.LookupRoomAdoptionSubmission(call, request.RequestID)
	if err == nil {
		if !old.Request.Same(request) {
			return store.RoomAdoptionSubmission{}, false, store.ErrConflict
		}
		return old, false, nil
	}
	if !errors.Is(err, store.ErrNotFound) {
		return store.RoomAdoptionSubmission{}, false, err
	}
	if err = p.world(call, request.World()); err != nil {
		return store.RoomAdoptionSubmission{}, false, err
	}
	if err = p.current(call, epoch); err != nil {
		return store.RoomAdoptionSubmission{}, false, err
	}
	return p.journal.SubmitRoomAdoption(call, request)
}

// LookupRoomAdoptionSubmission returns one stored adoption by request ID.
func (p *Player) LookupRoomAdoptionSubmission(ctx context.Context, requestID string) (store.RoomAdoptionSubmission, error) {
	return p.journal.LookupRoomAdoptionSubmission(ctx, requestID)
}

// AdoptedShelter reports the world's current preferred shelter, the read side of
// SubmitRoomAdoption and the Go form of Python's
// plan.control['preferred_shelter'].
func (p *Player) AdoptedShelter(ctx context.Context, w store.World) (store.RoomAdoptionSubmission, error) {
	return p.journal.AdoptedShelter(ctx, w)
}
