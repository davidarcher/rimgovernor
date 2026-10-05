package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
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
			// The shelter's research table moves into the laboratory before
			// the shelter can retire (#2047).
			if sleeping, sk := projection.Facts.Sleeping.Value(); sk {
				layout, haveLayout, err := r.layoutPlan(ctx, snapshot, tick)
				if err != nil {
					return err
				}
				tidied := map[string]bool{}
				for _, id := range request.Tidied {
					tidied[id] = true
				}
				if move, ok := policy.ShelterTableMove(layout.Plan, rooms, sleeping, census.Buildings, tidied); haveLayout && ok {
					request.Relocate = &move
				}
			}
		}
	}
	review := policy.PlanTidyLayout(request)
	projection.Facts.LayoutTidy = domain.Known(review)
	if proposal := review.Proposal; proposal != nil {
		tidyEdit(ctx, "proposed", proposal.Item.ID, map[string]any{"kind": string(proposal.Item.Kind), "gain": proposal.Gain})
	}
	return nil
}

// tidyEdit files one tidy outcome (proposed, admitted, abandoned, closed) as
// a layout_edit row about target, an item or a plan.
func tidyEdit(ctx context.Context, verdict, target string, attrs map[string]any) {
	attrs["family"] = "tidy"
	telemetry.Decide(ctx, telemetry.Decision{Kind: "layout_edit", Component: "layout", Verdict: verdict, Reason: "tidy", Target: target, Attrs: attrs})
}

// tidyBusy reports the open work that holds the tidy: a project definition
// still to build, or an open building, haul, zone or clearance action.
func tidyBusy(definitions []string, plans []store.PlanState, current domain.GenerationSnapshot, player map[domain.PlanID]uint64) bool {
	if len(definitions) > 0 {
		return true
	}
	busy := false
	roundsOpenActions(plans, current, player, func(a domain.Action) {
		switch a.Kind() {
		case domain.BuildingAction, domain.HaulAction, domain.ZoneCreateAction, domain.ZoneDeleteAction, domain.WallRemovalAction, domain.ExcavationAction, domain.DeconstructionAction, domain.MoveBuildingAction, domain.UninstallBuildingAction:
			busy = true
		}
	})
	return busy
}
