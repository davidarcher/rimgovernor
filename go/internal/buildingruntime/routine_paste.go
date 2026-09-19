package buildingruntime

import (
	"context"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func (r *RoutineBuildingPlanner) selectPaste(p observation.ColonyProjection) (*RoutineBuildingPlanner, RoutineBuildingReason) {
	seasonal := r.reviewer.seasonal(p.Facts)
	meals := p.MealRequest(seasonal.FoodMinDays, seasonal.FoodTargetDays)
	review, err := policy.ReviewMealTier(meals, p.ProductionBenches)
	if err != nil || review.Tier != policy.MealPaste {
		return r, ""
	}
	channels, known := p.FoodChannels.Value()
	if !known {
		return r, BuildingMethodUnknown
	}
	if len(channels.PasteDispenser) > 0 {
		return r, BuildingExistingFacility
	}
	request := policy.PasteSiteRequest{Meals: meals, Benches: p.ProductionBenches, Power: p.PowerPlanning}
	for _, d := range p.Definitions {
		if d.Name == "Hopper" {
			request.Hopper = domain.Known(policy.Infrastructure{Name: d.Name, Available: d.Available, Costs: d.Costs})
		}
		if d.Name == "NutrientPasteDispenser" {
			request.Size = d.Size
		}
	}
	site, ok := policy.PlanSiteType(policy.SiteTypeRequest{Paste: &request, Field: policy.FieldRequest{Site: policy.FarmSiteRequest{Cells: p.Cells, Bounds: p.Bounds, Anchor: p.Center}}})
	if !ok {
		return r, BuildingMethodNoSpace
	}
	result := *r
	result.paste = site.Buildings
	result.definition = "NutrientPasteDispenser"
	return &result, ""
}

func (r *RoutineBuildingPlanner) previewPaste(ctx context.Context, snapshot domain.GenerationSnapshot, p observation.ColonyProjection, protected []domain.Cell, check func() error) ([]policy.Preview, policy.StockObservation, RoutineBuildingReason, error) {
	stock := policy.StockObservation{Snapshot: snapshot, Tick: p.Identity.Tick}
	blocked := map[domain.Cell]bool{}
	for _, cell := range protected {
		blocked[cell] = true
	}
	var out []policy.Preview
	for i, site := range r.paste {
		building, err := domain.NewBuilding(site.Definition, site.Cell, site.Rotation, "")
		if err != nil {
			return nil, stock, "", err
		}
		action, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("%s-%d", snapshot.Plan, i)), building)
		if err != nil {
			return nil, stock, "", err
		}
		read, _, err := r.native.PreviewBuilding(ctx, action, snapshot)
		if err != nil {
			return nil, stock, "", err
		}
		if err = check(); err != nil {
			return nil, stock, "", err
		}
		v := read.Preview
		legal, lk := v.CanPlace.Value()
		safe, sk := v.SafeToPlace.Value()
		cells, ck := v.Footprint.Value()
		if v.Action != action || !v.Snapshot.Matches(snapshot) || !v.Tick.FreshFor(p.Identity.Tick) {
			return nil, stock, "", ErrControl
		}
		if !lk || !sk || !ck || !legal || !safe || len(cells) == 0 {
			return nil, stock, BuildingMethodRefused, nil
		}
		for _, cell := range cells {
			if blocked[cell] {
				return nil, stock, BuildingMethodRefused, nil
			}
			blocked[cell] = true
		}
		if err = mergeRoutineStock(&stock, read.Stock, true); err != nil {
			return nil, stock, "", err
		}
		out = append(out, v)
	}
	return out, stock, "", nil
}
