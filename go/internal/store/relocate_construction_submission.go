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

// RelocateConstructionSubmissionRequest is explicit player intent to move one
// already-named construction somewhere else. It is the Go form of Python
// player_commands.RelocateConstruction.
//
// Python's replacement field is a RoomShell | Buildings discriminated union and
// its handler refuses a request whose replacement is not the same kind as the
// original ("Relocation must preserve the construction kind"). Go carries only
// the RoomShell arm: there is no player-facing PlaceBuildings command in this
// family, so every relocatable construction is a room shell and the check
// degenerates to a tautology this type already enforces at its field type. The
// refusal is therefore deliberately absent rather than ported; if a
// buildings-shaped player command ever lands, it comes back with it.
type RelocateConstructionSubmissionRequest struct {
	RequestID   string
	World       World
	IntentID    string
	Replacement domain.RoomShell
}

// RelocateConstructionSubmission records what one relocation committed.
//
// Source is the construction the intent resolved to at submission -- the
// original build_room plan, or the plan of an earlier relocation of the same
// intent -- and is the plan this submission supersedes. Targets are the
// placements in it that had a live native order to withdraw, in the source's own
// expansion order.
//
// Plan is the single committed plan holding *both* halves of the relocation:
// one domain.ConstructionCancelAction per target followed by one
// domain.BuildingAction per placement of the replacement shell. CancelAction and
// BuildAction are the two identity prefixes within it, derived exactly as
// BuildRoomSubmission.Action and CancelConstructionSubmission.Action are.
//
// FastPath reports the whole-intent form of Python's "never issued at all" early
// exit: nothing of the source construction had ever been dispatched, so there is
// no native order to withdraw and the relocation is a pure supersession. Targets
// is then empty and CancelAction is empty, and the committed plan holds the
// replacement placements alone. The combined path is the opposite and requires
// *every* dispatched placement to still be withdrawable, so a relocation is
// never partial: Targets is nonempty exactly when FastPath is false.
type RelocateConstructionSubmission struct {
	Request      RelocateConstructionSubmissionRequest
	Source       domain.PlanID
	Targets      []domain.ActionID
	Plan         domain.PlanID
	CancelAction domain.ActionID
	BuildAction  domain.ActionID
	Revision     domain.PlanRevision
	FastPath     bool
}

// CancelActionIDs is every committed withdrawal identity in target order, empty
// on the fast path.
func (v RelocateConstructionSubmission) CancelActionIDs() []domain.ActionID {
	if v.FastPath {
		return nil
	}
	ids := make([]domain.ActionID, 0, len(v.Targets))
	for i := range v.Targets {
		ids = append(ids, cancelConstructionActionID(v.CancelAction, i))
	}
	return ids
}

// BuildActionIDs is every committed replacement placement identity in the
// replacement shell's own expansion order, the entrance first.
func (v RelocateConstructionSubmission) BuildActionIDs() []domain.ActionID {
	_, ids, err := buildRoomActions(v.Request.Replacement, string(v.BuildAction))
	if err != nil {
		return nil
	}
	return ids
}

// relocateConstructionPayload is the durable record of what this relocation
// covered. Like cancelConstructionPayload it is stored rather than recomputed,
// because the source plan's progress moves on after submission and the record
// must keep describing what was actually committed.
type relocateConstructionPayload struct {
	Source      domain.PlanID
	Targets     []domain.ActionID
	Replacement roomShellPayload
}

func (r RelocateConstructionSubmissionRequest) validate() error {
	if err := submissionID(r.RequestID); err != nil {
		return err
	}
	if err := r.World.Validate(); err != nil {
		return err
	}
	if err := domain.ValidateRoomIntent(r.IntentID); err != nil {
		return err
	}
	canonical, err := reconstructRoomShell(roomShellPayloadOf(r.Replacement))
	if err != nil || canonical != r.Replacement {
		return errors.New("invalid replacement room shell configuration")
	}
	if len(r.Replacement.Placements()) == 0 {
		return errors.New("replacement room shell expands to no placements")
	}
	return nil
}

// ConstructionIntentHead is the construction one world-scoped player intent
// currently names.
//
// A build_room intent starts out naming its own committed plan, and each
// relocation of that intent supersedes the head with the plan it commits. The
// head is therefore the tip of an append-only chain rather than a mutated row:
// no relocation ever rewrites the build_room submission it supersedes, which is
// what keeps BuildRoom's own request-ID replay guarantee -- the same request ID
// always resolving to the same committed plan and placements -- intact through
// any number of relocations.
type ConstructionIntentHead struct {
	World       World
	IntentID    string
	Room        domain.RoomShell
	Plan        domain.PlanID
	Action      domain.ActionID
	Relocations int
}

// ActionIDs is every placement identity the head's construction committed, in
// the shell's own expansion order. It is derived exactly as
// BuildRoomSubmission.ActionIDs is, because a relocated head's placements are
// expanded by the same total function over the same prefix.
func (h ConstructionIntentHead) ActionIDs() []domain.ActionID {
	_, ids, err := buildRoomActions(h.Room, string(h.Action))
	if err != nil {
		return nil
	}
	return ids
}

// LookupConstructionIntent resolves one world-scoped player construction intent
// to the construction it currently names, following every relocation of it.
func (s *Store) LookupConstructionIntent(ctx context.Context, world World, intentID string) (ConstructionIntentHead, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return ConstructionIntentHead{}, err
	}
	defer tx.Rollback()
	head, err := lookupConstructionIntent(ctx, tx, world, intentID)
	if err != nil {
		return ConstructionIntentHead{}, err
	}
	if err = tx.Commit(); err != nil {
		return ConstructionIntentHead{}, err
	}
	return head, nil
}

// maxIntentRelocations bounds the supersession walk. The chain cannot loop --
// each link names a plan whose identity is fresh entropy, and a plan may be
// superseded at most once by the table's own uniqueness -- so this is a bound on
// pathological length, not a cycle guard.
const maxIntentRelocations = 256

// lookupConstructionIntent is the in-transaction resolver every follow-up
// command on a construction intent uses. It starts at the intent's original
// build_room submission and walks the relocation chain forwards, each link
// naming the plan it supersedes, until no relocation supersedes the current
// head.
func lookupConstructionIntent(ctx context.Context, tx *sql.Tx, world World, intentID string) (ConstructionIntentHead, error) {
	source, err := lookupBuildRoomIntent(ctx, tx, world, intentID)
	if err != nil {
		return ConstructionIntentHead{}, err
	}
	head := ConstructionIntentHead{World: world, IntentID: intentID, Room: source.Request.Room, Plan: source.Plan, Action: source.Action}
	for head.Relocations < maxIntentRelocations {
		var requestID string
		err = tx.QueryRowContext(ctx, "SELECT request_id FROM relocate_construction_submissions WHERE colony=? AND load_token=? AND map_id=? AND intent_id=? AND source_plan=?",
			world.Colony, world.Load, world.Map, intentID, head.Plan).Scan(&requestID)
		if errors.Is(err, sql.ErrNoRows) {
			return head, nil
		}
		if err != nil {
			return ConstructionIntentHead{}, err
		}
		next, err := lookupRelocateConstruction(ctx, tx, requestID)
		if err != nil {
			return ConstructionIntentHead{}, err
		}
		if next.Source != head.Plan || next.Request.World != world || next.Request.IntentID != intentID {
			return ConstructionIntentHead{}, errors.New("relocate construction chain is corrupt")
		}
		head.Room, head.Plan, head.Action = next.Request.Replacement, next.Plan, next.BuildAction
		head.Relocations++
	}
	return ConstructionIntentHead{}, errors.New("construction intent has been relocated too many times")
}

// relocatableSource decides whether one already-committed construction may be
// relocated at all, and on which of the two paths.
//
// Python makes the same split in two places: the early exit when the original
// construction "was never issued at all", and the combined path's insistence
// that every issued placement still be capturable. Both are reproduced here
// against journalled progress rather than a Python PlanStep's `issued` set.
//
// fast reports that not one placement of the source was ever dispatched, so
// nothing native exists to withdraw and relocation is a pure supersession.
// Otherwise every dispatched placement must still be cleanly withdrawable:
// Python's "Relocation requires every issued placement to remain pending;
// completed or missing construction is preserved" refuses outright rather than
// relocating half a room, because a completed or independently removed
// placement cannot be read as consent to duplicate it somewhere else.
//
// supersede is the source's never-dispatched placements. They placed nothing
// native, so they need no cancellation -- but they are still live committed work
// that a worker would otherwise go on to build at the old location, which is
// exactly what relocation asks not to happen. The caller cancels their progress
// in the same transaction. This is the one respect in which relocation is more
// than cancel-plus-build: CancelConstruction leaves them alone because
// withdrawing an intent's orders is all it promises.
func relocatableSource(state PlanState, ids []domain.ActionID) (targets []domain.ActionID, cancels []domain.ConstructionCancel, supersede []domain.ActionID, fast bool, err error) {
	targets, cancels, err = cancellableTargets(state, ids)
	if err != nil {
		return nil, nil, nil, false, err
	}
	actions := state.Spec.Actions()
	issued := 0
	for _, id := range ids {
		for i, a := range actions {
			if a.ID() != id {
				continue
			}
			v := state.Progress[i].View()
			if v.Attempt != 0 {
				issued++
			} else if !v.Unresolved && (v.Stage == domain.Pending || v.Stage == domain.Prepared) {
				supersede = append(supersede, id)
			}
			break
		}
	}
	// Python's own fast path additionally requires the step to be pending or
	// blocked, which is why an undispatched-but-already-cancelled placement does
	// not qualify: nothing was issued, but the construction is no longer whole.
	if issued == 0 {
		if len(supersede) != len(ids) {
			return nil, nil, nil, false, errors.New("relocation requires the whole construction to remain open; resolved placements are preserved")
		}
		return nil, nil, supersede, true, nil
	}
	if len(targets) == 0 || len(targets) != issued {
		return nil, nil, nil, false, errors.New("relocation requires every issued placement to remain pending; completed or missing construction is preserved")
	}
	return targets, cancels, supersede, false, nil
}

// dependentOnSource reports whether any action in the source's own plan requires
// one of the construction's placements, the Go form of Python's "Dependent work
// references this construction; resolve it before relocation".
//
// domain.ActionDependency is plan-local, so only the source plan can hold such
// an edge, and nothing in the player-command family creates one pointing *at* a
// placement today: build_room commits its plan with no dependencies at all, and
// a relocation's own edges point at its cancellations, never at its placements.
// The check is therefore currently vacuous. It is kept because it is two loops
// over data already in hand and it is the guard that makes adding a dependent
// command later safe by default rather than silently destructive.
func dependentOnSource(state PlanState, ids []domain.ActionID) bool {
	placement := make(map[domain.ActionID]bool, len(ids))
	for _, id := range ids {
		placement[id] = true
	}
	for _, d := range state.Spec.Dependencies() {
		if placement[d.Requires] && !placement[d.Action] {
			return true
		}
	}
	return false
}

// SubmitRelocateConstruction atomically supersedes one player construction
// intent with a replacement shell somewhere else, committing a single plan that
// holds both the withdrawal of the old orders and the placement of the new ones.
//
// The two halves travel in one plan rather than two because they must be
// ordered: nothing of the replacement may be placed until the old orders are
// actually gone. domain.ActionDependency expresses that ordering, and it is
// plan-local, which is what forces the single plan. The edges mirror Python's
// coarse two-step shape -- one monolithic removal, then one monolithic
// replacement `after` it -- rather than pairing cells: the withdrawals run in a
// chain and every replacement placement requires the last of them, so the
// replacement starts only once every old order is observed complete and then
// proceeds in whatever order the colony finds convenient. Pairing each new cell
// to the old cell it replaces would be finer but wrong at the fan-out this
// command reaches: a 64-by-64 shell is 252 placements, and requiring each of
// them of each of 252 withdrawals is 63504 edges against domain's 4096 bound,
// where the chain is at most 503.
//
// Submission neither acquires authority nor touches anything native: a worker
// later admits each committed action, and the dependency gate holds every
// replacement placement out of admission until the withdrawals have completed in
// the current world.
func (s *Store) SubmitRelocateConstruction(ctx context.Context, r RelocateConstructionSubmissionRequest) (RelocateConstructionSubmission, bool, error) {
	if err := r.validate(); err != nil {
		return RelocateConstructionSubmission{}, false, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return RelocateConstructionSubmission{}, false, err
	}
	defer tx.Rollback()
	old, err := lookupRelocateConstruction(ctx, tx, r.RequestID)
	if err == nil {
		if old.Request != r {
			return RelocateConstructionSubmission{}, false, ErrConflict
		}
		if err = tx.Commit(); err != nil {
			return RelocateConstructionSubmission{}, false, err
		}
		return old, false, nil
	}
	if !errors.Is(err, ErrNotFound) {
		return RelocateConstructionSubmission{}, false, err
	}
	head, err := lookupConstructionIntent(ctx, tx, r.World, r.IntentID)
	if err != nil {
		return RelocateConstructionSubmission{}, false, err
	}
	// Python refuses `source.action == request.replacement` before it looks at
	// any progress: moving a room to exactly where it already is issues native
	// churn for no change and is far more likely a mistake than a request.
	if head.Room == r.Replacement {
		return RelocateConstructionSubmission{}, false, errors.New("replacement geometry is unchanged")
	}
	state, err := load(ctx, tx, head.Plan)
	if err != nil {
		return RelocateConstructionSubmission{}, false, err
	}
	ids := head.ActionIDs()
	if len(ids) == 0 {
		return RelocateConstructionSubmission{}, false, errors.New("construction intent expands to no placements")
	}
	if dependentOnSource(state, ids) {
		return RelocateConstructionSubmission{}, false, errors.New("dependent work references this construction; resolve it before relocation")
	}
	targets, cancels, supersede, fast, err := relocatableSource(state, ids)
	if err != nil {
		return RelocateConstructionSubmission{}, false, err
	}
	var entropy [32]byte
	if _, err = rand.Read(entropy[:]); err != nil {
		return RelocateConstructionSubmission{}, false, err
	}
	result := RelocateConstructionSubmission{
		Request:     r,
		Source:      head.Plan,
		Targets:     targets,
		Plan:        domain.PlanID("relocate-construction-" + hex.EncodeToString(entropy[:16])),
		BuildAction: domain.ActionID("relocate-build-action-" + hex.EncodeToString(entropy[16:])),
		Revision:    1,
		FastPath:    fast,
	}
	if !fast {
		result.CancelAction = domain.ActionID("relocate-cancel-action-" + hex.EncodeToString(entropy[16:]))
	}
	actions := make([]domain.Action, 0, len(cancels)+len(r.Replacement.Placements()))
	var dependencies []domain.ActionDependency
	var last domain.ActionID
	for i, cancel := range cancels {
		id := cancelConstructionActionID(result.CancelAction, i)
		action, err := domain.NewConstructionCancelAction(id, cancel)
		if err != nil {
			return RelocateConstructionSubmission{}, false, err
		}
		actions = append(actions, action)
		if last != "" {
			dependencies = append(dependencies, domain.ActionDependency{Action: id, Requires: last})
		}
		last = id
	}
	placements, placementIDs, err := buildRoomActions(r.Replacement, string(result.BuildAction))
	if err != nil {
		return RelocateConstructionSubmission{}, false, err
	}
	actions = append(actions, placements...)
	if last != "" {
		for _, id := range placementIDs {
			dependencies = append(dependencies, domain.ActionDependency{Action: id, Requires: last})
		}
	}
	plan, err := domain.NewPlan(result.Plan, 1, actions, dependencies...)
	if err != nil {
		return RelocateConstructionSubmission{}, false, err
	}
	if err = createPlan(ctx, tx, plan); err != nil {
		return RelocateConstructionSubmission{}, false, err
	}
	// Withdraw the source's never-dispatched placements from this controller's
	// own work. They have no native order to cancel, so nothing here reaches the
	// game; what it prevents is a worker going on to build the old room beside
	// the new one.
	for _, id := range supersede {
		if _, err = advanceInTransaction(ctx, tx, head.Plan, id, transition{Kind: "cancel"}); err != nil {
			return RelocateConstructionSubmission{}, false, err
		}
	}
	data, err := json.Marshal(relocateConstructionPayload{Source: head.Plan, Targets: targets, Replacement: roomShellPayloadOf(r.Replacement)})
	if err != nil {
		return RelocateConstructionSubmission{}, false, err
	}
	var cancelAction any
	if !fast {
		cancelAction = string(result.CancelAction)
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO relocate_construction_submissions(request_id,colony,load_token,map_id,intent_id,source_plan,plan_id,cancel_action,build_action,revision,payload) VALUES(?,?,?,?,?,?,?,?,?,?,?)",
		r.RequestID, r.World.Colony, r.World.Load, r.World.Map, r.IntentID, head.Plan, string(result.Plan), cancelAction, string(result.BuildAction), "1", data); err != nil {
		return RelocateConstructionSubmission{}, false, conflict(err)
	}
	if err = tx.Commit(); err != nil {
		return RelocateConstructionSubmission{}, false, err
	}
	return result, true, nil
}

func (s *Store) LookupRelocateConstructionSubmission(ctx context.Context, requestID string) (RelocateConstructionSubmission, error) {
	if err := submissionID(requestID); err != nil {
		return RelocateConstructionSubmission{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return RelocateConstructionSubmission{}, err
	}
	defer tx.Rollback()
	result, err := lookupRelocateConstruction(ctx, tx, requestID)
	if err != nil {
		return RelocateConstructionSubmission{}, err
	}
	if err = tx.Commit(); err != nil {
		return RelocateConstructionSubmission{}, err
	}
	return result, nil
}

func lookupRelocateConstruction(ctx context.Context, tx *sql.Tx, id string) (RelocateConstructionSubmission, error) {
	var (
		world              World
		intent, revision   string
		sourcePlan, planID string
		buildAction        string
		cancelAction       sql.NullString
		data               []byte
	)
	err := tx.QueryRowContext(ctx, "SELECT colony,load_token,map_id,intent_id,source_plan,plan_id,cancel_action,build_action,revision,payload FROM relocate_construction_submissions WHERE request_id=?", id).
		Scan(&world.Colony, &world.Load, &world.Map, &intent, &sourcePlan, &planID, &cancelAction, &buildAction, &revision, &data)
	if errors.Is(err, sql.ErrNoRows) {
		return RelocateConstructionSubmission{}, ErrNotFound
	}
	if err != nil {
		return RelocateConstructionSubmission{}, err
	}
	if len(data) > 32768 {
		return RelocateConstructionSubmission{}, errors.New("relocate construction submission exceeds bound")
	}
	var payload relocateConstructionPayload
	if err = json.Unmarshal(data, &payload); err != nil {
		return RelocateConstructionSubmission{}, err
	}
	canonical, err := json.Marshal(payload)
	if err != nil {
		return RelocateConstructionSubmission{}, err
	}
	if !bytes.Equal(canonical, data) {
		return RelocateConstructionSubmission{}, errors.New("noncanonical relocate construction submission")
	}
	if revision != "1" {
		return RelocateConstructionSubmission{}, errors.New("invalid submitted revision")
	}
	replacement, err := reconstructRoomShell(payload.Replacement)
	if err != nil {
		return RelocateConstructionSubmission{}, err
	}
	result := RelocateConstructionSubmission{
		Request:      RelocateConstructionSubmissionRequest{RequestID: id, World: world, IntentID: intent, Replacement: replacement},
		Source:       payload.Source,
		Targets:      payload.Targets,
		Plan:         domain.PlanID(planID),
		CancelAction: domain.ActionID(cancelAction.String),
		BuildAction:  domain.ActionID(buildAction),
		Revision:     1,
		FastPath:     !cancelAction.Valid,
	}
	if err = result.Request.validate(); err != nil {
		return RelocateConstructionSubmission{}, err
	}
	if result.Source != domain.PlanID(sourcePlan) || result.FastPath != (len(result.Targets) == 0) {
		return RelocateConstructionSubmission{}, errors.New("relocate construction submission is corrupt")
	}
	source, err := load(ctx, tx, result.Source)
	if err != nil {
		return RelocateConstructionSubmission{}, err
	}
	expected := make([]domain.ConstructionCancel, 0, len(result.Targets))
	for _, target := range result.Targets {
		found := false
		for _, a := range source.Spec.Actions() {
			if a.ID() != target {
				continue
			}
			building, ok := a.Building()
			if !ok {
				return RelocateConstructionSubmission{}, errors.New("relocate construction target is not a placement")
			}
			cancel, err := domain.NewConstructionCancelFor(building)
			if err != nil {
				return RelocateConstructionSubmission{}, err
			}
			expected = append(expected, cancel)
			found = true
			break
		}
		if !found {
			return RelocateConstructionSubmission{}, errors.New("relocate construction target is missing from its source plan")
		}
	}
	wanted, wantedIDs, err := buildRoomActions(replacement, buildAction)
	if err != nil {
		return RelocateConstructionSubmission{}, err
	}
	state, err := load(ctx, tx, result.Plan)
	if err != nil {
		return RelocateConstructionSubmission{}, err
	}
	actions := state.Spec.Actions()
	if state.Spec.Revision() != 1 || len(actions) != len(expected)+len(wanted) {
		return RelocateConstructionSubmission{}, errors.New("relocate construction submission plan is corrupt")
	}
	for i, a := range actions[:len(expected)] {
		if a.ID() != cancelConstructionActionID(result.CancelAction, i) {
			return RelocateConstructionSubmission{}, errors.New("relocate construction submission plan is corrupt")
		}
		actual, ok := a.ConstructionCancel()
		if !ok || actual != expected[i] {
			return RelocateConstructionSubmission{}, errors.New("relocate construction submission differs from intent")
		}
	}
	for i, a := range actions[len(expected):] {
		if a.ID() != wanted[i].ID() {
			return RelocateConstructionSubmission{}, errors.New("relocate construction submission plan is corrupt")
		}
		actual, ok := a.Building()
		want, _ := wanted[i].Building()
		if !ok || actual != want {
			return RelocateConstructionSubmission{}, errors.New("relocate construction submission differs from intent")
		}
	}
	if err = checkRelocateDependencies(state.Spec, result.CancelAction, len(expected), wantedIDs); err != nil {
		return RelocateConstructionSubmission{}, err
	}
	return result, nil
}

// checkRelocateDependencies replays the exact ordering SubmitRelocateConstruction
// committed: the withdrawals in a chain, then every replacement placement
// requiring the last of them. A relocation whose plan lost or gained an edge
// would otherwise read back as sound while being free to place the replacement
// before the old orders are gone.
func checkRelocateDependencies(spec domain.PlanSpec, cancelPrefix domain.ActionID, cancels int, placements []domain.ActionID) error {
	want := map[domain.ActionDependency]bool{}
	for i := 1; i < cancels; i++ {
		want[domain.ActionDependency{Action: cancelConstructionActionID(cancelPrefix, i), Requires: cancelConstructionActionID(cancelPrefix, i-1)}] = true
	}
	if cancels > 0 {
		last := cancelConstructionActionID(cancelPrefix, cancels-1)
		for _, id := range placements {
			want[domain.ActionDependency{Action: id, Requires: last}] = true
		}
	}
	found := spec.Dependencies()
	if len(found) != len(want) {
		return errors.New("relocate construction submission plan is corrupt")
	}
	for _, d := range found {
		if !want[d] {
			return errors.New("relocate construction submission plan is corrupt")
		}
	}
	return nil
}
