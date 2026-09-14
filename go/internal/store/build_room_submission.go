package store

import (
	"bytes"
	"context"
	"crypto/rand"
	"database/sql"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// BuildRoomSubmissionRequest is explicit player intent to build one room
// shell -- a perimeter of walls with a single entrance door -- over an
// already-observed rectangle. It is the Go form of Python
// player_commands.BuildRoom.
//
// Like ZoneCreateSubmissionRequest it is player-command-driven, not a
// fixed-priority routine producer, so it commits its own plan immediately
// instead of being composed by a routine planner or bound to an autopilot goal
// through goals.go's admitZoneMethod-family gates. Unlike every other
// submission its plan holds many actions rather than one: the shell expands to
// one domain.Building per perimeter cell, and each of those is dispatched,
// received and verified by the same BuildingAction plumbing the autopilot
// shelter routine already uses.
//
// IntentID is the player-facing handle a later CancelConstruction or
// RelocateConstruction slice resolves back to this plan, standing in for
// Python's plan.control['player_intents'] mapping. It is unique per world, not
// globally: the same intent name in a different colony, load or map is a
// different construction.
type BuildRoomSubmissionRequest struct {
	RequestID string
	World     World
	IntentID  string
	Room      domain.RoomShell
}
type BuildRoomSubmission struct {
	Request BuildRoomSubmissionRequest
	Plan    domain.PlanID
	// Action is the entrance placement: the first action of the committed
	// plan and the one recorded in the shared submissions header, which holds
	// exactly one action identity per request. Every other placement's
	// identity is derived from it, so the record stays comparable and replay
	// equality remains typed semantics rather than a slice comparison.
	Action   domain.ActionID
	Revision domain.PlanRevision
}

// ActionIDs is every committed placement identity in expansion order, the
// entrance first. It is derived, never stored: the expansion is a total
// function of the shell and the header's action identity.
func (v BuildRoomSubmission) ActionIDs() []domain.ActionID {
	_, ids, err := buildRoomActions(v.Request.Room, string(v.Action))
	if err != nil {
		return nil
	}
	return ids
}

type roomShellPayload struct {
	X, Z, Width, Height int32
	WallDef             string
	DoorDef             string
	Material            string
	Entrance            domain.Rotation
	Purpose             domain.RoomPurpose
}

func roomShellPayloadOf(r domain.RoomShell) roomShellPayload {
	b := r.Bounds()
	return roomShellPayload{b.X, b.Z, b.Width, b.Height, r.WallDefinition(), r.DoorDefinition(), r.Material(), r.Entrance(), r.Purpose()}
}
func reconstructRoomShell(p roomShellPayload) (domain.RoomShell, error) {
	return domain.NewRoomShell(domain.RoomBounds{X: p.X, Z: p.Z, Width: p.Width, Height: p.Height}, p.WallDef, p.DoorDef, p.Material, p.Entrance, p.Purpose)
}

func (b BuildRoomSubmissionRequest) validate() error {
	if err := submissionID(b.RequestID); err != nil {
		return err
	}
	if err := b.World.Validate(); err != nil {
		return err
	}
	if err := domain.ValidateRoomIntent(b.IntentID); err != nil {
		return err
	}
	canonical, err := reconstructRoomShell(roomShellPayloadOf(b.Room))
	if err != nil || canonical != b.Room {
		return errors.New("invalid room shell configuration")
	}
	if len(b.Room.Placements()) == 0 {
		return errors.New("room shell expands to no placements")
	}
	return nil
}

// buildRoomActions expands one shell into the committed building actions.
// Identities are derived from the plan's own entropy so a replayed lookup
// reconstructs exactly the same list without storing it.
func buildRoomActions(room domain.RoomShell, prefix string) ([]domain.Action, []domain.ActionID, error) {
	placements := room.Placements()
	if len(placements) == 0 {
		return nil, nil, errors.New("room shell expands to no placements")
	}
	actions := make([]domain.Action, 0, len(placements))
	ids := make([]domain.ActionID, 0, len(placements))
	for i, building := range placements {
		id := domain.ActionID(prefix)
		if i > 0 {
			id = domain.ActionID(fmt.Sprintf("%s-%d", prefix, i))
		}
		action, err := domain.NewBuildingAction(id, building)
		if err != nil {
			return nil, nil, err
		}
		actions = append(actions, action)
		ids = append(ids, id)
	}
	return actions, ids, nil
}

// SubmitBuildRoom atomically stores one explicit player room-shell intent and
// its whole expanded plan, the same shape SubmitZoneCreate uses. Submission
// neither acquires authority nor issues any native placement; a worker later
// admits and dispatches each committed building action.
func (s *Store) SubmitBuildRoom(ctx context.Context, b BuildRoomSubmissionRequest) (BuildRoomSubmission, bool, error) {
	if err := b.validate(); err != nil {
		return BuildRoomSubmission{}, false, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return BuildRoomSubmission{}, false, err
	}
	defer tx.Rollback()
	old, err := lookupBuildRoomSubmission(ctx, tx, b.RequestID)
	if err == nil {
		if old.Request != b {
			return BuildRoomSubmission{}, false, ErrConflict
		}
		if err = tx.Commit(); err != nil {
			return BuildRoomSubmission{}, false, err
		}
		return old, false, nil
	}
	if !errors.Is(err, ErrNotFound) {
		return BuildRoomSubmission{}, false, err
	}
	var entropy [32]byte
	if _, err = rand.Read(entropy[:]); err != nil {
		return BuildRoomSubmission{}, false, err
	}
	result := BuildRoomSubmission{Request: b, Plan: domain.PlanID("build-room-" + hex.EncodeToString(entropy[:16])), Action: domain.ActionID("build-room-action-" + hex.EncodeToString(entropy[16:])), Revision: 1}
	actions, _, err := buildRoomActions(b.Room, string(result.Action))
	if err != nil {
		return BuildRoomSubmission{}, false, err
	}
	plan, err := domain.NewPlan(result.Plan, 1, actions)
	if err != nil {
		return BuildRoomSubmission{}, false, err
	}
	if err = createPlan(ctx, tx, plan); err != nil {
		return BuildRoomSubmission{}, false, err
	}
	if err = insertSubmissionHeader(ctx, tx, b.RequestID, "build_room", b.World, result.Plan, result.Action); err != nil {
		return BuildRoomSubmission{}, false, err
	}
	data, err := json.Marshal(roomShellPayloadOf(b.Room))
	if err != nil {
		return BuildRoomSubmission{}, false, err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO build_room_submissions(request_id,colony,load_token,map_id,intent_id,payload) VALUES(?,?,?,?,?,?)",
		b.RequestID, b.World.Colony, b.World.Load, b.World.Map, b.IntentID, data); err != nil {
		return BuildRoomSubmission{}, false, conflict(err)
	}
	if err = tx.Commit(); err != nil {
		return BuildRoomSubmission{}, false, err
	}
	return result, true, nil
}

func (s *Store) LookupBuildRoomSubmission(ctx context.Context, requestID string) (BuildRoomSubmission, error) {
	if err := submissionID(requestID); err != nil {
		return BuildRoomSubmission{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return BuildRoomSubmission{}, err
	}
	defer tx.Rollback()
	result, err := lookupBuildRoomSubmission(ctx, tx, requestID)
	if err != nil {
		return BuildRoomSubmission{}, err
	}
	if err = tx.Commit(); err != nil {
		return BuildRoomSubmission{}, err
	}
	return result, nil
}

// LookupBuildRoomIntent resolves one world-scoped player construction intent
// back to its committed plan and placements. It is the durable half of the
// follow-up hook a CancelConstruction or RelocateConstruction slice needs,
// standing in for Python's plan.control['player_intents'][intent] lookup; this
// slice deliberately provides the resolution only, never the cancellation.
func (s *Store) LookupBuildRoomIntent(ctx context.Context, world World, intentID string) (BuildRoomSubmission, error) {
	if err := world.Validate(); err != nil {
		return BuildRoomSubmission{}, err
	}
	if err := domain.ValidateRoomIntent(intentID); err != nil {
		return BuildRoomSubmission{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return BuildRoomSubmission{}, err
	}
	defer tx.Rollback()
	result, err := lookupBuildRoomIntent(ctx, tx, world, intentID)
	if err != nil {
		return BuildRoomSubmission{}, err
	}
	if err = tx.Commit(); err != nil {
		return BuildRoomSubmission{}, err
	}
	return result, nil
}

// lookupBuildRoomIntent is the in-transaction half of LookupBuildRoomIntent, so
// a follow-up command such as cancel_construction can resolve the intent and
// read the plan it names in the same atomic view.
func lookupBuildRoomIntent(ctx context.Context, tx *sql.Tx, world World, intentID string) (BuildRoomSubmission, error) {
	if err := world.Validate(); err != nil {
		return BuildRoomSubmission{}, err
	}
	if err := domain.ValidateRoomIntent(intentID); err != nil {
		return BuildRoomSubmission{}, err
	}
	var requestID string
	err := tx.QueryRowContext(ctx, "SELECT request_id FROM build_room_submissions WHERE colony=? AND load_token=? AND map_id=? AND intent_id=?",
		world.Colony, world.Load, world.Map, intentID).Scan(&requestID)
	if errors.Is(err, sql.ErrNoRows) {
		return BuildRoomSubmission{}, ErrNotFound
	}
	if err != nil {
		return BuildRoomSubmission{}, err
	}
	return lookupBuildRoomSubmission(ctx, tx, requestID)
}

func lookupBuildRoomSubmission(ctx context.Context, tx *sql.Tx, id string) (BuildRoomSubmission, error) {
	h, err := lookupSubmissionHeader(ctx, tx, id, "build_room")
	if err != nil {
		return BuildRoomSubmission{}, err
	}
	var data []byte
	var intent string
	var world World
	err = tx.QueryRowContext(ctx, "SELECT colony,load_token,map_id,intent_id,payload FROM build_room_submissions WHERE request_id=?", id).
		Scan(&world.Colony, &world.Load, &world.Map, &intent, &data)
	if errors.Is(err, sql.ErrNoRows) {
		return BuildRoomSubmission{}, ErrNotFound
	}
	if err != nil {
		return BuildRoomSubmission{}, err
	}
	if world != h.World {
		return BuildRoomSubmission{}, errors.New("build room submission world differs from its header")
	}
	if len(data) > 32768 {
		return BuildRoomSubmission{}, errors.New("build room submission exceeds bound")
	}
	var payload roomShellPayload
	if err = json.Unmarshal(data, &payload); err != nil {
		return BuildRoomSubmission{}, err
	}
	canonical, err := json.Marshal(payload)
	if err != nil {
		return BuildRoomSubmission{}, err
	}
	if !bytes.Equal(canonical, data) {
		return BuildRoomSubmission{}, errors.New("noncanonical build room submission")
	}
	room, err := reconstructRoomShell(payload)
	if err != nil {
		return BuildRoomSubmission{}, err
	}
	result := BuildRoomSubmission{Request: BuildRoomSubmissionRequest{RequestID: id, World: h.World, IntentID: intent, Room: room}, Revision: h.Revision, Plan: h.Plan, Action: h.Action}
	if err = result.Request.validate(); err != nil {
		return BuildRoomSubmission{}, err
	}
	expected, _, err := buildRoomActions(room, string(h.Action))
	if err != nil {
		return BuildRoomSubmission{}, err
	}
	state, err := load(ctx, tx, result.Plan)
	if err != nil {
		return BuildRoomSubmission{}, err
	}
	actions := state.Spec.Actions()
	if state.Spec.Revision() != 1 || len(actions) != len(expected) {
		return BuildRoomSubmission{}, errors.New("build room submission plan is corrupt")
	}
	for i, action := range actions {
		if action.ID() != expected[i].ID() {
			return BuildRoomSubmission{}, errors.New("build room submission plan is corrupt")
		}
		actual, ok := action.Building()
		wanted, _ := expected[i].Building()
		if !ok || actual != wanted {
			return BuildRoomSubmission{}, errors.New("build room submission differs from intent")
		}
	}
	return result, nil
}
