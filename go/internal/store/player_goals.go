package store

import (
	"context"
	"crypto/rand"
	"database/sql"
	"encoding/hex"
	"errors"
	"strings"
	"sync"

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

// World is the world half of the requested snapshot; PlayerGoals matches its
// colony and map against each player goal's own snapshot.
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
	s.submissions.mu.Lock()
	defer s.submissions.mu.Unlock()
	old, err := s.lookupGoalCreateSubmission(ctx, tx, q.RequestID)
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
	if err = tx.Commit(); err != nil {
		return GoalCreateSubmission{}, false, err
	}
	result := GoalCreateSubmission{Request: q, Goal: state.Goal.ID, State: state}
	if s.submissions.entries == nil {
		s.submissions.entries = map[string]GoalCreateSubmission{}
	}
	s.submissions.entries[q.RequestID] = result
	return result, true, nil
}

// activatePlayerGoal reuses or creates the world's player goal for one kind and
// leaves it active and in deficit. It never resurrects a cancelled or
// invalidated goal; see SubmitGoalCreate.
func activatePlayerGoal(ctx context.Context, tx *sql.Tx, q GoalCreateSubmissionRequest) (GoalState, error) {
	current, err := playerGoals(ctx, tx, q.World())
	if err != nil {
		return GoalState{}, err
	}
	if state, ok := current[q.Kind]; ok {
		if !state.Retired && state.Goal.Status != domain.GoalCancelled && state.Goal.Status != domain.GoalInvalidated {
			open, err := goalOpenWork(ctx, tx, state)
			if err != nil {
				return GoalState{}, err
			}
			g, err := domain.ReviewGoal(state.Goal, q.Snapshot, q.Tick, domain.NeedDeficit, open, goalIsStandard(state.Goal.ID))
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
	activated, err := domain.ReviewGoal(goal, q.Snapshot, q.Tick, domain.NeedDeficit, false, goalIsStandard(goal.ID))
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

// PlayerGoals returns the goal identity the player has activated for each kind
// in one world. It is derived from the rebuilt goals themselves (#1006): every
// goal whose Source is PlayerGoal and whose snapshot names this colony and map
// (U4b: the load token is not compared, so a player goal survives a world
// change). The kind is read from the identity activatePlayerGoal mints. An
// empty result is not an error.
func (s *Store) PlayerGoals(ctx context.Context, w World) (map[domain.GoalKind]domain.GoalID, error) {
	if err := w.Validate(); err != nil {
		return nil, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return nil, err
	}
	defer tx.Rollback()
	found, err := playerGoals(ctx, tx, w)
	if err != nil {
		return nil, err
	}
	out := make(map[domain.GoalKind]domain.GoalID, len(found))
	for kind, state := range found {
		out[kind] = state.Goal.ID
	}
	return out, tx.Commit()
}

// playerGoalKind reads the kind from a player goal identity,
// "player-<hex>-<kind>". A goal minted any other way has no kind.
func playerGoalKind(id domain.GoalID) (domain.GoalKind, bool) {
	rest, ok := strings.CutPrefix(string(id), "player-")
	if !ok {
		return "", false
	}
	_, kind, ok := strings.Cut(rest, "-")
	if !ok {
		return "", false
	}
	known, err := domain.NewGoalKind(kind)
	return known, err == nil
}

// playerGoals picks, per kind, the world's current player goal: a live one
// over a cancelled, invalidated or retired one, then the latest tick, then
// the greater identity.
func playerGoals(ctx context.Context, tx *sql.Tx, w World) (map[domain.GoalKind]GoalState, error) {
	ids, err := goalIDs(ctx, tx)
	if err != nil {
		return nil, err
	}
	live := func(s GoalState) bool {
		return !s.Retired && s.Goal.Status != domain.GoalCancelled && s.Goal.Status != domain.GoalInvalidated
	}
	out := map[domain.GoalKind]GoalState{}
	for _, id := range ids {
		kind, ok := playerGoalKind(id)
		if !ok {
			continue
		}
		state, err := loadGoal(ctx, tx, id)
		if err != nil {
			return nil, err
		}
		g := state.Goal
		if g.Source != domain.PlayerGoal || g.Snapshot.Colony != w.Colony || g.Snapshot.Map != w.Map {
			continue
		}
		prev, seen := out[kind]
		if seen {
			if live(prev) != live(state) {
				if live(prev) {
					continue
				}
			} else if prev.Goal.Tick > g.Tick {
				continue
			}
		}
		out[kind] = state
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
	s.submissions.mu.Lock()
	defer s.submissions.mu.Unlock()
	result, err := s.lookupGoalCreateSubmission(ctx, tx, requestID)
	if err != nil {
		return GoalCreateSubmission{}, err
	}
	if err = tx.Commit(); err != nil {
		return GoalCreateSubmission{}, err
	}
	return result, nil
}

// goalCreateSubmissions holds accepted requests by request ID in memory
// (#1011): request replay is session-only (U2a), so a restart forgets them.
// mu also serializes SubmitGoalCreate so lookup-then-activate cannot race.
type goalCreateSubmissions struct {
	mu      sync.Mutex
	entries map[string]GoalCreateSubmission
}

// lookupGoalCreateSubmission rereads an accepted request's goal. The caller
// holds s.submissions.mu.
func (s *Store) lookupGoalCreateSubmission(ctx context.Context, tx *sql.Tx, id string) (GoalCreateSubmission, error) {
	entry, ok := s.submissions.entries[id]
	if !ok {
		return GoalCreateSubmission{}, ErrNotFound
	}
	state, err := loadGoal(ctx, tx, entry.Goal)
	if err != nil {
		return GoalCreateSubmission{}, err
	}
	if state.Goal.Source != domain.PlayerGoal {
		return GoalCreateSubmission{}, errors.New("goal activation did not produce a player goal")
	}
	return GoalCreateSubmission{Request: entry.Request, Goal: state.Goal.ID, State: state}, nil
}
