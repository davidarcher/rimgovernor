package store

import (
	"bytes"
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"fmt"
	"strconv"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// StandardState links methods to the existing plan catalog. Revision is a local CAS
// token, not a native generation or permission to run a method.
type StandardState struct {
	Standard domain.Standard
	Revision uint64
	Methods  []domain.Method // Active plans; retired methods remain in LoadMethod.
	// Admitted counts every method ever committed for the goal, retired
	// plans included: a monotonic salt for method identities that must not
	// collide with a retired plan's row (#214).
	Admitted int
	// History lists the current epoch's methods, retired plans included.
	// Planners that number attempts count it, not Methods: a retired plan
	// drops out of Methods, and re-deriving its ID fails plans.id's unique
	// constraint.
	History []domain.Method
	Retired bool
}

const maxActiveStandards = 512

// initializeGoals creates the goal lifecycle tables. goals and rounds
// are session caches (#1011): RebuildStandards refills goals and projects from the save and
// ResetRounds empties rounds on every world change, and the
// next review recomputes it. Goal-create request replay is in memory only.
func initializeStandards(ctx context.Context, tx *sql.Tx) error {
	_, err := tx.ExecContext(ctx, `CREATE TABLE standards(id TEXT PRIMARY KEY, revision TEXT NOT NULL, payload BLOB NOT NULL, retired INTEGER NOT NULL DEFAULT 0 CHECK(retired IN (0,1))) STRICT;
CREATE INDEX active_standards ON standards(id) WHERE retired=0;
CREATE TABLE incidents(id TEXT PRIMARY KEY, colony TEXT NOT NULL, load_token TEXT NOT NULL, map_id INTEGER NOT NULL, kind TEXT NOT NULL, subject TEXT NOT NULL, started_tick INTEGER NOT NULL, ended_tick INTEGER, payload BLOB NOT NULL) STRICT;
CREATE UNIQUE INDEX open_incidents ON incidents(colony,load_token,map_id,kind,subject) WHERE ended_tick IS NULL;
CREATE TABLE projects(id TEXT PRIMARY KEY, revision TEXT NOT NULL, payload BLOB NOT NULL, retired INTEGER NOT NULL DEFAULT 0 CHECK(retired IN (0,1))) STRICT;
CREATE INDEX active_projects ON projects(id) WHERE retired=0;
CREATE TABLE plan_owner(plan_id TEXT PRIMARY KEY REFERENCES plans(id), kind TEXT NOT NULL CHECK(kind IN ('standard','project','incident')), UNIQUE(plan_id,kind)) STRICT;
CREATE TABLE standard_methods(standard_id TEXT NOT NULL REFERENCES standards(id), episode TEXT NOT NULL, method_id TEXT NOT NULL, plan_id TEXT NOT NULL UNIQUE, kind TEXT NOT NULL DEFAULT 'standard' CHECK(kind='standard'), priority INTEGER NOT NULL, reason TEXT, PRIMARY KEY(standard_id,episode,method_id), FOREIGN KEY(plan_id,kind) REFERENCES plan_owner(plan_id,kind)) STRICT;
CREATE TABLE project_methods(project_id TEXT NOT NULL REFERENCES projects(id), method_id TEXT NOT NULL, plan_id TEXT NOT NULL UNIQUE, kind TEXT NOT NULL DEFAULT 'project' CHECK(kind='project'), priority INTEGER NOT NULL, reason TEXT, PRIMARY KEY(project_id,method_id), FOREIGN KEY(plan_id,kind) REFERENCES plan_owner(plan_id,kind)) STRICT;
CREATE TABLE incident_methods(incident_id TEXT NOT NULL REFERENCES incidents(id), method_id TEXT NOT NULL, plan_id TEXT NOT NULL UNIQUE, kind TEXT NOT NULL DEFAULT 'incident' CHECK(kind='incident'), priority INTEGER NOT NULL, reason TEXT, PRIMARY KEY(incident_id,method_id), FOREIGN KEY(plan_id,kind) REFERENCES plan_owner(plan_id,kind)) STRICT;
CREATE VIEW plan_methods AS SELECT plan_id,kind,standard_id AS owner_id,episode,method_id,priority,reason FROM standard_methods UNION ALL SELECT plan_id,kind,project_id,NULL,method_id,priority,reason FROM project_methods UNION ALL SELECT plan_id,kind,incident_id,NULL,method_id,priority,reason FROM incident_methods;
CREATE TABLE rounds(singleton INTEGER PRIMARY KEY CHECK(singleton=1), payload BLOB NOT NULL) STRICT;
CREATE TABLE defense_layout(singleton INTEGER PRIMARY KEY CHECK(singleton=1), payload BLOB NOT NULL) STRICT;
CREATE TABLE production_ladder(singleton INTEGER PRIMARY KEY CHECK(singleton=1), payload BLOB NOT NULL) STRICT;
CREATE TABLE combat_restoration(singleton INTEGER PRIMARY KEY CHECK(singleton=1), payload BLOB NOT NULL) STRICT;
CREATE TABLE soldier_squad(singleton INTEGER PRIMARY KEY CHECK(singleton=1), payload BLOB NOT NULL) STRICT;`)
	return err
}

func createStandard(ctx context.Context, tx *sql.Tx, g domain.Standard) error {
	if err := g.Validate(); err != nil {
		return err
	}
	var count int
	if err := tx.QueryRowContext(ctx, "SELECT count(*) FROM standards WHERE retired=0").Scan(&count); err != nil {
		return err
	}
	if count >= maxActiveStandards {
		return ErrCapacity
	}
	data, err := json.Marshal(g)
	if err != nil {
		return err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO standards(id,revision,payload) VALUES(?,?,?)", g.ID, "0", data); err != nil {
		return conflict(err)
	}
	return nil
}

func loadStandard(ctx context.Context, tx *sql.Tx, id domain.ConcernID) (StandardState, error) {
	var out StandardState
	var data []byte
	var revision string
	if err := tx.QueryRowContext(ctx, "SELECT revision,payload,retired FROM standards WHERE id=?", id).Scan(&revision, &data, &out.Retired); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			err = ErrNotFound
		}
		return out, err
	}
	n, err := strconv.ParseUint(revision, 10, 64)
	if err != nil || strconv.FormatUint(n, 10) != revision {
		return out, errors.New("invalid standard revision")
	}
	if len(data) > 8192 {
		return out, errors.New("standard payload exceeds bound")
	}
	if err = json.Unmarshal(data, &out.Standard); err != nil {
		return StandardState{}, err
	}
	canonical, err := json.Marshal(out.Standard)
	if err != nil || !bytes.Equal(data, canonical) {
		return StandardState{}, errors.New("noncanonical standard state")
	}
	if err = out.Standard.Validate(); err != nil {
		return StandardState{}, err
	}
	if out.Standard.ID != id {
		return StandardState{}, errors.New("standard identity mismatch")
	}
	if out.Retired && out.Standard.Status != domain.StandardVoided {
		return StandardState{}, errors.New("invalid retired standard")
	}
	out.Revision = n
	if err := tx.QueryRowContext(ctx, "SELECT COUNT(*) FROM standard_methods WHERE standard_id=?", id).Scan(&out.Admitted); err != nil {
		return StandardState{}, err
	}
	history, err := tx.QueryContext(ctx, "SELECT method_id,plan_id FROM standard_methods WHERE standard_id=? AND episode=? ORDER BY method_id", id, strconv.FormatUint(out.Standard.Episode, 10))
	if err != nil {
		return StandardState{}, err
	}
	for history.Next() {
		m := domain.Method{Owner: id, Episode: out.Standard.Episode}
		if err = history.Scan(&m.Method, &m.Plan); err != nil {
			history.Close()
			return StandardState{}, err
		}
		out.History = append(out.History, m)
	}
	err = history.Err()
	history.Close()
	if err != nil {
		return StandardState{}, err
	}
	rows, err := tx.QueryContext(ctx, "SELECT m.episode,m.method_id,m.plan_id FROM plans p INDEXED BY active_plans CROSS JOIN standard_methods m ON m.plan_id=p.id WHERE p.retired=0 AND m.standard_id=? ORDER BY length(m.episode),m.episode,m.method_id", id)
	if err != nil {
		return StandardState{}, err
	}
	defer rows.Close()
	for rows.Next() {
		var m domain.Method
		var epoch string
		m.Owner = id
		if err = rows.Scan(&epoch, &m.Method, &m.Plan); err != nil {
			return StandardState{}, err
		}
		m.Episode, err = strconv.ParseUint(epoch, 10, 64)
		if err != nil || strconv.FormatUint(m.Episode, 10) != epoch || m.Episode > out.Standard.Episode || m.Validate() != nil {
			return StandardState{}, errors.New("invalid standard method record")
		}
		out.Methods = append(out.Methods, m)
		if len(out.Methods) > 256 {
			return StandardState{}, ErrCapacity
		}
	}
	return out, rows.Err()
}

func (s *Store) LoadStandard(ctx context.Context, id domain.ConcernID) (StandardState, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return StandardState{}, err
	}
	defer tx.Rollback()
	out, err := loadStandard(ctx, tx, id)
	if err != nil {
		return StandardState{}, err
	}
	if err = tx.Commit(); err != nil {
		return StandardState{}, err
	}
	return out, nil
}

func saveStandard(ctx context.Context, tx *sql.Tx, previous StandardState, g domain.Standard) (StandardState, error) {
	if previous.Retired {
		return StandardState{}, errors.New("retired standard is read-only")
	}
	if g == previous.Standard {
		return previous, nil
	}
	if previous.Revision == ^uint64(0) {
		return StandardState{}, ErrCapacity
	}
	if err := g.Validate(); err != nil {
		return StandardState{}, err
	}
	data, err := json.Marshal(g)
	if err != nil {
		return StandardState{}, err
	}
	next := previous.Revision + 1
	if _, err = tx.ExecContext(ctx, "UPDATE standards SET revision=?,payload=? WHERE id=?", strconv.FormatUint(next, 10), data, g.ID); err != nil {
		return StandardState{}, err
	}
	previous.Standard = g
	previous.Revision = next
	return previous, nil
}

func standardOpenWork(ctx context.Context, tx *sql.Tx, owner methodOwner) (bool, error) {
	open := false
	for _, plan := range owner.ownerPlans() {
		p, err := load(ctx, tx, plan)
		if err != nil {
			return false, err
		}
		open = open || PlanOpen(p) && !rulesAttachOnly(p.Spec)
		if !open {
			// A fight's drafts are its open work (#910).
			if open, err = combatFightHolds(ctx, tx, plan); err != nil {
				return false, err
			}
		}
	}
	return open, nil
}

// rulesAttachOnly reports a plan of native rule attachments alone (#2154): one
// in-memory write per Round that holds no pawn work, so it neither waits behind
// the owner's open work nor counts as open work for its other planners.
func rulesAttachOnly(plan domain.PlanSpec) bool {
	if len(plan.Actions()) == 0 {
		return false
	}
	for _, action := range plan.Actions() {
		if action.Kind() != domain.RulesAttachAction {
			return false
		}
	}
	return true
}

// planOpenWork is goalOpenWork without the fights: whether any of the
// owner's plans is still open.
func planOpenWork(ctx context.Context, tx *sql.Tx, owner methodOwner) (bool, error) {
	for _, plan := range owner.ownerPlans() {
		p, err := load(ctx, tx, plan)
		if err != nil || PlanOpen(p) {
			return err == nil, err
		}
	}
	return false, nil
}

func (s *Store) ReviewStandard(ctx context.Context, id domain.ConcernID, revision uint64, current domain.GenerationSnapshot, tick domain.Tick, need domain.Finding) (StandardState, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return StandardState{}, err
	}
	defer tx.Rollback()
	state, err := loadStandard(ctx, tx, id)
	if err != nil {
		return StandardState{}, err
	}
	if state.Revision != revision {
		return StandardState{}, ErrConflict
	}
	open, err := standardOpenWork(ctx, tx, state)
	if err != nil {
		return StandardState{}, err
	}
	g, err := domain.ReviewStandard(state.Standard, current, need, open)
	if err != nil {
		return StandardState{}, err
	}
	if g.Status == domain.StandardVoided {
		if err = cancelMethods(ctx, tx, state); err != nil {
			return StandardState{}, err
		}
	}
	out, err := saveStandard(ctx, tx, state, g)
	if err != nil {
		return StandardState{}, err
	}
	if err = tx.Commit(); err != nil {
		return StandardState{}, err
	}
	return out, nil
}

// CommitMethod stores the method and its shared plan atomically. Admission
// and dispatch still belong to existing policy/Hands; this grants no authority.
func (s *Store) CommitMethod(ctx context.Context, id domain.ConcernID, revision uint64, method domain.MethodID, plan domain.PlanSpec) (StandardState, error) {
	return s.CommitMethodReason(ctx, id, revision, method, "", plan)
}

// CommitMethodReason is CommitMethod with the planner's short reason
// for admitting it (runway, deficit, target); the dispatcher appends it to
// Operation.intent (#846). Empty stores none.
func (s *Store) CommitMethodReason(ctx context.Context, id domain.ConcernID, revision uint64, method domain.MethodID, reason string, plan domain.PlanSpec) (StandardState, error) {
	if err := plan.Validate(); err != nil {
		return StandardState{}, err
	}
	if len(plan.Actions()) == 0 {
		return StandardState{}, errors.New("empty standard method")
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return StandardState{}, err
	}
	defer tx.Rollback()
	state, err := commitMethod(ctx, tx, id, revision, method, reason, plan)
	if err != nil {
		return StandardState{}, err
	}
	if err = tx.Commit(); err != nil {
		return StandardState{}, err
	}
	return state, nil
}

func commitMethod(ctx context.Context, tx *sql.Tx, id domain.ConcernID, revision uint64, method domain.MethodID, reason string, plan domain.PlanSpec) (StandardState, error) {
	state, err := loadStandard(ctx, tx, id)
	if err != nil {
		return StandardState{}, err
	}
	if err = admitOwnerCommit(ctx, tx, state, revision, method, reason, plan); err != nil {
		return StandardState{}, err
	}
	return loadStandard(ctx, tx, id)
}

// admitOwnerCommit is the commit every method owner shares, a goal or a
// Project: the revision CAS, the open-deficit check, the Safeguards and current review, the open-work check with its exemptions, then the family
// admission, the methods row and the revision bump. The caller reloads
// the owner.
func admitOwnerCommit(ctx context.Context, tx *sql.Tx, state WorkOwner, revision uint64, method domain.MethodID, reason string, plan domain.PlanSpec) error {
	summary, _ := SummarizeOwner(state)
	if state.OwnerRevision() != revision {
		return fmt.Errorf("%w: %s is at revision %d, not %d", ErrStaleOwner, state.ownerLabel(), state.OwnerRevision(), revision)
	}
	if summary.Status != domain.StandardOpen || summary.Finding != domain.FindingUnmet {
		return errors.New("standard does not admit a method")
	}
	if err := admitRoundsSafeguards(ctx, tx, state); err != nil {
		return err
	}
	if err := admitRoundsReview(ctx, tx, summary); err != nil {
		return err
	}

	open, err := standardOpenWork(ctx, tx, state)
	if err != nil {
		return err
	}
	if open {
		// An uncertain remote request earns no stock and cannot monopolize
		// its supply owner. Ordinary local methods may proceed while its
		// separate Hands attempt remains unresolved.
		onlyRequests := true
		for _, linked := range state.OwnerMethods() {
			pending, err := load(ctx, tx, linked.Plan)
			if err != nil {
				return err
			}
			for _, progress := range pending.Progress {
				if domain.StandardWorkOpen([]domain.Progress{progress}) && progress.Action().Kind() != domain.CommsTradeRequestAction {
					onlyRequests = false
				}
			}
		}
		for _, action := range plan.Actions() {
			if action.Kind() == domain.CommsTradeRequestAction {
				onlyRequests = false
			}
		}
		if onlyRequests {
			open = false
		}
	}
	if open {
		exempt, err := acquisitionOpenWorkExempt(ctx, tx, state, plan)
		if err != nil {
			return err
		}
		if !exempt {
			exempt, err = fieldOpenWorkExempt(ctx, tx, state, plan)
			if err != nil {
				return err
			}
		}
		if !exempt {
			exempt = growerCropOpenWorkExempt(plan) || rulesAttachOnly(plan) || mealReplacementOpenWorkExempt(state, plan)
		}
		if !exempt {
			exempt, err = foodFacilityOpenWorkExempt(ctx, tx, state, plan)
			if err != nil {
				return err
			}
		}
		if !exempt {
			exempt, err = reserveAccessOpenWorkExempt(ctx, tx, state, plan)
			if err != nil {
				return err
			}
		}
		if !exempt {
			exempt, err = gearOpenWorkExempt(ctx, tx, state, plan)
			if err != nil {
				return err
			}
		}
		if !exempt {
			exempt, err = shelterOpenWorkExempt(ctx, tx, state, plan)
			if err != nil {
				return err
			}
		}
		if !exempt {
			exempt, err = roomShellOpenWorkExempt(ctx, tx, state, method, plan)
			if err != nil {
				return err
			}
		}
		if !exempt {
			exempt, err = buildingOpenWorkExempt(ctx, tx, state, plan)
			if err != nil {
				return err
			}
		}
		if !exempt {
			return ErrOpenMethod
		}
	}
	m := domain.Method{Owner: domain.ConcernID(summary.ID), Episode: summary.Episode, Method: method, Plan: plan.ID()}
	if err = m.Validate(); err != nil {
		return err
	}
	if state.OwnerRevision() == ^uint64(0) {
		return ErrCapacity
	}
	if err = bindOwnerMethod(ctx, tx, state, method, reason, plan); err != nil {
		return err
	}
	return state.bumpRevision(ctx, tx)
}

// cancelUndispatchedMethods cancels every open method of the goal that
// no step ever dispatched: each action is still Pending or Prepared on its
// first attempt. A review that observes the goal recovered runs it before
// counting open work, so a plan admitted for a deficit the colonists (or the
// player) cleared on their own settles here instead of holding the goal
// Active and never authorized: the worker refuses a recovered goal's fresh
// write on every step, and nothing else ever retires the plan (#290). Work
// already dispatched keeps its own settlement path.
func cancelUndispatchedMethods(ctx context.Context, tx *sql.Tx, owner methodOwner) error {
	for _, plan := range owner.ownerPlans() {
		p, err := load(ctx, tx, plan)
		if err != nil {
			return err
		}
		if p.Retired || !domain.StandardWorkOpen(p.Progress) {
			continue
		}
		undispatched := true
		for _, progress := range p.Progress {
			v := progress.View()
			if v.Attempt != 0 || v.Stage != domain.Pending && v.Stage != domain.Prepared && v.Stage != domain.Cancelled {
				undispatched = false
			}
		}
		if !undispatched {
			continue
		}
		for _, progress := range p.Progress {
			v := progress.View()
			if v.Stage == domain.Cancelled {
				continue
			}
			if _, err = advanceInTransaction(ctx, tx, plan, v.Action, transition{Kind: "cancel"}); err != nil {
				return err
			}
		}
	}
	return nil
}

func cancelMethods(ctx context.Context, tx *sql.Tx, owner methodOwner) error {
	for _, plan := range owner.ownerPlans() {
		p, err := load(ctx, tx, plan)
		if err != nil {
			return err
		}
		for _, progress := range p.Progress {
			v := progress.View()
			// A completed intent-mode order stands until cancelled; cancelling
			// it releases the draft it holds (workerPlanHoldsDraft).
			if v.Stage == domain.Completed && !progress.Action().Kind().IntentMode() || v.Stage == domain.Unsuccessful || v.Stage == domain.Cancelled {
				continue
			}
			if _, err = advanceInTransaction(ctx, tx, plan, v.Action, transition{Kind: "cancel"}); err != nil {
				return err
			}
		}
	}
	return nil
}

func guardStandardWork(ctx context.Context, tx *sql.Tx, floors *retirementFloors, plan domain.PlanID, current domain.GenerationSnapshot, tick domain.Tick) error {
	var retired bool
	if err := tx.QueryRowContext(ctx, "SELECT retired FROM plans WHERE id=?", plan).Scan(&retired); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return ErrNotFound
		}
		return err
	}
	if retired {
		return errors.New("retired plan does not admit work")
	}
	if err := floors.guard(current, tick); err != nil {
		return err
	}
	var kind, ownerID string
	var epoch sql.NullString
	err := tx.QueryRowContext(ctx, "SELECT kind,owner_id,episode FROM plan_methods WHERE plan_id=?", plan).Scan(&kind, &ownerID, &epoch)
	if errors.Is(err, sql.ErrNoRows) {
		return nil
	}
	if err != nil {
		return err
	}
	switch kind {
	case "incident":
		return guardIncidentWork(ctx, tx, domain.IncidentID(ownerID), current, tick)
	case "project":
		return guardProjectWork(ctx, tx, domain.ProjectID(ownerID), current, tick)
	}
	state, err := loadStandard(ctx, tx, domain.ConcernID(ownerID))
	if err != nil {
		return err
	}
	g := state.Standard
	s := g.Snapshot
	if g.Status != domain.StandardOpen || g.Finding == domain.FindingUnclear || epoch.String != strconv.FormatUint(g.Episode, 10) ||
		s.Colony != current.Colony || s.Map != current.Map {
		return errors.New("maintained standard does not admit current work")
	}
	// A prepared plan does not prepare or dispatch while a Safeguard vetoes its
	// goal (#1017).
	return admitRoundsSafeguards(ctx, tx, state)
}
