package buildingruntime

import (
	"context"
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// Non-food field candidates: for each MaintainResource deficit the
// supply plan prices a new field of the crop policy.PlanFieldByResource picks,
// beside the wild rows, mines and trades. The plan it priced stays on the row
// (resourceSupplyRow.fields) for the executor of the opened field.

// fieldProtected are the cells a new field never takes: held building
// reservations, the interiors of the housing shells, and the firebreak ring.
func (r *Rounder) fieldProtected(call context.Context, state ControlState, review store.Rounds, projection observation.ColonyProjection, claims domain.Fact[[]policy.ConstructionClaim]) ([]domain.Cell, error) {
	p := r.player
	held, err := p.journal.BuildingReservations(call, state.Snapshot)
	if err != nil {
		return nil, err
	}
	var protected []domain.Cell
	plans, err := p.journal.LoadPlans(call)
	if err != nil {
		return nil, err
	}
	protected = append(protected, pendingMonumentCells(plans, projection.Facts.QuestOffers)...)
	for _, h := range held {
		protected = append(protected, h.Footprint...)
	}
	var shells []store.PlanState
	for _, binding := range review.Standards {
		if binding.Concern != policy.MaintainHousing {
			continue
		}
		shelter, err := p.journal.LoadStandard(call, binding.Standard)
		if err != nil {
			return nil, err
		}
		for _, method := range shelter.Methods {
			plan, err := p.journal.LoadPlan(call, method.Plan)
			if err != nil {
				return nil, err
			}
			shells = append(shells, plan)
		}
	}
	claimed, _ := claims.Value()
	protected = append(protected, shellInteriors(shells, claimed)...)
	ring, err := firebreakRing(projection)
	if err != nil {
		return nil, err
	}
	return append(protected, ring...), nil
}

// sowableCrops are the catalog's sowable plants that harvest a thing, none
// when the reviewer's source serves no definitions. The census reads them so a
// resource's crop choices are catalog-derived, not a list of names.
func (r *Rounder) sowableCrops(call context.Context, snapshot domain.GenerationSnapshot) ([]string, error) {
	source, ok := r.native.(observation.DefinitionSource)
	if !ok {
		return nil, nil
	}
	catalog, err := source.DefinitionCatalog(call, boundary.Identity(snapshot))
	if err != nil {
		return nil, err
	}
	return catalog.SowableCrops(), nil
}

// resourceFieldPlanner plans a new field per resource deficit over one read of
// the colony; the site facts are shared across resources.
type resourceFieldPlanner struct {
	choices []policy.CropChoice
	climate policy.CropClimate
	// skill is the best sowing skill among the colonists (policy.GrowerSkill).
	skill domain.Fact[int32]
	// farms are the standing growing zones.
	farms []observation.FarmZoneFact
	// siteRead reads the field site on first use: held reservations and shell
	// interiors cost journal reads a resource no crop serves never pays.
	siteRead func() (policy.FarmSiteRequest, bool, error)
	site     *policy.FarmSiteRequest
	sited    bool
}

func (r *Rounder) newResourceFieldPlanner(call context.Context, state ControlState, review store.Rounds, expected observation.Identity, projection observation.ColonyProjection) *resourceFieldPlanner {
	_, choices := fieldRequest(projection, 0)
	return &resourceFieldPlanner{choices: choices, climate: projection.CropClimate, skill: policy.GrowerSkill(projection.WorkPawns), farms: projection.Farms,
		siteRead: func() (policy.FarmSiteRequest, bool, error) {
			// A colony with no layout plan has no anchor and prices no field.
			anchor, planned := fieldAnchor(projection)
			if !planned {
				return policy.FarmSiteRequest{}, false, nil
			}
			claims, err := r.player.journal.ConstructionClaims(call, state.Snapshot, expected.Tick)
			if err != nil {
				return policy.FarmSiteRequest{}, false, err
			}
			protected, err := r.fieldProtected(call, state, review, projection, claims)
			if err != nil {
				return policy.FarmSiteRequest{}, false, err
			}
			site := policy.FarmSiteRequest{Bounds: projection.Bounds, Anchor: anchor, Cells: projection.Cells, Protected: protected}
			if fields, ok := layoutFieldCells(projection); ok {
				site.Fields = fields
			}
			return site, true, nil
		}}
}

// serves is whether any crop harvests resource.
func (f *resourceFieldPlanner) serves(resource policy.Resource) bool {
	for _, crop := range f.choices {
		if harvests, ok := crop.Harvests.Value(); ok && harvests == resource {
			return true
		}
	}
	return false
}

func (f *resourceFieldPlanner) siteOf() (policy.FarmSiteRequest, bool, error) {
	if f.site == nil {
		site, ok, err := f.siteRead()
		if err != nil {
			return site, false, err
		}
		f.site, f.sited = &site, ok
	}
	return *f.site, f.sited, nil
}

// standing is the units the standing fields of resource's crops will deliver.
func (f *resourceFieldPlanner) standing(resource policy.Resource) int64 {
	byName := map[string]policy.CropChoice{}
	for _, crop := range f.choices {
		byName[crop.Name] = crop
	}
	var units float64
	for _, farm := range f.farms {
		if crop, ok := byName[farm.Crop]; ok {
			units += policy.StandingFieldYield(crop, resource, farm.UsableCells)
		}
	}
	return int64(math.Round(units))
}

// candidate is the priced new field for need units of resource and the plan
// behind it, false when no crop can be sown for it now.
func (f *resourceFieldPlanner) candidate(resource policy.Resource, need int64) (policy.SupplyCandidate, policy.FieldPlan, bool, error) {
	site, sited, err := f.siteOf()
	if err != nil || !sited {
		return policy.SupplyCandidate{}, policy.FieldPlan{}, false, err
	}
	plan, ok := policy.PlanFieldByResource(policy.ResourceFieldRequest{
		Resource: resource, Deficit: domain.Known(float64(need)), Choices: f.choices, Climate: f.climate, Site: site, GrowerSkill: f.skill,
	})
	if !ok {
		return policy.SupplyCandidate{}, policy.FieldPlan{}, false, nil
	}
	candidate, ok := policy.ResourceFieldCandidate(resource, plan)
	return candidate, plan, ok, nil
}

// chopMinGrowth is the growth fraction a plantation tree must reach before the
// chop census offers it: the fell point of the best tree species the
// colony can sow now, else the configured gate. Recomputed from the review's
// own read, never stored.
func (r *Rounder) chopMinGrowth(projection observation.ColonyProjection) float64 {
	if fraction, ok := policy.TreeFellFraction(cropChoices(projection), "WoodLog", projection.CropClimate.Biome, policy.GrowerSkill(projection.WorkPawns)); ok {
		return fraction
	}
	return r.policy.ChopMinGrowth
}
