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

// ResourcePolicySubmissionRequest is explicit player intent to change one half
// of one resource's production policy: ModifyResourcePolicy's spending
// restriction, or SetResourceReserve's protected quantity. The two
// commands share one handler and one dispatch, so they share one submission
// here; Patch names which half was asked for and the other half keeps whatever
// the player established before.
//
// Unlike PopulationDecision and the two colony policies this DOES issue a
// native call, so it owns a row in the shared submissions table with a plan and
// action of its own, exactly as ZoneCreateSubmissionRequest does. And like zone
// creation it is player-command-driven rather than routine-planned: the
// submitted request commits its own one-action plan through createPlan directly
// and never passes through CommitGoalMethod, so the autopilot-goal-bound
// admission gates (goals.go's admitZoneMethod and friends) are bypassed rather
// than widened. The autopilot's own production-policy writer,
// RoutineProductionPolicyPlanner, commits through that gate;
// both paths resolve directives over controller defaults and share domain/bridge/executor
// dispatch for domain.ProductionPolicyAction, and the native
// SetProductionPolicy preview run at dispatch inspection remains the
// authoritative admission for either.
type ResourcePolicySubmissionRequest struct {
	RequestID string
	World     World
	Patch     domain.ResourcePolicyPatch
}

// ResourcePolicySubmission is one stored request, the named resource's merged
// directive that request produced, that resource's directive now, and the
// one-action plan the request committed.
type ResourcePolicySubmission struct {
	// Applied is the merged directive this request produced at the moment it
	// was accepted. It is recorded rather than recomputed so replaying an old
	// request ID reports what that request actually did, even after later
	// requests have moved the current value on.
	Applied domain.ResourceDirective
	// Current is the resource's directive now, which is Applied for a freshly
	// accepted request and a newer value when an older ID is replayed.
	Current  domain.ResourceDirective
	Request  ResourcePolicySubmissionRequest
	Plan     domain.PlanID
	Action   domain.ActionID
	Revision domain.PlanRevision
}

// resourcePolicyPatchWire is the stored JSON shape of one partial request.
// Absence must survive a round trip, so each half is a pointer and an unset
// half is omitted entirely, the same encoding expeditionPolicyPatchWire uses.
type resourcePolicyPatchWire struct {
	Resource string  `json:"resource"`
	Spending *string `json:"spending,omitempty"`
	Reserve  *int64  `json:"reserve,omitempty"`
}

func encodeResourcePolicyPatch(patch domain.ResourcePolicyPatch) ([]byte, error) {
	wire := resourcePolicyPatchWire{Resource: patch.Resource}
	if spending, ok := patch.Spending.Get(); ok {
		name := string(spending)
		wire.Spending = &name
	}
	wire.Reserve = optionalWire(patch.Reserve)
	return json.Marshal(wire)
}

func decodeResourcePolicyPatch(payload []byte) (domain.ResourcePolicyPatch, error) {
	if len(payload) > 4096 {
		return domain.ResourcePolicyPatch{}, errors.New("resource policy request exceeds bound")
	}
	var wire resourcePolicyPatchWire
	if err := json.Unmarshal(payload, &wire); err != nil {
		return domain.ResourcePolicyPatch{}, err
	}
	canonical, err := json.Marshal(wire)
	if err != nil {
		return domain.ResourcePolicyPatch{}, err
	}
	if !bytes.Equal(canonical, payload) {
		return domain.ResourcePolicyPatch{}, errors.New("noncanonical resource policy request")
	}
	patch := domain.ResourcePolicyPatch{Resource: wire.Resource, Reserve: optionalDomain(wire.Reserve)}
	if wire.Spending != nil {
		patch.Spending = domain.Some(domain.ResourceSpending(*wire.Spending))
	}
	return patch, patch.Validate()
}

func (q ResourcePolicySubmissionRequest) validate() error {
	if err := submissionID(q.RequestID); err != nil {
		return err
	}
	if err := q.World.Validate(); err != nil {
		return err
	}
	return q.Patch.Validate()
}

// resourcePolicyDispatch folds the world's directives, with the accepted one
// substituted in, into the single whole-state replacement one native
// SetProductionPolicy write carries. The native operation replaces the whole
// map-scoped production policy in one call, so the full merged set is what must
// be dispatched even though the player only changed one resource -- exactly the
// full-replacement shape ZoneCreate's cells already have.
func resourcePolicyDispatch(current []domain.ResourceDirective, applied domain.ResourceDirective, defaults domain.ProductionPolicy) (domain.ProductionPolicy, error) {
	merged := make([]domain.ResourceDirective, 0, len(current)+1)
	replaced := false
	for _, d := range current {
		if d.Resource() == applied.Resource() {
			merged, replaced = append(merged, applied), true
			continue
		}
		merged = append(merged, d)
	}
	if !replaced {
		merged = append(merged, applied)
	}
	return domain.ResolveProductionPolicy(defaults, merged)
}

// SubmitResourcePolicy atomically records one explicit player request, merges
// it over the resource's directive in force, and commits the one-action plan
// that pushes the world's whole merged policy to native. Replay is decided by
// request ID exactly as every other submission decides it: the same ID with the
// same requested fields returns the stored request and reports no creation, and
// with different fields returns ErrConflict. Submission neither acquires
// authority nor issues the native SetProductionPolicy command; a worker later
// admits and dispatches the committed action through the same unchanged
// executor/bridge path the autopilot's own production-policy writer uses.
func (s *Store) SubmitResourcePolicy(ctx context.Context, q ResourcePolicySubmissionRequest) (ResourcePolicySubmission, bool, error) {
	return s.SubmitResourcePolicyWithDefaults(ctx, q, domain.ProductionPolicy{})
}

// SubmitResourcePolicyWithDefaults uses the same precedence as routine
// reconciliation, retaining configuration for resources without directives.
func (s *Store) SubmitResourcePolicyWithDefaults(ctx context.Context, q ResourcePolicySubmissionRequest, defaults domain.ProductionPolicy) (ResourcePolicySubmission, bool, error) {
	if err := q.validate(); err != nil {
		return ResourcePolicySubmission{}, false, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return ResourcePolicySubmission{}, false, err
	}
	defer tx.Rollback()
	old, err := lookupResourcePolicySubmission(ctx, tx, q.RequestID)
	if err == nil {
		if old.Request != q {
			return ResourcePolicySubmission{}, false, ErrConflict
		}
		if err = tx.Commit(); err != nil {
			return ResourcePolicySubmission{}, false, err
		}
		return old, false, nil
	}
	if !errors.Is(err, ErrNotFound) {
		return ResourcePolicySubmission{}, false, err
	}
	directives, err := resourcePolicies(ctx, tx, q.World)
	if err != nil {
		return ResourcePolicySubmission{}, false, err
	}
	var base domain.ResourceDirective
	for _, d := range directives {
		if d.Resource() == q.Patch.Resource {
			base = d
			break
		}
	}
	applied, err := q.Patch.Apply(base)
	if err != nil {
		return ResourcePolicySubmission{}, false, err
	}
	value, err := resourcePolicyDispatch(directives, applied, defaults)
	if err != nil {
		return ResourcePolicySubmission{}, false, err
	}
	var entropy [32]byte
	if _, err = rand.Read(entropy[:]); err != nil {
		return ResourcePolicySubmission{}, false, err
	}
	result := ResourcePolicySubmission{
		Applied:  applied,
		Current:  applied,
		Request:  q,
		Plan:     domain.PlanID("resource-policy-" + hex.EncodeToString(entropy[:16])),
		Action:   domain.ActionID("resource-policy-action-" + hex.EncodeToString(entropy[16:])),
		Revision: 1,
	}
	action, err := domain.NewProductionPolicyAction(result.Action, value)
	if err != nil {
		return ResourcePolicySubmission{}, false, err
	}
	plan, err := domain.NewPlan(result.Plan, 1, []domain.Action{action})
	if err != nil {
		return ResourcePolicySubmission{}, false, err
	}
	if err = createPlan(ctx, tx, plan); err != nil {
		return ResourcePolicySubmission{}, false, err
	}
	if err = insertSubmissionHeader(ctx, tx, q.RequestID, "resource_policy", q.World, result.Plan, result.Action); err != nil {
		return ResourcePolicySubmission{}, false, err
	}
	payload, err := encodeResourcePolicyPatch(q.Patch)
	if err != nil {
		return ResourcePolicySubmission{}, false, err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO resource_policy_submissions(request_id,patch,resource,reserve,spending) VALUES(?,?,?,?,?)",
		q.RequestID, payload, applied.Resource(), applied.Reserve(), string(applied.Spending())); err != nil {
		return ResourcePolicySubmission{}, false, conflict(err)
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO resource_policies(colony,load_token,map_id,resource,request_id,reserve,spending) VALUES(?,?,?,?,?,?,?) ON CONFLICT(colony,load_token,map_id,resource) DO UPDATE SET request_id=excluded.request_id,reserve=excluded.reserve,spending=excluded.spending",
		q.World.Colony, q.World.Load, q.World.Map, applied.Resource(), q.RequestID, applied.Reserve(), string(applied.Spending())); err != nil {
		return ResourcePolicySubmission{}, false, conflict(err)
	}
	if err = tx.Commit(); err != nil {
		return ResourcePolicySubmission{}, false, err
	}
	return result, true, nil
}

// LookupResourcePolicySubmission returns one stored request by request ID.
func (s *Store) LookupResourcePolicySubmission(ctx context.Context, requestID string) (ResourcePolicySubmission, error) {
	if err := submissionID(requestID); err != nil {
		return ResourcePolicySubmission{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return ResourcePolicySubmission{}, err
	}
	defer tx.Rollback()
	result, err := lookupResourcePolicySubmission(ctx, tx, requestID)
	if err != nil {
		return ResourcePolicySubmission{}, err
	}
	if err = tx.Commit(); err != nil {
		return ResourcePolicySubmission{}, err
	}
	return result, nil
}

// ResourcePolicies returns every recorded per-resource directive for one world,
// ordered by resource. An empty result is not an error: a world whose player
// has declared no reserves or restrictions simply has none.
func (s *Store) ResourcePolicies(ctx context.Context, w World) ([]domain.ResourceDirective, error) {
	if err := w.Validate(); err != nil {
		return nil, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return nil, err
	}
	defer tx.Rollback()
	out, err := resourcePolicies(ctx, tx, w)
	if err != nil {
		return nil, err
	}
	if err = tx.Commit(); err != nil {
		return nil, err
	}
	return out, nil
}

// CurrentResourcePolicy returns one resource's current directive, or
// ErrNotFound when the player has never named that resource in that world.
func (s *Store) CurrentResourcePolicy(ctx context.Context, w World, resource string) (domain.ResourceDirective, error) {
	if err := w.Validate(); err != nil {
		return domain.ResourceDirective{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return domain.ResourceDirective{}, err
	}
	defer tx.Rollback()
	directive, err := currentResourcePolicy(ctx, tx, w, resource)
	if err != nil {
		return domain.ResourceDirective{}, err
	}
	if err = tx.Commit(); err != nil {
		return domain.ResourceDirective{}, err
	}
	return directive, nil
}

func currentResourcePolicy(ctx context.Context, tx *sql.Tx, w World, resource string) (domain.ResourceDirective, error) {
	var reserve int64
	var spending string
	err := tx.QueryRowContext(ctx, "SELECT reserve,spending FROM resource_policies WHERE colony=? AND load_token=? AND map_id=? AND resource=?", w.Colony, w.Load, w.Map, resource).Scan(&reserve, &spending)
	if errors.Is(err, sql.ErrNoRows) {
		return domain.ResourceDirective{}, ErrNotFound
	}
	if err != nil {
		return domain.ResourceDirective{}, err
	}
	return domain.NewResourceDirective(resource, reserve, domain.ResourceSpending(spending))
}

func resourcePolicies(ctx context.Context, tx *sql.Tx, w World) ([]domain.ResourceDirective, error) {
	rows, err := tx.QueryContext(ctx, "SELECT resource,reserve,spending FROM resource_policies WHERE colony=? AND load_token=? AND map_id=? ORDER BY resource", w.Colony, w.Load, w.Map)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []domain.ResourceDirective
	for rows.Next() {
		var resource, spending string
		var reserve int64
		if err = rows.Scan(&resource, &reserve, &spending); err != nil {
			return nil, err
		}
		directive, err := domain.NewResourceDirective(resource, reserve, domain.ResourceSpending(spending))
		if err != nil {
			return nil, err
		}
		out = append(out, directive)
	}
	if err = rows.Err(); err != nil {
		return nil, err
	}
	if err = rows.Close(); err != nil {
		return nil, err
	}
	return out, nil
}

func lookupResourcePolicySubmission(ctx context.Context, tx *sql.Tx, id string) (ResourcePolicySubmission, error) {
	h, err := lookupSubmissionHeader(ctx, tx, id, "resource_policy")
	if err != nil {
		return ResourcePolicySubmission{}, err
	}
	var payload []byte
	var resource, spending string
	var reserve int64
	err = tx.QueryRowContext(ctx, "SELECT patch,resource,reserve,spending FROM resource_policy_submissions WHERE request_id=?", id).Scan(&payload, &resource, &reserve, &spending)
	if errors.Is(err, sql.ErrNoRows) {
		return ResourcePolicySubmission{}, ErrNotFound
	}
	if err != nil {
		return ResourcePolicySubmission{}, err
	}
	applied, err := domain.NewResourceDirective(resource, reserve, domain.ResourceSpending(spending))
	if err != nil {
		return ResourcePolicySubmission{}, err
	}
	patch, err := decodeResourcePolicyPatch(payload)
	if err != nil {
		return ResourcePolicySubmission{}, err
	}
	if patch.Resource != applied.Resource() {
		return ResourcePolicySubmission{}, errors.New("resource policy submission names two resources")
	}
	result := ResourcePolicySubmission{
		Applied:  applied,
		Request:  ResourcePolicySubmissionRequest{RequestID: id, World: h.World, Patch: patch},
		Plan:     h.Plan,
		Action:   h.Action,
		Revision: h.Revision,
	}
	if err = result.Request.validate(); err != nil {
		return ResourcePolicySubmission{}, err
	}
	if result.Current, err = currentResourcePolicy(ctx, tx, h.World, applied.Resource()); err != nil {
		return ResourcePolicySubmission{}, err
	}
	state, err := load(ctx, tx, result.Plan)
	if err != nil {
		return ResourcePolicySubmission{}, err
	}
	actions := state.Spec.Actions()
	if state.Spec.Revision() != 1 || len(actions) != 1 || actions[0].ID() != result.Action {
		return ResourcePolicySubmission{}, errors.New("resource policy submission plan is corrupt")
	}
	if _, ok := actions[0].ProductionPolicy(); !ok {
		return ResourcePolicySubmission{}, errors.New("resource policy submission differs from intent")
	}
	return result, nil
}
