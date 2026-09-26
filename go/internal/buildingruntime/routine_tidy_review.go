package buildingruntime

import (
	"context"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// reviewTidy serves the TidyLayout review (#611) on the projection: the
// managed zones this colony created (zone claims still listed by the zone
// census) and the Camp shells it built (rooms ringed by claimed walls)
// measured against the colony grid, with the tidies the timeline already
// recorded held out. The colony counts busy while any project definition
// or open building, haul or zone action stands, and while the zone census
// or the claims are unknown, so the tidy never competes with real work.
// Runs only when the tidy method is served; otherwise the fact stays
// unknown and the goal is never assessed active.
func (r *RoutineReviewer) reviewTidy(ctx context.Context, snapshot domain.GenerationSnapshot, projection *observation.ColonyProjection, busy bool) error {
	projection.Facts.LayoutTidy = domain.Unknown[policy.TidyReview]()
	if !r.methodEnabled(policy.TidyLayout) {
		return nil
	}
	tick := projection.Identity.Tick
	request := policy.TidyRequest{Tier: projection.BuildTier, Grid: projection.ColonyGrid, Bounds: projection.Bounds, Cells: projection.Cells, Protected: layoutProtected(*projection, nil), Extent: domain.Unknown[policy.ColonyExtent]()}
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
	history, err := r.player.journal.EstablishedColonyExtent(ctx, snapshot, tick)
	if err != nil {
		return err
	}
	if len(history) > 0 {
		extent := policy.ColonyExtent{}
		for _, row := range history {
			extent.Regions = append(extent.Regions, row.Region)
		}
		request.Extent = domain.Known(extent)
	}
	claims, err := r.player.journal.ZoneClaims(ctx, snapshot, tick)
	if err != nil {
		return err
	}
	owned, ok := claims.Value()
	if !ok || !projection.Zones.Complete {
		busy = true
	}
	request.Busy = domain.Known(busy)
	request.Items = append(request.Items, tidyZoneItems(owned, projection)...)
	request.Items = append(request.Items, tidyShellItems(projection)...)
	review := policy.PlanTidyLayout(request)
	projection.Facts.LayoutTidy = domain.Known(review)
	if proposal := review.Proposal; proposal != nil {
		clockEvent(ctx, "layout", "tidy", "tidy proposal: "+proposal.Explanation, "item", proposal.Item.ID, "kind", string(proposal.Item.Kind), "gain", proposal.Gain)
	}
	return nil
}

// tidyZoneItems measures the managed zones the census still lists: the
// footprint is the census bounding box, the cell count the claim's cells
// (a growing zone's usable cells when the census serves them).
func tidyZoneItems(owned []store.OwnedZone, projection *observation.ColonyProjection) []policy.TidyItem {
	if !projection.Zones.Complete {
		return nil
	}
	claims := map[string]store.OwnedZone{}
	for _, z := range owned {
		claims[z.ID] = z
	}
	var out []policy.TidyItem
	for _, row := range projection.Zones.Value.Rows {
		claim, managed := claims[row.GetId()]
		if !managed || row.GetBounds() == nil {
			continue
		}
		item := policy.TidyItem{ID: row.GetId(), Managed: true, Cells: len(claim.Cells), Crop: claim.Crop}
		lo, hi := row.GetBounds().GetMinimum(), row.GetBounds().GetMaximum()
		item.Footprint = policy.Rectangle{X: lo.GetX(), Z: lo.GetZ(), Width: hi.GetX() - lo.GetX() + 1, Height: hi.GetZ() - lo.GetZ() + 1}
		switch {
		case row.GetType() == "growing" && claim.Kind == domain.GrowingZone:
			item.Kind = policy.TidyField
			if farm := row.GetFarm(); farm != nil {
				if farm.GetCrop() != "" {
					item.Crop = farm.GetCrop()
				}
				if farm.GetUsableCells() > 0 {
					item.Cells = int(farm.GetUsableCells())
				}
			}
		case row.GetType() == "stockpile" && claim.Kind == domain.StockpileZone:
			item.Kind = policy.TidyStockpile
		default:
			continue
		}
		out = append(out, item)
	}
	sort.Slice(out, func(i, j int) bool { return out[i].ID < out[j].ID })
	return out
}

// tidyShellItems measures the Camp shells this controller built: an
// enclosed room whose every ring cell (the interior's outer neighbours)
// stands on a construction claim. InUse marks beds or contents; Replaced
// marks another enclosed, unused room of the same role whose exterior
// sits on the grid.
func tidyShellItems(projection *observation.ColonyProjection) []policy.TidyItem {
	rooms, rk := projection.Rooms.Value()
	claims, ck := projection.Facts.ConstructionClaims.Value()
	grid, gk := projection.ColonyGrid.Value()
	if !rk || !ck || !gk {
		return nil
	}
	claimed := map[domain.Cell]bool{}
	for _, c := range claims {
		for _, cell := range c.Cells {
			claimed[cell] = true
		}
	}
	type shell struct {
		item policy.TidyItem
		role policy.RoomRole
	}
	var shells []shell
	for _, room := range rooms.Rooms {
		role, known := room.Role.Value()
		enclosed, ek := room.Enclosed.Value()
		if len(room.Cells) == 0 || !known || !ek || !enclosed {
			continue
		}
		interior := map[domain.Cell]bool{}
		lo, hi := room.Cells[0], room.Cells[0]
		for _, c := range room.Cells {
			interior[c] = true
			lo.X, hi.X = min(lo.X, c.X), max(hi.X, c.X)
			lo.Z, hi.Z = min(lo.Z, c.Z), max(hi.Z, c.Z)
		}
		managed := true
		for _, c := range room.Cells {
			for _, n := range []domain.Cell{{X: c.X + 1, Z: c.Z}, {X: c.X - 1, Z: c.Z}, {X: c.X, Z: c.Z + 1}, {X: c.X, Z: c.Z - 1}} {
				if !interior[n] && !claimed[n] {
					managed = false
				}
			}
		}
		contents, _ := room.Contents.Value()
		item := policy.TidyItem{Kind: policy.TidyShell, ID: room.ID, Managed: managed, InUse: len(room.Beds) > 0 || len(contents) > 0, Cells: len(room.Cells), Footprint: policy.Rectangle{X: lo.X - 1, Z: lo.Z - 1, Width: hi.X - lo.X + 3, Height: hi.Z - lo.Z + 3}}
		shells = append(shells, shell{item, role})
	}
	var out []policy.TidyItem
	for i, s := range shells {
		if !s.item.Managed {
			continue
		}
		for j, other := range shells {
			if i != j && other.role == s.role && !other.item.InUse && grid.CornerError(other.item.Footprint) == 0 {
				s.item.Replaced = true
			}
		}
		out = append(out, s.item)
	}
	sort.Slice(out, func(i, j int) bool { return out[i].ID < out[j].ID })
	return out
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
		case domain.BuildingAction, domain.HaulAction, domain.ZoneCreateAction, domain.ZoneDeleteAction, domain.WallRemovalAction, domain.ExcavationAction, domain.DeconstructionAction:
			busy = true
		}
	})
	return busy
}
