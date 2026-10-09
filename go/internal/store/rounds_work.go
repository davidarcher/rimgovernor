package store

import (
	"context"
	"crypto/sha256"
	"database/sql"
	"errors"
	"fmt"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// roundsPlan is one current-world, non-retired plan and the need (or
// player project) it serves.
type roundsPlan struct {
	state   PlanState
	concern domain.ConcernID
}

// roundsPlans maps every non-retired plan of the current world to its
// goal: routine plans to their bound need, player submissions to a
// per-plan project id. Plans of another world, and plans with neither a
// method nor a submission, are skipped.
func roundsPlans(ctx context.Context, tx *sql.Tx, current domain.GenerationSnapshot, bindings []RoundsStandard) ([]roundsPlan, error) {
	plans, err := loadPlans(ctx, tx)
	if err != nil {
		return nil, err
	}
	var result []roundsPlan
	for _, plan := range plans {
		var concernID domain.ConcernID
		world := World{}
		var kind, ownerID string
		err = tx.QueryRowContext(ctx, "SELECT kind,owner_id FROM plan_methods WHERE plan_id=?", plan.Spec.ID()).Scan(&kind, &ownerID)
		concernID = domain.ConcernID(ownerID)
		if err == nil && kind == "project" {
			// A Project's method serves its kind, which is its need.
			p, e := loadProject(ctx, tx, domain.ProjectID(ownerID))
			if e != nil {
				return nil, e
			}
			concernID = p.Project.Kind
			world = World{Colony: p.Project.Snapshot.Colony, Load: p.Project.Snapshot.Load, Map: p.Project.Snapshot.Map}
		} else if err == nil && kind == "incident" {
			// An incident's method serves its Response kind.
			i, e := loadIncident(ctx, tx, domain.IncidentID(ownerID))
			if e != nil {
				return nil, e
			}
			concernID = i.Incident.Kind
			world = World{Colony: i.Incident.Snapshot.Colony, Load: i.Incident.Snapshot.Load, Map: i.Incident.Snapshot.Map}
		} else if err == nil {
			g, e := loadStandard(ctx, tx, concernID)
			if e != nil {
				return nil, e
			}
			world = World{Colony: g.Standard.Snapshot.Colony, Load: g.Standard.Snapshot.Load, Map: g.Standard.Snapshot.Map}
			for _, b := range bindings {
				if concernID == b.Standard || roundsStandardOwns(concernID, b.Concern) {
					concernID = b.Concern
					break
				}
			}
		} else if errors.Is(err, sql.ErrNoRows) {
			err = tx.QueryRowContext(ctx, "SELECT colony,load_token,map_id FROM submissions WHERE plan_id=?", plan.Spec.ID()).Scan(&world.Colony, &world.Load, &world.Map)
			if errors.Is(err, sql.ErrNoRows) {
				continue
			}
			if err != nil {
				return nil, err
			}
			concernID = domain.ConcernID(fmt.Sprintf("player-project-%x", sha256.Sum256([]byte(plan.Spec.ID()))))
		} else {
			return nil, err
		}
		if world != (World{Colony: current.Colony, Load: current.Load, Map: current.Map}) {
			continue
		}
		result = append(result, roundsPlan{plan, concernID})
	}
	return result, nil
}

// readyWorkOf is the shadow ready-work projection of the review's
// plans: diagnostics only, read by no admission.
// Stage inputs (bill ingredients, crop readiness) are not observed here
// yet, so staged work reads awaiting_observation rather than ready.
func readyWorkOf(r RoundsRequest, plans []roundsPlan, unserved []domain.ConcernID) policy.ReadyWorkReport {
	var ready []policy.ReadyPlan
	for _, p := range plans {
		ready = append(ready, policy.ReadyPlan{Concern: p.concern, Spec: p.state.Spec, Progress: p.state.Progress})
	}
	return policy.ProjectReadyWork(policy.ReadyRequest{Snapshot: r.Current, Tick: r.Tick, Plans: ready, Unserved: unserved, Construction: r.Facts.CurrentConstruction, Recipes: r.Facts.Recipes})
}

// admitRoundsReview refuses work from a stale or disabled review, independently
// of pawn availability. Safety is enforced by the shared Safeguards.
func admitRoundsReview(ctx context.Context, tx *sql.Tx, g OwnerSummary) error {
	if !(strings.HasPrefix(g.ID, "routine-") || isProjectID(g.ID)) {
		return nil
	}
	review, err := loadRounds(ctx, tx)
	if err != nil {
		return err
	}
	bound := false
	for _, b := range review.Standards {
		bound = bound || string(b.Standard) == g.ID
	}
	for _, b := range review.Projects {
		bound = bound || string(b.Project) == g.ID
	}
	if !bound {
		return nil
	}
	if !review.Enabled || review.Snapshot != g.Snapshot {
		return fmt.Errorf("%w: standard %s has no current enabled review", ErrNotAdmitted, g.ID)
	}
	return nil
}

func reviewWork(ctx context.Context, tx *sql.Tx, r RoundsRequest, needs policy.RoundsFindings, states []WorkOwner, records []DependencyRecord) (policy.ReadyWorkReport, []DependencyRecord, error) {
	var bindings []RoundsStandard
	var unserved []domain.ConcernID
	for i, n := range needs.Assessments {
		bindings = append(bindings, RoundsStandard{Concern: n.ID, Standard: domain.ConcernID(states[i].OwnerID())})
		if !ownerActive(states[i]) || len(policy.ConcernLabor(n.ID)) == 0 {
			continue
		}
		served, err := states[i].servedCount(ctx, tx)
		if err != nil {
			return policy.ReadyWorkReport{}, nil, err
		}
		if served == 0 {
			unserved = append(unserved, n.ID)
		}
	}
	plans, err := roundsPlans(ctx, tx, r.Current, bindings)
	if err != nil {
		return policy.ReadyWorkReport{}, nil, err
	}
	kept, _, err := roundsDependencies(ctx, tx, records, bindings, states, r.Tick)
	if err != nil {
		return policy.ReadyWorkReport{}, nil, err
	}
	return readyWorkOf(r, plans, unserved), kept, nil
}
