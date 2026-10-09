package buildingruntime

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"
	"log/slog"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// defenseRetierTier names the promotion's set-tier methods and results.
const defenseRetierTier policy.DefenseTierName = "retier"

// defensePromoted is the policy's promotion verdict over the round's raid
// points and defense capacity; unknown inputs never promote.
func defensePromoted(projection observation.ColonyProjection) bool {
	return policy.DefensePromoted(projection.Facts.RaidPoints, projection.Facts.DefenseCapacity)
}

// retier lifts the layout's already placed blueprints and frames to Survive
// when the colony is outmatched (#2527): one set-tier action per site, on the
// exact target. done is false when nothing needs lifting. A site that reads
// Survive on the next round is no longer selected, so the action is idempotent.
func (r *RoundsDefenseLayoutPlanner) retier(call, epoch context.Context, goal store.ProjectState, state ControlState, read observation.RoundsReading, record store.DefenseLayoutRecord) (RoundsDefenseLayoutResult, bool, error) {
	if !defensePromoted(read.Projection) {
		return RoundsDefenseLayoutResult{}, false, nil
	}
	census, known := read.Projection.Facts.CurrentConstruction.Value()
	if !known || !census.Colony {
		return RoundsDefenseLayoutResult{}, false, nil
	}
	var layout []domain.Building
	for _, tier := range record.Tiers {
		if _, buildings, ok := record.Tier(tier.Name); ok && !tier.Remove {
			layout = append(layout, buildings...)
		}
	}
	sites := policy.DefenseRetiers(census.Sites, layout)
	if len(sites) == 0 {
		return RoundsDefenseLayoutResult{}, false, nil
	}
	p := r.reviewer.player
	hash := sha256.New()
	for _, site := range sites {
		fmt.Fprintf(hash, "%s\n", site.ID)
	}
	method := domain.MethodID(fmt.Sprintf("defense-%s-%x", defenseRetierTier, hash.Sum(nil)[:8]))
	if _, err := p.journal.LoadOwnerMethod(call, goal, method); err == nil {
		return RoundsDefenseLayoutResult{}, false, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoundsDefenseLayoutResult{}, false, err
	}
	id := domain.MintPlanID()
	actions := make([]domain.Action, 0, len(sites))
	for i, site := range sites {
		action, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("%s-%d", id, i)), site.Building, domain.TierSurvive)
		if err != nil {
			return RoundsDefenseLayoutResult{}, false, err
		}
		if action, err = action.WithTier(domain.TierSurvive, site.ID); err != nil {
			return RoundsDefenseLayoutResult{}, false, err
		}
		actions = append(actions, action)
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoundsDefenseLayoutResult{}, false, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsDefenseLayoutResult{}, false, err
	}
	if p.session.State() != state {
		return RoundsDefenseLayoutResult{}, false, defenseControlErr(119)
	}
	if _, err = p.journal.CommitProjectMethod(call, goal.Project.ID, goal.Revision, method, "", plan); err != nil {
		return RoundsDefenseLayoutResult{}, false, err
	}
	defenseAction(call, "defense-layout", slog.LevelInfo, "applied", "retier", string(defenseRetierTier), map[string]any{"sites": len(sites), "method": string(method)})
	return RoundsDefenseLayoutResult{Verdict: BuildingReasonAdmitted, Plan: id, Tier: defenseRetierTier}, true, nil
}
