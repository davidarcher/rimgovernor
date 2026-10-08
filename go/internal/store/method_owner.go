package store

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"strconv"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// ErrMethodBound is a commit refused because the owner already binds that method id.
var ErrMethodBound = errors.New("method already bound")

// methodOwner is what method admission reads of the goal or incident a
// methods row binds to (#1019): the world it was last reviewed in,
// whether the autopilot owns it, the routine need it serves and its open
// plans. StandardState, ProjectState and IncidentState implement it.
type methodOwner interface {
	ownerSnapshot() domain.GenerationSnapshot
	// ownerNeed is the routine need the owner serves under this review:
	// a goal's review binding, an incident's Kind.
	ownerNeed(Rounds) (domain.ConcernID, bool)
	ownerPriority() int
	ownerPlans() []domain.PlanID
	ownerLabel() string
	// boundPlan is the plan the owner binds method to in its current
	// episode (sql.ErrNoRows when it binds none), retired plans included.
	boundPlan(ctx context.Context, tx *sql.Tx, method domain.MethodID) (domain.PlanID, error)
	// bindMethod writes the owner's methods row and the plan_owner row that
	// keeps plan_id unique across the three owner tables.
	bindMethod(ctx context.Context, tx *sql.Tx, method domain.MethodID, plan domain.PlanID, reason string) error
	// servedCount counts the owner's methods in its current episode,
	// retired plans included.
	servedCount(ctx context.Context, tx *sql.Tx) (int, error)
}

func insertPlanOwner(ctx context.Context, tx *sql.Tx, plan domain.PlanID, kind string) error {
	_, err := tx.ExecContext(ctx, "INSERT INTO plan_owner(plan_id,kind) VALUES(?,?)", plan, kind)
	return err
}

func (g StandardState) boundPlan(ctx context.Context, tx *sql.Tx, method domain.MethodID) (plan domain.PlanID, err error) {
	err = tx.QueryRowContext(ctx, "SELECT plan_id FROM standard_methods WHERE standard_id=? AND episode=? AND method_id=?", g.Standard.ID, strconv.FormatUint(g.Standard.Episode, 10), method).Scan(&plan)
	return plan, err
}

func (g StandardState) bindMethod(ctx context.Context, tx *sql.Tx, method domain.MethodID, plan domain.PlanID, reason string) error {
	if err := insertPlanOwner(ctx, tx, plan, "standard"); err != nil {
		return err
	}
	_, err := tx.ExecContext(ctx, "INSERT INTO standard_methods(standard_id,episode,method_id,plan_id,priority,reason) VALUES(?,?,?,?,?,?)", g.Standard.ID, strconv.FormatUint(g.Standard.Episode, 10), method, plan, g.Standard.Priority, sql.NullString{String: reason, Valid: reason != ""})
	return err
}

func (g StandardState) servedCount(ctx context.Context, tx *sql.Tx) (n int, err error) {
	err = tx.QueryRowContext(ctx, "SELECT count(*) FROM standard_methods WHERE standard_id=? AND episode=?", g.Standard.ID, strconv.FormatUint(g.Standard.Episode, 10)).Scan(&n)
	return n, err
}

func (p ProjectState) boundPlan(ctx context.Context, tx *sql.Tx, method domain.MethodID) (plan domain.PlanID, err error) {
	err = tx.QueryRowContext(ctx, "SELECT plan_id FROM project_methods WHERE project_id=? AND method_id=?", p.Project.ID, method).Scan(&plan)
	return plan, err
}

func (p ProjectState) bindMethod(ctx context.Context, tx *sql.Tx, method domain.MethodID, plan domain.PlanID, reason string) error {
	if err := insertPlanOwner(ctx, tx, plan, "project"); err != nil {
		return err
	}
	_, err := tx.ExecContext(ctx, "INSERT INTO project_methods(project_id,method_id,plan_id,priority,reason) VALUES(?,?,?,?,?)", p.Project.ID, method, plan, p.Project.Priority, sql.NullString{String: reason, Valid: reason != ""})
	return err
}

func (p ProjectState) servedCount(ctx context.Context, tx *sql.Tx) (n int, err error) {
	err = tx.QueryRowContext(ctx, "SELECT count(*) FROM project_methods WHERE project_id=?", p.Project.ID).Scan(&n)
	return n, err
}

func (i IncidentState) boundPlan(ctx context.Context, tx *sql.Tx, method domain.MethodID) (plan domain.PlanID, err error) {
	err = tx.QueryRowContext(ctx, "SELECT plan_id FROM incident_methods WHERE incident_id=? AND method_id=?", i.Incident.ID, method).Scan(&plan)
	return plan, err
}

func (i IncidentState) bindMethod(ctx context.Context, tx *sql.Tx, method domain.MethodID, plan domain.PlanID, reason string) error {
	if err := insertPlanOwner(ctx, tx, plan, "incident"); err != nil {
		return err
	}
	_, err := tx.ExecContext(ctx, "INSERT INTO incident_methods(incident_id,method_id,plan_id,priority,reason) VALUES(?,?,?,?,?)", i.Incident.ID, method, plan, i.Incident.Priority, sql.NullString{String: reason, Valid: reason != ""})
	return err
}

func (i IncidentState) servedCount(ctx context.Context, tx *sql.Tx) (n int, err error) {
	err = tx.QueryRowContext(ctx, "SELECT count(*) FROM incident_methods WHERE incident_id=?", i.Incident.ID).Scan(&n)
	return n, err
}

func (g StandardState) ownerSnapshot() domain.GenerationSnapshot { return g.Standard.Snapshot }
func (g StandardState) ownerNeed(r Rounds) (domain.ConcernID, bool) {
	return r.Need(g.Standard.ID)
}
func (g StandardState) ownerPriority() int { return g.Standard.Priority }
func (g StandardState) ownerPlans() []domain.PlanID {
	out := make([]domain.PlanID, len(g.Methods))
	for i, m := range g.Methods {
		out[i] = m.Plan
	}
	return out
}
func (g StandardState) ownerLabel() string { return "standard " + string(g.Standard.ID) }

// admitRoundsSafeguards is method admission's backstop for the Safeguards the
// planner already asked: a vetoed proposal is ErrNotAdmitted with the
// Safeguard's reason. An owner the review does not bind (a player goal) is
// outside the routine Safeguards.
func admitRoundsSafeguards(ctx context.Context, tx *sql.Tx, owner methodOwner) error {
	review, err := loadRounds(ctx, tx)
	if err != nil {
		return err
	}
	need, bound := owner.ownerNeed(review)
	if !bound {
		return nil
	}
	proposal := policy.SafeguardProposal{Need: need, Priority: owner.ownerPriority()}
	if ref, vetoed := review.refuse(proposal); vetoed {
		return fmt.Errorf("%w: %s: %s: %s", ErrNotAdmitted, owner.ownerLabel(), ref.Safeguard, ref.Reason)
	}
	return nil
}

// admitOwnerMethod runs the per-family admission checks every owner shares
// and stores the plan; the caller then writes the methods row.
func admitOwnerMethod(ctx context.Context, tx *sql.Tx, owner methodOwner, plan domain.PlanSpec) error {
	for _, admit := range []func(context.Context, *sql.Tx, methodOwner, domain.PlanSpec) error{admitBillMethod, admitZoneMethod, admitAcquisitionMethod, admitWorkMethod, admitSupplyMethod} {
		if err := admit(ctx, tx, owner, plan); err != nil {
			return err
		}
	}
	return createPlan(ctx, tx, plan)
}

// bindOwnerMethod is the tail every owner's commit shares: refuse a method the
// owner already binds in its epoch, bound the owner's methods, run the family
// admission and store the plan, then write the methods row and name the
// plan's method.
func bindOwnerMethod(ctx context.Context, tx *sql.Tx, owner methodOwner, method domain.MethodID, reason string, plan domain.PlanSpec) error {
	// Plan ids are minted (#985); the real double-admission key is the
	// owner table's primary key (owner, [episode,] method).
	switch bound, err := owner.boundPlan(ctx, tx, method); {
	case err == nil:
		return fmt.Errorf("%w: %w: %s already binds method %s to plan %s", ErrConflict, ErrMethodBound, owner.ownerLabel(), method, bound)
	case !errors.Is(err, sql.ErrNoRows):
		return err
	}
	if err := admitOwnerMethod(ctx, tx, owner, plan); err != nil {
		return err
	}
	if err := owner.bindMethod(ctx, tx, method, plan.ID(), reason); err != nil {
		return conflict(err)
	}
	_, err := tx.ExecContext(ctx, "UPDATE plans SET method_id=? WHERE id=?", method, plan.ID())
	return err
}

func (g StandardState) bumpRevision(ctx context.Context, tx *sql.Tx) error {
	_, err := tx.ExecContext(ctx, "UPDATE standards SET revision=? WHERE id=?", strconv.FormatUint(g.Revision+1, 10), g.Standard.ID)
	return err
}

func (p ProjectState) bumpRevision(ctx context.Context, tx *sql.Tx) error {
	_, err := tx.ExecContext(ctx, "UPDATE projects SET revision=? WHERE id=?", strconv.FormatUint(p.Revision+1, 10), p.Project.ID)
	return err
}

func (r Rounds) refuseNeed(need domain.ConcernID, priority int) (policy.SafeguardRefusal, bool) {
	return r.refuse(policy.SafeguardProposal{Need: need, Priority: priority})
}

func (r Rounds) refuse(p policy.SafeguardProposal) (policy.SafeguardRefusal, bool) {
	return policy.RefuseProposal(policy.SafeguardContext{Enabled: r.Enabled, Unsafe: r.Unsafe}, p)
}

func (r Rounds) vetoNeed(need domain.ConcernID, priority int) string {
	ref, _ := r.refuseNeed(need, priority)
	return ref.Reason
}
