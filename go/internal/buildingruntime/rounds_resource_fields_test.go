package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// A cotton field is priced from the crop's catalog facts, standing fields of
// the crop count against the deficit, and the plan it priced stays readable by
// candidate ID for the executor.
func TestResourceFieldPlannerPricesAndCountsStandingFields(t *testing.T) {
	cotton := policy.CropChoice{Name: "Plant_Cotton", Available: domain.Known(true), Edible: domain.Known(false),
		GrowDays: domain.Known(5.8), FertilityMin: domain.Known(0.5), FertilitySensitivity: domain.Known(1.0), HarvestWork: domain.Known(200.0),
		SowTags: domain.Known([]string{"Ground"}), Harvests: domain.Known(policy.Resource("Cloth")), UnitsPerCell: domain.Known(8.0)}
	var cells []policy.SiteCell
	for x := int32(0); x < 20; x++ {
		for z := int32(0); z < 20; z++ {
			cells = append(cells, policy.SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true), Zone: domain.Known(false),
				Roofed: domain.Known(false), Fertility: domain.Known(1.0)})
		}
	}
	sited := 0
	f := &resourceFieldPlanner{
		choices: []policy.CropChoice{cotton},
		climate: policy.CropClimate{Sowing: domain.Known(true), DaysRemaining: domain.Known(40.0), OutdoorsDark: domain.Known(false)},
		farms:   []observation.FarmZoneFact{{ID: "z1", Crop: "Plant_Cotton", UsableCells: domain.Known(uint32(3))}, {ID: "z2", Crop: "Plant_Rice", UsableCells: domain.Known(uint32(9))}},
		siteRead: func() (policy.FarmSiteRequest, bool, error) {
			sited++
			return policy.FarmSiteRequest{Bounds: policy.Bounds{Width: 20, Height: 20}, Anchor: domain.Cell{X: 10, Z: 10}, Cells: cells}, true, nil
		},
	}
	if !f.serves("Cloth") || f.serves("Steel") {
		t.Fatal("cotton serves Cloth only")
	}
	if got := f.standing("Cloth"); got != 24 {
		t.Fatalf("three standing cells yield 24, got %d", got)
	}
	if sited != 0 {
		t.Fatal("the site is read only when a field is priced")
	}
	candidate, plan, ok, err := f.candidate("Cloth", 50)
	if err != nil || !ok || sited != 1 {
		t.Fatalf("%v %v sited=%d", ok, err, sited)
	}
	if candidate.ID != policy.ResourceFieldPrefix+"Plant_Cotton" || plan.Crop.Name != "Plant_Cotton" || plan.Needed != 7 || plan.Sites.Cells == 0 {
		t.Fatalf("%+v %s", candidate, plan.Explain())
	}
	if _, _, ok, _ = f.candidate("Cloth", 10); !ok || sited != 1 {
		t.Fatalf("the site is read once: ok=%v sited=%d", ok, sited)
	}
	f.climate.Sowing = domain.Known(false)
	if _, _, ok, _ = f.candidate("Cloth", 50); ok {
		t.Fatal("no field out of the sowing season")
	}
}

// The field step places the fields the supply plan opened: a field the plan
// closed, or an opened harvest candidate that is no field, is not placed.
func TestResourceSupplyOpenedFields(t *testing.T) {
	cotton := policy.FieldPlan{Crop: policy.CropChoice{Name: "Plant_Cotton"}, Needed: 7}
	entry := func(id string, decision policy.SupplyDecision) policy.SupplyEntry {
		return policy.SupplyEntry{Decision: decision, Candidate: policy.SupplyCandidate{Kind: policy.CandidateHarvest, ID: id, Yields: []policy.CandidateYield{{Good: policy.ResourceKey{Def: "Cloth"}}}}}
	}
	s := &resourceSupply{
		order: []policy.Resource{"Cloth"},
		rows:  map[policy.Resource]*resourceSupplyRow{"Cloth": {fields: map[string]policy.FieldPlan{"field:Plant_Cotton": cotton}}},
		plan: policy.ResourceSupply{Plan: policy.SupplyPlan{Portfolio: []policy.SupplyEntry{
			entry("field:Plant_Cotton", policy.SupplyOpen), entry("bush-1", policy.SupplyOpen),
		}}},
	}
	if got := s.openedFields(); len(got) != 1 || got[0].Crop.Name != "Plant_Cotton" || got[0].Needed != 7 {
		t.Fatalf("%+v", got)
	}
	s.plan.Plan.Portfolio[0].Decision = policy.SupplyClose
	if got := s.openedFields(); len(got) != 0 {
		t.Fatalf("a closed field is not placed: %+v", got)
	}
}

// The medical reserve leaves an herbal deficit to a field only when
// the plan opened one (or one stands) and opened no wild source: a wild plant
// the plan priced cheaper keeps the harvest.
func TestResourceSupplyFieldRoute(t *testing.T) {
	healroot := policy.FieldPlan{Crop: policy.CropChoice{Name: "Plant_Healroot"}, Needed: 4}
	entry := func(id string) policy.SupplyEntry {
		return policy.SupplyEntry{Decision: policy.SupplyOpen, Candidate: policy.SupplyCandidate{Kind: policy.CandidateHarvest, ID: id, Yields: []policy.CandidateYield{{Good: policy.ResourceKey{Def: "MedicineHerbal"}}}}}
	}
	supply := func(row *resourceSupplyRow, entries ...policy.SupplyEntry) *resourceSupply {
		return &resourceSupply{
			rows: map[policy.Resource]*resourceSupplyRow{"MedicineHerbal": row},
			plan: policy.ResourceSupply{Plan: policy.SupplyPlan{Portfolio: entries}},
		}
	}
	fields := map[string]policy.FieldPlan{"field:Plant_Healroot": healroot}
	if !supply(&resourceSupplyRow{fields: fields}, entry("field:Plant_Healroot")).fieldRoute("MedicineHerbal") {
		t.Fatal("an opened field is the route when no wild plant is")
	}
	wild := &resourceSupplyRow{fields: fields, open: []policy.AcquisitionSource{{ID: "healroot0", Resource: "MedicineHerbal"}}}
	if supply(wild, entry("field:Plant_Healroot"), entry("healroot0")).fieldRoute("MedicineHerbal") {
		t.Fatal("an opened wild plant keeps the harvest")
	}
	if supply(&resourceSupplyRow{}).fieldRoute("MedicineHerbal") {
		t.Fatal("no field, no route")
	}
	if !supply(&resourceSupplyRow{standing: 3}).fieldRoute("MedicineHerbal") {
		t.Fatal("a standing field is the route")
	}
	if supply(&resourceSupplyRow{standing: 3}).fieldRoute("Steel") {
		t.Fatal("an unplanned resource has no route")
	}
}
