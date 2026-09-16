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

// GoalCreateSubmissionRequest is explicit player intent to force-activate one
// already-known maintained goal kind right now, as a player-sourced goal,
// overriding whatever the autopilot's own routine review currently observes
// about that kind's deficit.
//
// It deliberately produces no plan and no action -- activating a goal issues no
// native call, exactly like PopulationDecisionSubmissionRequest -- so it keeps
// its own request table rather than a row in the shared submissions table,
// whose every row owns a plan_id and action_id. The native work still comes
// later and unchanged: whichever routine planner already knows how to compose a
// method for that kind commits it through CommitGoalMethod, which admits a
// player-sourced goal on exactly the same terms it always has (see
// admitRoutineDevelopment, which exempts non-autopilot goals from the routine
// development gate, and routineCommitments, which already counts PlayerGoal
// commitments against the autopilot's own concurrent-project capacity).
//
// Snapshot and Tick are supplied by the caller rather than read here, the same
// way RoutineReviewRequest supplies its own Current/Tick: this store holds no
// native census and must not invent one. The world half of Snapshot is checked
// against live native identity by the runtime gate before submission.
type GoalCreateSubmissionRequest struct {
	RequestID string
	Kind      domain.GoalKind
	Snapshot  domain.GenerationSnapshot
	Tick      domain.Tick
}

// GoalCreateSubmission is one stored request, the goal identity it activated,
// and that goal's state as this request left it.
type GoalCreateSubmission struct {
	Request GoalCreateSubmissionRequest
	// Goal is the goal identity this request activated. Replaying an old
	// request ID returns the identity that request actually activated, even
	// after a later cancel-and-recreate has moved the kind's current binding
	// on to a different identity.
	Goal domain.GoalID
	// State is that goal's state now, which is what the request produced for a
	// freshly accepted request and a later value when an older ID is replayed.
	State GoalState
}

// World is the world half of the requested snapshot, the key the per-kind
// player goal binding is stored under.
func (q GoalCreateSubmissionRequest) World() World {
	return World{Colony: q.Snapshot.Colony, Load: q.Snapshot.Load, Map: q.Snapshot.Map}
}

func (q GoalCreateSubmissionRequest) validate() error {
	if err := submissionID(q.RequestID); err != nil {
		return err
	}
	if err := q.Kind.Validate(); err != nil {
		return err
	}
	if err := q.Snapshot.Validate(); err != nil {
		return err
	}
	if err := q.World().Validate(); err != nil {
		return err
	}
	if q.Tick < 0 {
		return errors.New("invalid goal activation tick")
	}
	return nil
}

// SubmitGoalCreate atomically records one explicit player request and makes the
// named goal kind active and in deficit for that world, deciding replay by
// request ID exactly as every other submission does: the same ID with the same
// fields returns the stored request and reports no creation, and with different
// fields returns ErrConflict.
//
// Activation reuses the existing goal lifecycle rather than widening it. A kind
// the player has not activated in this world gets a fresh player-sourced goal
// through the same createGoal every autopilot goal uses, then one
// domain.ReviewGoal at NeedDeficit -- the player's explicit direction is the
// deficit assertion. A kind whose player goal is still live is re-reviewed at
// NeedDeficit instead, so ReviewGoal's own epoch rule does the reopening
// work: an already-recovered goal with no open work
// starts a new epoch, and open work is left alone for observation. A player
// goal that is cancelled or invalidated is never resurrected -- cancellation is
// terminal by design in ReviewGoal -- so the binding is replaced by a fresh
// identity, and the superseded goal keeps its own history. Goal capacity
// (maxActiveGoals) is the only bound on repeated cancel-and-recreate.
//
// Nothing here touches the autopilot's own routine bindings. A player goal is
// not in RoutineReview.Goals, so routine review never invalidates it, and
// retireRoutineGoals retires only invalidated autopilot goals.
func (s *Store) SubmitGoalCreate(ctx context.Context, q GoalCreateSubmissionRequest) (GoalCreateSubmission, bool, error) {
	if err := q.validate(); err != nil {
		return GoalCreateSubmission{}, false, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return GoalCreateSubmission{}, false, err
	}
	defer tx.Rollback()
	old, err := lookupGoalCreateSubmission(ctx, tx, q.RequestID)
	if err == nil {
		if old.Request != q {
			return GoalCreateSubmission{}, false, ErrConflict
		}
		if err = tx.Commit(); err != nil {
			return GoalCreateSubmission{}, false, err
		}
		return old, false, nil
	}
	if !errors.Is(err, ErrNotFound) {
		return GoalCreateSubmission{}, false, err
	}
	state, err := activatePlayerGoal(ctx, tx, q)
	if err != nil {
		return GoalCreateSubmission{}, false, err
	}
	w := q.World()
	payload, err := encodeGoalCreateRequest(q)
	if err != nil {
		return GoalCreateSubmission{}, false, err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO goal_create_submissions(request_id,colony,load_token,map_id,kind,goal_id,payload) VALUES(?,?,?,?,?,?,?)",
		q.RequestID, w.Colony, w.Load, w.Map, string(q.Kind), string(state.Goal.ID), payload); err != nil {
		return GoalCreateSubmission{}, false, conflict(err)
	}
	if err = bindPlayerGoal(ctx, tx, w, q.Kind, "create_goal", q.RequestID, state.Goal.ID); err != nil {
		return GoalCreateSubmission{}, false, conflict(err)
	}
	if err = tx.Commit(); err != nil {
		return GoalCreateSubmission{}, false, err
	}
	return GoalCreateSubmission{Request: q, Goal: state.Goal.ID, State: state}, true, nil
}

// activatePlayerGoal reuses or creates the world's player goal for one kind and
// leaves it active and in deficit. It never resurrects a cancelled or
// invalidated goal; see SubmitGoalCreate.
func activatePlayerGoal(ctx context.Context, tx *sql.Tx, q GoalCreateSubmissionRequest) (GoalState, error) {
	existing, err := currentPlayerGoal(ctx, tx, q.World(), q.Kind)
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
			g, err := domain.ReviewGoal(state.Goal, q.Snapshot, q.Tick, domain.NeedDeficit, false, open)
			if err != nil {
				return GoalState{}, err
			}
			g.Priority = q.Kind.Priority()
			if g.Status == domain.GoalInvalidated {
				// The player's own direction moved under the goal. Cancel its
				// captured work exactly as ReviewGoal's own invalidation does,
				// record the invalidation, and fall through to a fresh identity.
				if err = cancelGoalMethods(ctx, tx, state); err != nil {
					return GoalState{}, err
				}
				if _, err = saveGoal(ctx, tx, state, g); err != nil {
					return GoalState{}, err
				}
			} else {
				return saveGoal(ctx, tx, state, g)
			}
		}
	}
	var entropy [16]byte
	if _, err = rand.Read(entropy[:]); err != nil {
		return GoalState{}, err
	}
	id := domain.GoalID("player-" + hex.EncodeToString(entropy[:]) + "-" + string(q.Kind))
	goal, err := domain.NewGoal(id, domain.PlayerGoal, q.Kind.Priority(), q.Snapshot, q.Tick)
	if err != nil {
		return GoalState{}, err
	}
	if err = createGoal(ctx, tx, goal); err != nil {
		return GoalState{}, err
	}
	activated, err := domain.ReviewGoal(goal, q.Snapshot, q.Tick, domain.NeedDeficit, false, false)
	if err != nil {
		return GoalState{}, err
	}
	return saveGoal(ctx, tx, GoalState{Goal: goal}, activated)
}

// CancelPlayerGoal is the player-facing cancellation path: it resolves one
// already-observed goal identity within one world and cancels it through the
// unchanged store cancellation, which invalidates unissued work and marks
// issued work cancelled in the ordinary progress journal.
//
// It deliberately cancels autopilot-sourced goals too: any recorded goal ID
// resolves. The world check is
// what bounds it: a goal whose snapshot names a different colony, load or map
// is not this world's goal and returns ErrNotFound rather than being cancelled
// across worlds. Revision is the same local CAS token ReviewGoal uses; a stale
// one returns ErrConflict and changes nothing.
func (s *Store) CancelPlayerGoal(ctx context.Context, w World, id domain.GoalID, revision uint64) (GoalState, error) {
	if err := w.Validate(); err != nil {
		return GoalState{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return GoalState{}, err
	}
	defer tx.Rollback()
	state, err := loadGoal(ctx, tx, id)
	if err != nil {
		return GoalState{}, err
	}
	s2 := state.Goal.Snapshot
	if s2.Colony != w.Colony || s2.Load != w.Load || s2.Map != w.Map {
		return GoalState{}, ErrNotFound
	}
	out, err := cancelGoalState(ctx, tx, state, revision)
	if err != nil {
		return GoalState{}, err
	}
	if err = tx.Commit(); err != nil {
		return GoalState{}, err
	}
	return out, nil
}

// PlayerGoals returns every goal identity the player has activated for one
// world, keyed by kind. An empty result is not an error: a world where the
// player has activated nothing simply has none.
func (s *Store) PlayerGoals(ctx context.Context, w World) (map[domain.GoalKind]domain.GoalID, error) {
	if err := w.Validate(); err != nil {
		return nil, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return nil, err
	}
	defer tx.Rollback()
	rows, err := tx.QueryContext(ctx, "SELECT kind,goal_id FROM player_goals WHERE colony=? AND load_token=? AND map_id=? ORDER BY kind", w.Colony, w.Load, w.Map)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := map[domain.GoalKind]domain.GoalID{}
	for rows.Next() {
		var kind, id string
		if err = rows.Scan(&kind, &id); err != nil {
			return nil, err
		}
		known, err := domain.NewGoalKind(kind)
		if err != nil {
			return nil, err
		}
		out[known] = domain.GoalID(id)
	}
	if err = rows.Err(); err != nil {
		return nil, err
	}
	if err = rows.Close(); err != nil {
		return nil, err
	}
	if err = tx.Commit(); err != nil {
		return nil, err
	}
	return out, nil
}

// LookupGoalCreateSubmission returns one stored request by request ID.
func (s *Store) LookupGoalCreateSubmission(ctx context.Context, requestID string) (GoalCreateSubmission, error) {
	if err := submissionID(requestID); err != nil {
		return GoalCreateSubmission{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return GoalCreateSubmission{}, err
	}
	defer tx.Rollback()
	result, err := lookupGoalCreateSubmission(ctx, tx, requestID)
	if err != nil {
		return GoalCreateSubmission{}, err
	}
	if err = tx.Commit(); err != nil {
		return GoalCreateSubmission{}, err
	}
	return result, nil
}

// bindPlayerGoal records which goal identity a player command left bound to one
// kind in one world, replacing whatever was bound before. Every player command
// that owns a goal binds through here, so the binding stays single per kind; see
// initializeGoals for why the row names its own command.
func bindPlayerGoal(ctx context.Context, tx *sql.Tx, w World, kind domain.GoalKind, command, requestID string, id domain.GoalID) error {
	_, err := tx.ExecContext(ctx, "INSERT INTO player_goals(colony,load_token,map_id,kind,command,request_id,goal_id) VALUES(?,?,?,?,?,?,?) ON CONFLICT(colony,load_token,map_id,kind) DO UPDATE SET command=excluded.command,request_id=excluded.request_id,goal_id=excluded.goal_id",
		w.Colony, w.Load, w.Map, string(kind), command, requestID, string(id))
	return err
}

func currentPlayerGoal(ctx context.Context, tx *sql.Tx, w World, kind domain.GoalKind) (domain.GoalID, error) {
	var id string
	err := tx.QueryRowContext(ctx, "SELECT goal_id FROM player_goals WHERE colony=? AND load_token=? AND map_id=? AND kind=?", w.Colony, w.Load, w.Map, string(kind)).Scan(&id)
	if errors.Is(err, sql.ErrNoRows) {
		return "", ErrNotFound
	}
	if err != nil {
		return "", err
	}
	return domain.GoalID(id), nil
}

// goalCreateRequestWire is the stored JSON shape of one accepted request. The
// requested snapshot must survive a round trip exactly: the goal's own snapshot
// cannot stand in for it, because every later review advances the goal's
// snapshot and tick while a replayed request ID must still report the request
// the player actually made.
type goalCreateRequestWire struct {
	Kind     string                    `json:"kind"`
	Snapshot domain.GenerationSnapshot `json:"snapshot"`
	Tick     domain.Tick               `json:"tick"`
}

func encodeGoalCreateRequest(q GoalCreateSubmissionRequest) ([]byte, error) {
	return json.Marshal(goalCreateRequestWire{Kind: string(q.Kind), Snapshot: q.Snapshot, Tick: q.Tick})
}

func decodeGoalCreateRequest(id string, payload []byte) (GoalCreateSubmissionRequest, error) {
	if len(payload) > 4096 {
		return GoalCreateSubmissionRequest{}, errors.New("goal activation request exceeds bound")
	}
	var wire goalCreateRequestWire
	if err := json.Unmarshal(payload, &wire); err != nil {
		return GoalCreateSubmissionRequest{}, err
	}
	canonical, err := json.Marshal(wire)
	if err != nil {
		return GoalCreateSubmissionRequest{}, err
	}
	if !bytes.Equal(canonical, payload) {
		return GoalCreateSubmissionRequest{}, errors.New("noncanonical goal activation request")
	}
	kind, err := domain.NewGoalKind(wire.Kind)
	if err != nil {
		return GoalCreateSubmissionRequest{}, err
	}
	q := GoalCreateSubmissionRequest{RequestID: id, Kind: kind, Snapshot: wire.Snapshot, Tick: wire.Tick}
	return q, q.validate()
}

func lookupGoalCreateSubmission(ctx context.Context, tx *sql.Tx, id string) (GoalCreateSubmission, error) {
	var w World
	var kind, goalID string
	var payload []byte
	err := tx.QueryRowContext(ctx, "SELECT colony,load_token,map_id,kind,goal_id,payload FROM goal_create_submissions WHERE request_id=?", id).
		Scan(&w.Colony, &w.Load, &w.Map, &kind, &goalID, &payload)
	if errors.Is(err, sql.ErrNoRows) {
		return GoalCreateSubmission{}, ErrNotFound
	}
	if err != nil {
		return GoalCreateSubmission{}, err
	}
	request, err := decodeGoalCreateRequest(id, payload)
	if err != nil {
		return GoalCreateSubmission{}, err
	}
	if string(request.Kind) != kind || request.World() != w {
		return GoalCreateSubmission{}, errors.New("goal activation names two kinds or worlds")
	}
	state, err := loadGoal(ctx, tx, domain.GoalID(goalID))
	if err != nil {
		return GoalCreateSubmission{}, err
	}
	if state.Goal.Source != domain.PlayerGoal {
		return GoalCreateSubmission{}, errors.New("goal activation did not produce a player goal")
	}
	return GoalCreateSubmission{Request: request, Goal: state.Goal.ID, State: state}, nil
}
