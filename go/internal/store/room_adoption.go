package store

import (
	"bytes"
	"context"
	"crypto/rand"
	"database/sql"
	"encoding/hex"
	"encoding/json"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// RoomAdoptionSubmissionRequest is explicit player intent to claim one
// already-built native room as satisfying EnsureInitialShelter, the Go form of
// Python player_commands.AdoptRoom and its room_adoption.adopt handler.
//
// It issues no native call and composes no plan -- Python's own reply is
// "Existing construction preserved; no new construction issued" -- so like
// GoalCreateSubmissionRequest it keeps its own request table rather than a row
// in the shared submissions table, whose every row owns a plan_id and action_id.
//
// IntentID is the same world-scoped player construction handle BuildRoom uses,
// standing in for Python's 'intent-'+intent_id goal identity. Python refuses an
// intent ID that already carries non-AdoptRoom construction history; this does
// the same by refusing an ID that already names a build_room_submissions row,
// which is where this controller's construction history for an intent lives.
//
// Snapshot and Tick are the caller's, exactly as GoalCreateSubmissionRequest
// documents: this store holds no native census and must not invent one.
type RoomAdoptionSubmissionRequest struct {
	RequestID string
	IntentID  string
	Adoption  domain.RoomAdoption
	Evidence  domain.AdoptionEvidence
	Snapshot  domain.GenerationSnapshot
	Tick      domain.Tick
}

// RoomAdoptionSubmission is one stored request, the goal identity it completed,
// and that goal's state as this request left it.
type RoomAdoptionSubmission struct {
	Request RoomAdoptionSubmissionRequest
	Goal    domain.GoalID
	State   GoalState
}

// World is the world half of the requested snapshot, the key the per-intent
// adoption binding and the world's preferred shelter are stored under.
func (q RoomAdoptionSubmissionRequest) World() World {
	return World{Colony: q.Snapshot.Colony, Load: q.Snapshot.Load, Map: q.Snapshot.Map}
}

// Same is value equality for a request whose adoption holds a slice, so replay
// can compare requests the way every comparable submission compares them with
// ==.
func (q RoomAdoptionSubmissionRequest) Same(other RoomAdoptionSubmissionRequest) bool {
	return q.RequestID == other.RequestID && q.IntentID == other.IntentID && q.Evidence == other.Evidence &&
		q.Snapshot == other.Snapshot && q.Tick == other.Tick && domain.SameRoomAdoption(q.Adoption, other.Adoption)
}

func (q RoomAdoptionSubmissionRequest) validate() error {
	if err := submissionID(q.RequestID); err != nil {
		return err
	}
	if err := domain.ValidateRoomIntent(q.IntentID); err != nil {
		return err
	}
	canonical, err := domain.ReconstructRoomAdoption(q.Adoption)
	if err != nil || !domain.SameRoomAdoption(canonical, q.Adoption) {
		return errors.New("invalid room adoption geometry")
	}
	if err = q.Evidence.Validate(); err != nil {
		return err
	}
	// The inspected native room must be the one the player described. Python
	// derives the cell count from the verified room itself; here the count is
	// reported by the player alongside the geometry, so the two must agree or
	// the evidence is not evidence for this request.
	if int(q.Evidence.Cells) != len(q.Adoption.Interior()) {
		return errors.New("adopted native room does not match the inspected interior")
	}
	if err = q.Snapshot.Validate(); err != nil {
		return err
	}
	if err = q.World().Validate(); err != nil {
		return err
	}
	if q.Tick < 0 {
		return errors.New("invalid room adoption tick")
	}
	return nil
}

// SubmitRoomAdoption atomically records one explicit player adoption and leaves
// the world's EnsureInitialShelter goal complete, with the player's inspection
// of the adopted native room stored beside it. Replay is decided by request ID
// exactly as every other submission decides it.
//
// Completion reuses the goal lifecycle rather than widening it. domain.Goal's
// own invariant is that a satisfied goal must carry observed recovery
// (Goal.Validate refuses GoalSatisfied unless Need is NeedRecovered and
// RecoveryObserved is set), and domain.ReviewGoal reaches exactly that state
// from a fresh goal in one call: reviewing at NeedRecovered with no open work
// sets GoalSatisfied and RecoveryObserved together. So a player adoption is a
// fresh player-sourced goal through the same createGoal every autopilot goal
// uses, then one ReviewGoal at NeedRecovered -- the player's explicit inspection
// is the recovery observation, the mirror of SubmitGoalCreate treating their
// explicit direction as the deficit assertion. No invariant is weakened and no
// new goal status is invented.
//
// A live player goal already bound to EnsureInitialShelter in this world is
// re-reviewed at NeedRecovered instead of being replaced, so adopting a second
// room moves the same goal's evidence rather than accumulating goals. Open work
// on that goal is honoured: ReviewGoal leaves a goal with unresolved captured
// work active rather than satisfied, and the adoption is refused rather than
// silently recorded as complete, because a room whose construction is still
// running is not a room the player can have inspected as finished.
//
// A cancelled or invalidated goal is never resurrected -- cancellation is
// terminal by design in ReviewGoal -- so the binding is replaced by a fresh
// identity and the superseded goal keeps its own history, the same rule
// activatePlayerGoal follows.
func (s *Store) SubmitRoomAdoption(ctx context.Context, q RoomAdoptionSubmissionRequest) (RoomAdoptionSubmission, bool, error) {
	if err := q.validate(); err != nil {
		return RoomAdoptionSubmission{}, false, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return RoomAdoptionSubmission{}, false, err
	}
	defer tx.Rollback()
	old, err := lookupRoomAdoptionSubmission(ctx, tx, q.RequestID)
	if err == nil {
		if !old.Request.Same(q) {
			return RoomAdoptionSubmission{}, false, ErrConflict
		}
		if err = tx.Commit(); err != nil {
			return RoomAdoptionSubmission{}, false, err
		}
		return old, false, nil
	}
	if !errors.Is(err, ErrNotFound) {
		return RoomAdoptionSubmission{}, false, err
	}
	w := q.World()
	// Python: "Use a new room-adoption intent ID; existing construction history
	// must remain intact". An intent this controller already placed construction
	// under is that history, and adoption must not overwrite it.
	if _, err = lookupBuildRoomIntent(ctx, tx, w, q.IntentID); err == nil {
		return RoomAdoptionSubmission{}, false, errors.New("use a new room-adoption intent identity; existing construction history must remain intact")
	} else if !errors.Is(err, ErrNotFound) {
		return RoomAdoptionSubmission{}, false, err
	}
	state, err := completeShelterGoal(ctx, tx, q)
	if err != nil {
		return RoomAdoptionSubmission{}, false, err
	}
	payload, err := encodeRoomAdoptionRequest(q)
	if err != nil {
		return RoomAdoptionSubmission{}, false, err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO room_adoption_submissions(request_id,colony,load_token,map_id,intent_id,goal_id,payload) VALUES(?,?,?,?,?,?,?)",
		q.RequestID, w.Colony, w.Load, w.Map, q.IntentID, string(state.Goal.ID), payload); err != nil {
		return RoomAdoptionSubmission{}, false, conflict(err)
	}
	if err = bindPlayerGoal(ctx, tx, w, domain.EnsureInitialShelterGoal, "adopt_room", q.RequestID, state.Goal.ID); err != nil {
		return RoomAdoptionSubmission{}, false, conflict(err)
	}
	// preferred_shelter in Python's plan.control, keyed per world: the adopted
	// intent the shelter handoff prefers over any other completed shelter.
	// Clearing suppressed_goals['EnsureInitialShelter'] has no Go counterpart --
	// this controller has no goal-suppression map; completing the goal is the
	// whole of what Python's two control writes achieve here.
	if _, err = tx.ExecContext(ctx, "INSERT INTO adopted_shelters(colony,load_token,map_id,request_id,intent_id,goal_id) VALUES(?,?,?,?,?,?) ON CONFLICT(colony,load_token,map_id) DO UPDATE SET request_id=excluded.request_id,intent_id=excluded.intent_id,goal_id=excluded.goal_id",
		w.Colony, w.Load, w.Map, q.RequestID, q.IntentID, string(state.Goal.ID)); err != nil {
		return RoomAdoptionSubmission{}, false, conflict(err)
	}
	if err = tx.Commit(); err != nil {
		return RoomAdoptionSubmission{}, false, err
	}
	return RoomAdoptionSubmission{Request: q, Goal: state.Goal.ID, State: state}, true, nil
}

// completeShelterGoal reuses or creates the world's player EnsureInitialShelter
// goal and leaves it satisfied with observed recovery. See SubmitRoomAdoption
// for why NeedRecovered is the right review and why open work is refused.
func completeShelterGoal(ctx context.Context, tx *sql.Tx, q RoomAdoptionSubmissionRequest) (GoalState, error) {
	kind := domain.EnsureInitialShelterGoal
	existing, err := currentPlayerGoal(ctx, tx, q.World(), kind)
	if err != nil && !errors.Is(err, ErrNotFound) {
		return GoalState{}, err
	}
	if err == nil {
		state, err := loadGoal(ctx, tx, existing)
		if err != nil {
			return GoalState{}, err
		}
		if !state.Retired && state.Goal.Status != domain.GoalCancelled && state.Goal.Status != domain.GoalInvalidated {
			open, err := goalOpenWork(ctx, tx, state)
			if err != nil {
				return GoalState{}, err
			}
			if open {
				return GoalState{}, errors.New("shelter construction is still running; cancel it before adopting a finished room")
			}
			g, err := domain.ReviewGoal(state.Goal, q.Snapshot, q.Tick, domain.NeedRecovered, false, false)
			if err != nil {
				return GoalState{}, err
			}
			g.Priority = kind.Priority()
			if g.Status != domain.GoalInvalidated {
				return saveGoal(ctx, tx, state, g)
			}
			// The player's own direction moved under the goal. Record the
			// invalidation and fall through to a fresh identity, exactly as
			// activatePlayerGoal does.
			if err = cancelGoalMethods(ctx, tx, state); err != nil {
				return GoalState{}, err
			}
			if _, err = saveGoal(ctx, tx, state, g); err != nil {
				return GoalState{}, err
			}
		}
	}
	var entropy [16]byte
	if _, err = rand.Read(entropy[:]); err != nil {
		return GoalState{}, err
	}
	id := domain.GoalID("player-" + hex.EncodeToString(entropy[:]) + "-" + string(kind))
	goal, err := domain.NewGoal(id, domain.PlayerGoal, kind.Priority(), q.Snapshot, q.Tick)
	if err != nil {
		return GoalState{}, err
	}
	if err = createGoal(ctx, tx, goal); err != nil {
		return GoalState{}, err
	}
	satisfied, err := domain.ReviewGoal(goal, q.Snapshot, q.Tick, domain.NeedRecovered, false, false)
	if err != nil {
		return GoalState{}, err
	}
	if satisfied.Status != domain.GoalSatisfied || !satisfied.RecoveryObserved {
		return GoalState{}, errors.New("room adoption did not complete its shelter goal")
	}
	return saveGoal(ctx, tx, GoalState{Goal: goal}, satisfied)
}

// AdoptedShelter is the world's current preferred shelter: the adoption request
// whose room the shelter handoff should furnish, Python's
// plan.control['preferred_shelter'].
func (s *Store) AdoptedShelter(ctx context.Context, w World) (RoomAdoptionSubmission, error) {
	if err := w.Validate(); err != nil {
		return RoomAdoptionSubmission{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return RoomAdoptionSubmission{}, err
	}
	defer tx.Rollback()
	var requestID string
	err = tx.QueryRowContext(ctx, "SELECT request_id FROM adopted_shelters WHERE colony=? AND load_token=? AND map_id=?", w.Colony, w.Load, w.Map).Scan(&requestID)
	if errors.Is(err, sql.ErrNoRows) {
		return RoomAdoptionSubmission{}, ErrNotFound
	}
	if err != nil {
		return RoomAdoptionSubmission{}, err
	}
	result, err := lookupRoomAdoptionSubmission(ctx, tx, requestID)
	if err != nil {
		return RoomAdoptionSubmission{}, err
	}
	if err = tx.Commit(); err != nil {
		return RoomAdoptionSubmission{}, err
	}
	return result, nil
}

// LookupRoomAdoptionSubmission returns one stored adoption by request ID.
func (s *Store) LookupRoomAdoptionSubmission(ctx context.Context, requestID string) (RoomAdoptionSubmission, error) {
	if err := submissionID(requestID); err != nil {
		return RoomAdoptionSubmission{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return RoomAdoptionSubmission{}, err
	}
	defer tx.Rollback()
	result, err := lookupRoomAdoptionSubmission(ctx, tx, requestID)
	if err != nil {
		return RoomAdoptionSubmission{}, err
	}
	if err = tx.Commit(); err != nil {
		return RoomAdoptionSubmission{}, err
	}
	return result, nil
}

// roomAdoptionWire is the stored JSON shape of one accepted adoption. The
// requested snapshot must survive a round trip exactly, for the same reason
// goalCreateRequestWire keeps its own: every later review advances the goal's
// snapshot while a replayed request ID must still report the request the player
// actually made.
type roomAdoptionWire struct {
	IntentID  string                    `json:"intentId"`
	X         int32                     `json:"x"`
	Z         int32                     `json:"z"`
	Width     int32                     `json:"width"`
	Height    int32                     `json:"height"`
	Entrance  domain.Rotation           `json:"entrance"`
	Interior  []domain.Cell             `json:"interior,omitempty"`
	Door      *domain.Cell              `json:"door,omitempty"`
	RoomID    string                    `json:"roomId"`
	Cells     int32                     `json:"cells"`
	Role      string                    `json:"role"`
	RoleLabel string                    `json:"roleLabel"`
	Snapshot  domain.GenerationSnapshot `json:"snapshot"`
	Tick      domain.Tick               `json:"tick"`
}

func roomAdoptionWireOf(q RoomAdoptionSubmissionRequest) roomAdoptionWire {
	b := q.Adoption.Bounds()
	wire := roomAdoptionWire{IntentID: q.IntentID, X: b.X, Z: b.Z, Width: b.Width, Height: b.Height,
		Entrance: q.Adoption.Entrance(), Interior: q.Adoption.InteriorCells(),
		RoomID: q.Evidence.RoomID, Cells: q.Evidence.Cells, Role: q.Evidence.Role, RoleLabel: q.Evidence.RoleLabel,
		Snapshot: q.Snapshot, Tick: q.Tick}
	if q.Adoption.EntranceCellSet() {
		door := q.Adoption.EntranceCell()
		wire.Door = &door
	}
	return wire
}

func encodeRoomAdoptionRequest(q RoomAdoptionSubmissionRequest) ([]byte, error) {
	return json.Marshal(roomAdoptionWireOf(q))
}

func decodeRoomAdoptionRequest(id string, payload []byte) (RoomAdoptionSubmissionRequest, error) {
	if len(payload) > 131072 {
		return RoomAdoptionSubmissionRequest{}, errors.New("room adoption request exceeds bound")
	}
	var wire roomAdoptionWire
	if err := json.Unmarshal(payload, &wire); err != nil {
		return RoomAdoptionSubmissionRequest{}, err
	}
	canonical, err := json.Marshal(wire)
	if err != nil {
		return RoomAdoptionSubmissionRequest{}, err
	}
	if !bytes.Equal(canonical, payload) {
		return RoomAdoptionSubmissionRequest{}, errors.New("noncanonical room adoption request")
	}
	var door domain.Cell
	if wire.Door != nil {
		door = *wire.Door
	}
	adoption, err := domain.NewRoomAdoption(domain.RoomBounds{X: wire.X, Z: wire.Z, Width: wire.Width, Height: wire.Height},
		wire.Entrance, wire.Interior, door, wire.Door != nil)
	if err != nil {
		return RoomAdoptionSubmissionRequest{}, err
	}
	q := RoomAdoptionSubmissionRequest{RequestID: id, IntentID: wire.IntentID, Adoption: adoption,
		Evidence: domain.AdoptionEvidence{RoomID: wire.RoomID, Cells: wire.Cells, Role: wire.Role, RoleLabel: wire.RoleLabel},
		Snapshot: wire.Snapshot, Tick: wire.Tick}
	return q, q.validate()
}

func lookupRoomAdoptionSubmission(ctx context.Context, tx *sql.Tx, id string) (RoomAdoptionSubmission, error) {
	var w World
	var intent, goalID string
	var payload []byte
	err := tx.QueryRowContext(ctx, "SELECT colony,load_token,map_id,intent_id,goal_id,payload FROM room_adoption_submissions WHERE request_id=?", id).
		Scan(&w.Colony, &w.Load, &w.Map, &intent, &goalID, &payload)
	if errors.Is(err, sql.ErrNoRows) {
		return RoomAdoptionSubmission{}, ErrNotFound
	}
	if err != nil {
		return RoomAdoptionSubmission{}, err
	}
	request, err := decodeRoomAdoptionRequest(id, payload)
	if err != nil {
		return RoomAdoptionSubmission{}, err
	}
	if request.IntentID != intent || request.World() != w {
		return RoomAdoptionSubmission{}, errors.New("room adoption names two intents or worlds")
	}
	state, err := loadGoal(ctx, tx, domain.GoalID(goalID))
	if err != nil {
		return RoomAdoptionSubmission{}, err
	}
	if state.Goal.Source != domain.PlayerGoal {
		return RoomAdoptionSubmission{}, errors.New("room adoption did not produce a player goal")
	}
	return RoomAdoptionSubmission{Request: request, Goal: state.Goal.ID, State: state}, nil
}
