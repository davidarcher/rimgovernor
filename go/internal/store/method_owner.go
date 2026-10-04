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
	// ownerKey is the methods owner column and id, and the epoch the
	// row is keyed under ("0" for owners without epochs).
	ownerKey() (column, id, epoch string)
}

func (g StandardState) ownerKey() (string, string, string) {
	return "standard_id", string(g.Standard.ID), strconv.FormatUint(g.Standard.Episode, 10)
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
func (g StandardState) ownerLabel() string { return "goal " + string(g.Standard.ID) }

// admitRoutineSafeguards is method admission's backstop for the Safeguards the
// planner already asked: a vetoed proposal is ErrNotAdmitted with the
// Safeguard's reason. An owner the review does not bind (a player goal) is
// outside the routine Safeguards.
func admitRoutineSafeguards(ctx context.Context, tx *sql.Tx, owner methodOwner) error {
	review, err := loadRoutine(ctx, tx)
	if err != nil {
		return err
	}
	need, bound := owner.ownerNeed(review)
	if !bound {
		return nil
	}
	if ref, vetoed := review.refuseNeed(need, owner.ownerPriority()); vetoed {
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
	column, id, epoch := owner.ownerKey()
	// Plan ids are minted (#985); the real double-admission key is the
	// methods unique index (owner, epoch, method).
	var bound string
	switch err := tx.QueryRowContext(ctx, "SELECT plan_id FROM methods WHERE "+column+"=? AND episode=? AND method_id=?", id, epoch, method).Scan(&bound); {
	case err == nil:
		return fmt.Errorf("%w: %s already binds method %s to plan %s", ErrConflict, owner.ownerLabel(), method, bound)
	case !errors.Is(err, sql.ErrNoRows):
		return err
	}
	if len(owner.ownerPlans()) >= 256 {
		return ErrCapacity
	}
	if err := admitOwnerMethod(ctx, tx, owner, plan); err != nil {
		return err
	}
	if _, err := tx.ExecContext(ctx, "INSERT INTO methods("+column+",episode,method_id,plan_id,priority,reason) VALUES(?,?,?,?,?,?)", id, epoch, method, plan.ID(), owner.ownerPriority(), sql.NullString{String: reason, Valid: reason != ""}); err != nil {
		return conflict(err)
	}
	_, err := tx.ExecContext(ctx, "UPDATE plans SET method_id=? WHERE id=?", method, plan.ID())
	return err
}

func (r Rounds) refuseNeed(need domain.ConcernID, priority int) (policy.SafeguardRefusal, bool) {
	return policy.RefuseProposal(policy.SafeguardContext{Enabled: r.Enabled, Emergency: r.Emergency, Unsafe: r.Unsafe}, policy.SafeguardProposal{Need: need, Priority: priority})
}

func (r Rounds) vetoNeed(need domain.ConcernID, priority int) string {
	ref, _ := r.refuseNeed(need, priority)
	return ref.Reason
}
