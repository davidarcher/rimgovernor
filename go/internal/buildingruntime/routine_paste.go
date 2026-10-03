package buildingruntime

import (
	"context"
	"fmt"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func (r *RoutineBuildingPlanner) selectPaste(p observation.ColonyProjection) (*RoutineBuildingPlanner, Verdict) {
	seasonal := r.reviewer.seasonal(p.Facts)
	meals := p.MealRequest(seasonal.FoodMinDays, seasonal.FoodTargetDays)
	review, err := policy.ReviewMealTier(meals, p.ProductionBenches)
	if err != nil || review.Tier != policy.MealPaste {
		return r, Verdict{}
	}
	channels, known := p.FoodChannels.Value()
	if !known {
		return r, fieldUnavailable("food_channels")
	}
	if len(channels.PasteDispenser) > 0 {
		return r, BuildingExistingFacility
	}
	request := policy.PasteSiteRequest{Meals: meals, Benches: p.ProductionBenches, Power: p.PowerPlanning}
	for _, d := range p.Definitions {
		if d.Name == "Hopper" {
			request.Hopper = domain.Known(policy.Infrastructure{Name: d.Name, Available: d.Available, Costs: d.CheapestCosts()})
		}
		if d.Name == "NutrientPasteDispenser" {
			request.Size = d.Size
		}
	}
	site, ok := policy.PlanSiteType(policy.SiteTypeRequest{Paste: &request, Field: policy.FieldRequest{Site: policy.FarmSiteRequest{Cells: p.Cells, Bounds: p.Bounds, Anchor: p.Center}}})
	if !ok {
		return r, noSpace("paste_dispenser_site")
	}
	result := *r
	result.paste = site.Buildings
	result.definition = "NutrientPasteDispenser"
	return &result, Verdict{}
}

func (r *RoutineBuildingPlanner) previewPaste(ctx context.Context, snapshot domain.GenerationSnapshot, p observation.ColonyProjection, protected []domain.Cell, check func() error) ([]policy.Preview, policy.StockObservation, Verdict, error) {
	stock := policy.StockObservation{Snapshot: snapshot, Tick: p.Identity.Tick}
	blocked := map[domain.Cell]bool{}
	for _, cell := range protected {
		blocked[cell] = true
	}
	var out []policy.Preview
	for i, site := range r.paste {
		building, err := domain.NewBuilding(site.Definition, site.Cell, site.Rotation, "")
		if err != nil {
			return nil, stock, Verdict{}, err
		}
		action, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("%s-%d", snapshot.Plan, i)), building)
		if err != nil {
			return nil, stock, Verdict{}, err
		}
		read, _, err := r.native.PreviewBuilding(ctx, action, snapshot)
		if err != nil {
			return nil, stock, Verdict{}, err
		}
		v := read.Preview
		legal, lk := v.CanPlace.Value()
		safe, sk := v.SafeToPlace.Value()
		cells, ck := v.Footprint.Value()
		if !lk || !sk || !ck || !legal || !safe || len(cells) == 0 {
			return nil, stock, BuildingReasonRefused, nil
		}
		for _, cell := range cells {
			if blocked[cell] {
				return nil, stock, BuildingReasonRefused, nil
			}
			blocked[cell] = true
		}
		if err = mergeRoutineStock(&stock, read.Stock, true); err != nil {
			return nil, stock, Verdict{}, err
		}
		out = append(out, v)
	}
	return out, stock, Verdict{}, nil
}

// digPaste mines the rock under the chosen dispenser or hopper cell ahead of
// them: the paste site (policy.planPasteSite) lets those two cells stand on
// natural rock or fogged mountain over an otherwise open apron, and the
// shared rock step (admitRockStep) admits the digs and both buildings as one
// method previewed over rock. Not handled when both cells are open ground.
func (r *RoutineBuildingPlanner) digPaste(call, epoch context.Context, s excavationStep, protected []domain.Cell, check func() error) (RoutineBuildingResult, bool, error) {
	var planned []policy.RoleCell
	var buildings []domain.Building
	for _, site := range r.paste {
		if slices.Contains(protected, site.Cell) {
			return RoutineBuildingResult{Verdict: BuildingReasonExistingWork}, true, nil
		}
		building, err := domain.NewBuilding(site.Definition, site.Cell, site.Rotation, "")
		if err != nil {
			return RoutineBuildingResult{}, false, err
		}
		planned = append(planned, policy.RoleCell{Cell: site.Cell, Role: policy.RockNeedsFloor})
		buildings = append(buildings, building)
	}
	dig := policy.RockStep(planned, s.facts.Cells).Dig
	if len(dig) == 0 {
		return RoutineBuildingResult{}, false, nil
	}
	access, ok := policy.RockAccess(dig, s.facts.Cells)
	if !ok {
		return RoutineBuildingResult{Verdict: rockNotDug(r.definition, "no_open_cell_beside_footprint")}, true, nil
	}
	method := domain.MethodID(fmt.Sprintf("plan-dig-paste-%d-%d", dig[0].X, dig[0].Z))
	return r.admitRockStep(call, epoch, s, planned, access, method, buildings, check)
}
