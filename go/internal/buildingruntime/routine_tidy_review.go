package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// reviewTidy serves the TidyLayout review (#611, #809) on the projection:
// the rooms' furniture measured against their derived interior plans, with
// the tidies the timeline already recorded held out. The colony counts busy
// while any project definition or open building, haul or zone action
// stands, so the tidy never competes with real work. Runs only when the
// tidy method is served; otherwise the fact stays unknown and the goal is
// never assessed active.
func (r *Rounder) reviewTidy(ctx context.Context, snapshot domain.GenerationSnapshot, projection *observation.ColonyProjection, busy bool) error {
	projection.Facts.LayoutTidy = domain.Unknown[policy.TidyReview]()
	if !r.methodEnabled(policy.TidyLayout) {
		return nil
	}
	tick := projection.Identity.Tick
	request := policy.TidyRequest{Tier: projection.BuildTier}
	if tier, known := request.Tier.Value(); !known || tier < policy.BuildTierMasonry {
		projection.Facts.LayoutTidy = domain.Known(policy.PlanTidyLayout(request))
		return nil
	}
	tidies, err := r.player.journal.LayoutTidies(ctx, snapshot, tick)
	if err != nil {
		return err
	}
	for _, t := range tidies {
		request.Tidied = append(request.Tidied, t.Item)
		if t.Status == store.LayoutTidyMoving {
			request.InFlight = true
		}
	}
	request.Busy = domain.Known(busy)
	if rooms, rk := projection.Rooms.Value(); rk {
		if census, ck := projection.Facts.CurrentConstruction.Value(); ck && census.Colony {
			request.Rooms = policy.TidyFurnitureRooms(rooms, census, projection.Cells)
		}
	}
	review := policy.PlanTidyLayout(request)
	projection.Facts.LayoutTidy = domain.Known(review)
	if proposal := review.Proposal; proposal != nil {
		clockEvent(ctx, "layout", "tidy", "tidy proposal: "+proposal.Explanation, "item", proposal.Item.ID, "kind", string(proposal.Item.Kind), "gain", proposal.Gain)
	}
	return nil
}

// tidyBusy reports the open work that holds the tidy: a project definition
// still to build, or an open building, haul, zone or clearance action.
func tidyBusy(definitions []string, plans []store.PlanState, current domain.GenerationSnapshot, player map[domain.PlanID]uint64) bool {
	if len(definitions) > 0 {
		return true
	}
	busy := false
	routineOpenActions(plans, current, player, func(a domain.Action) {
		switch a.Kind() {
		case domain.BuildingAction, domain.HaulAction, domain.ZoneCreateAction, domain.ZoneDeleteAction, domain.WallRemovalAction, domain.ExcavationAction, domain.DeconstructionAction, domain.MoveBuildingAction, domain.UninstallBuildingAction:
			busy = true
		}
	})
	return busy
}
