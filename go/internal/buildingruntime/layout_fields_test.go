package buildingruntime

import (
	"fmt"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// The cross-crop ledger (#1308): with rice 400 urgent and hay 200 in one
// patch, rice ranks first and its block covers the patch's rich cells
// before hay gets any; swapping the urgencies swaps who gets them.
func TestFieldLedgerRichSoilToHighestDemand(t *testing.T) {
	hay := policy.CropChoice{Name: "Plant_Haygrass", Edible: domain.Known(false), FertilityMin: domain.Known(0.5)}
	run := func(ricePriority, hayPriority int) (riceRich, hayRich int) {
		plan := policy.LayoutPlan{Zones: []policy.LayoutZone{{Kind: policy.ZoneField}}}
		facts := observation.ColonyProjection{TechTier: domain.Known(policy.TechTierCamp)}
		rich := map[domain.Cell]bool{}
		for z := int32(0); z < 20; z++ {
			plan.Zones[0].Runs = append(plan.Zones[0].Runs, policy.RowRun{Z: z, X: 0, Length: 40})
			for x := int32(0); x < 40; x++ {
				f := 1.0
				if x >= 30 {
					f, rich[domain.Cell{X: x, Z: z}] = 1.4, true
				}
				facts.Cells = append(facts.Cells, policy.SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true), Things: policy.OccupantThings(false), Zone: domain.Known(false), ZoneID: domain.Known(""), Roofed: domain.Known(false), Fertility: domain.Known(f)})
			}
		}
		facts.LayoutPlan = domain.Known(plan)
		facts.Definitions = []observation.PlanningDefinition{{Name: "Plant_Haygrass", Edible: domain.Known(false)}}
		goal := func(priority int) store.StandardState {
			return store.StandardState{Standard: domain.Standard{Priority: priority}}
		}
		ledger := rankFieldShortfalls([]fieldShortfall{
			{Standard: goal(hayPriority), Options: []policy.FieldBlockOption{{Crop: hay, Needed: 200}}, What: "hay"},
			{Standard: goal(ricePriority), Options: rice(400), What: "food"},
		})
		got := map[string]int{}
		for i, s := range ledger {
			edit, reason, ok := planFieldBlock(facts, domain.Cell{}, s.Options, nil)
			if !ok || edit.Zone != "" {
				t.Fatalf("%s: %+v %q %v", s.What, edit, reason, ok)
			}
			for _, c := range edit.Cells {
				if rich[c] {
					got[edit.Crop]++
				}
			}
			zone := fmt.Sprintf("Zone_%d", i)
			zoneBlockCells(&facts, zone, edit.Cells...)
			facts.Farms = append(facts.Farms, observation.FarmZoneFact{ID: zone, Crop: edit.Crop})
		}
		return got["Plant_Rice"], got["Plant_Haygrass"]
	}
	if riceRich, hayRich := run(1, 3); riceRich != 200 || hayRich != 0 {
		t.Fatalf("urgent rice: rice rich %d, hay rich %d", riceRich, hayRich)
	}
	if riceRich, hayRich := run(3, 0); hayRich <= riceRich {
		t.Fatalf("urgent hay: rice rich %d, hay rich %d", riceRich, hayRich)
	}
}

// A courtyard plan on a non-rectangular (plus-shaped) rich patch is still
// farmed: planFieldBlock returns cells of the patch, rich ones first, none
// of them under a room or hallway (#1960).
func TestFieldBlockFarmsCourtyardPatch(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	const n = 140
	patch := func(x, z int32) bool {
		return (x >= 60 && x < 80 && z >= 66 && z < 74) || (x >= 66 && x < 74 && z >= 60 && z < 80)
	}
	s := policy.MapSurvey{Bounds: policy.Bounds{Width: n, Height: n}}
	facts := observation.ColonyProjection{TechTier: domain.Known(policy.TechTierCamp)}
	rich := map[domain.Cell]bool{}
	for z := int32(0); z < n; z++ {
		for x := int32(0); x < n; x++ {
			f := 0.5
			if patch(x, z) {
				f = 1.4
				rich[domain.Cell{X: x, Z: z}] = true
			}
			c := domain.Cell{X: x, Z: z}
			s.Cells = append(s.Cells, policy.SurveyCell{Cell: c, Walkable: true, Fertility: f})
			facts.Cells = append(facts.Cells, policy.SiteCell{Cell: c, Walkable: domain.Known(true), Things: policy.OccupantThings(false), Zone: domain.Known(false), ZoneID: domain.Known(""), Roofed: domain.Known(false), Fertility: domain.Known(f)})
		}
	}
	plan, ok := policy.DeriveLayoutPlan(s, 8, policy.TechTierCamp, nil, 30, 0).Value()
	if !ok {
		t.Fatal("no plan")
	}
	facts.LayoutPlan = domain.Known(plan)
	edit, reason, ok := planFieldBlock(facts, domain.Cell{X: 70, Z: 70}, rice(60), nil)
	if !ok || len(edit.Cells) == 0 {
		t.Fatalf("the courtyard is not farmed: %+v %q", edit, reason)
	}
	for _, c := range edit.Cells {
		if !rich[c] {
			t.Fatalf("block cell %v is off the patch", c)
		}
	}
}

// blockFacts is a Camp-tier colony with two 4x2 plan field blocks at
// x 0-3 and x 6-9 on rows 0-1, fertile open ground everywhere else.
func blockFacts() observation.ColonyProjection {
	plan := policy.LayoutPlan{Zones: []policy.LayoutZone{
		{Kind: policy.ZoneField, Runs: []policy.RowRun{{Z: 0, X: 0, Length: 4}, {Z: 1, X: 0, Length: 4}}},
		{Kind: policy.ZoneField, Runs: []policy.RowRun{{Z: 0, X: 6, Length: 4}, {Z: 1, X: 6, Length: 4}}},
	}}
	facts := observation.ColonyProjection{TechTier: domain.Known(policy.TechTierCamp), LayoutPlan: domain.Known(plan)}
	for z := int32(0); z < 2; z++ {
		for x := int32(0); x < 10; x++ {
			facts.Cells = append(facts.Cells, policy.SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true), Things: policy.OccupantThings(false), Zone: domain.Known(false), ZoneID: domain.Known(""), Roofed: domain.Known(false), Fertility: domain.Known(1.0)})
		}
	}
	return facts
}

// One 40x30 fertile patch with demand for three crops holds three adjacent
// growing zones with no gap between them, each sized to its demand; more
// demand grows one zone in place and leaves the others (#1283).
func TestFieldPatchCropBlocksByDemand(t *testing.T) {
	plan := policy.LayoutPlan{Zones: []policy.LayoutZone{{Kind: policy.ZoneField}}}
	facts := observation.ColonyProjection{TechTier: domain.Known(policy.TechTierCamp)}
	for z := int32(0); z < 30; z++ {
		plan.Zones[0].Runs = append(plan.Zones[0].Runs, policy.RowRun{Z: z, X: 0, Length: 40})
		for x := int32(0); x < 40; x++ {
			facts.Cells = append(facts.Cells, policy.SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true), Things: policy.OccupantThings(false), Zone: domain.Known(false), ZoneID: domain.Known(""), Roofed: domain.Known(false), Fertility: domain.Known(1.4)})
		}
	}
	facts.LayoutPlan = domain.Known(plan)
	facts.Definitions = []observation.PlanningDefinition{{Name: "Plant_Haygrass", Edible: domain.Known(false)}, {Name: "Plant_Smokeleaf", Edible: domain.Known(false)}}
	hay := policy.CropChoice{Name: "Plant_Haygrass", Edible: domain.Known(false), FertilityMin: domain.Known(0.5)}
	smoke := policy.CropChoice{Name: "Plant_Smokeleaf", Edible: domain.Known(false), FertilityMin: domain.Known(0.5)}
	anchor := domain.Cell{X: 0, Z: 0}
	owner := map[domain.Cell]string{}
	apply := func(zone string, cells []domain.Cell) {
		zoneBlockCells(&facts, zone, cells...)
		for _, c := range cells {
			if owner[c] != "" {
				t.Fatalf("cell %v already in %s", c, owner[c])
			}
			owner[c] = zone
		}
	}
	demands := []struct {
		zone string
		opt  policy.FieldBlockOption
	}{{"Zone_1", rice(300)[0]}, {"Zone_2", policy.FieldBlockOption{Crop: hay, Needed: 250}}, {"Zone_3", policy.FieldBlockOption{Crop: smoke, Needed: 200}}}
	for _, d := range demands {
		edit, reason, ok := planFieldBlock(facts, anchor, []policy.FieldBlockOption{d.opt}, nil)
		if !ok || edit.Zone != "" || edit.Crop != d.opt.Crop.Name {
			t.Fatalf("%s: %+v %q %v", d.zone, edit, reason, ok)
		}
		if n := len(edit.Cells); n*10 < d.opt.Needed*9 || n*10 > d.opt.Needed*11 {
			t.Fatalf("%s sized %d for demand %d", d.zone, n, d.opt.Needed)
		}
		apply(d.zone, edit.Cells)
		facts.Farms = append(facts.Farms, observation.FarmZoneFact{ID: d.zone, Crop: d.opt.Crop.Name})
	}
	// No gap: the zones form one connected footprint and each touches another.
	var start domain.Cell
	all := map[domain.Cell]bool{}
	for c := range owner {
		all[c], start = true, c
	}
	if got := len(cellComponent(all, start)); got != len(all) {
		t.Fatalf("zones split by a gap: %d of %d connected", got, len(all))
	}
	for _, d := range demands {
		touches := false
		for c, z := range owner {
			if z != d.zone {
				continue
			}
			for _, n := range []domain.Cell{{X: c.X, Z: c.Z - 1}, {X: c.X - 1, Z: c.Z}, {X: c.X + 1, Z: c.Z}, {X: c.X, Z: c.Z + 1}} {
				if o := owner[n]; o != "" && o != z {
					touches = true
				}
			}
		}
		if !touches {
			t.Fatalf("%s touches no other crop block", d.zone)
		}
	}
	before := len(owner)
	grow, reason, ok := planFieldBlock(facts, anchor, rice(60), nil)
	if !ok || grow.Zone != "Zone_1" || len(grow.Cells) != 60 {
		t.Fatalf("demand change: %+v %q %v", grow, reason, ok)
	}
	apply("Zone_1", grow.Cells)
	if len(owner) != before+60 {
		t.Fatalf("other zones moved")
	}
}

func zoneBlockCells(facts *observation.ColonyProjection, zone string, cells ...domain.Cell) {
	for i := range facts.Cells {
		for _, c := range cells {
			if facts.Cells[i].Cell == c {
				facts.Cells[i].Zone, facts.Cells[i].ZoneID = domain.Known(true), domain.Known(zone)
			}
		}
	}
}

var riceChoice = policy.CropChoice{Name: "Plant_Rice", FertilityMin: domain.Known(0.5)}

func rice(n int) []policy.FieldBlockOption {
	return []policy.FieldBlockOption{{Crop: riceChoice, Needed: n}}
}

// A new block next to a rice zone opens with the next viable crop; with
// rice the sole viable crop it still opens rice (#1225).
func TestFieldBlockAvoidsNeighbourCrop(t *testing.T) {
	facts := blockFacts()
	anchor := domain.Cell{X: 0, Z: 0}
	var first []domain.Cell
	for z := int32(0); z < 2; z++ {
		for x := int32(0); x < 4; x++ {
			first = append(first, domain.Cell{X: x, Z: z})
		}
	}
	zoneBlockCells(&facts, "Zone_1", first...)
	facts.Farms = []observation.FarmZoneFact{{ID: "Zone_1", Crop: "Plant_Rice"}}
	corn := policy.FieldBlockOption{Crop: policy.CropChoice{Name: "Plant_Corn", FertilityMin: domain.Known(0.5)}, Needed: 8}
	edit, reason, ok := planFieldBlock(facts, anchor, append(rice(8), corn), nil)
	if !ok || edit.Zone != "" || edit.Crop != "Plant_Corn" {
		t.Fatalf("neighbour crop not avoided: %+v %q %v", edit, reason, ok)
	}
	edit, reason, ok = planFieldBlock(facts, anchor, rice(8), nil)
	if !ok || edit.Zone != "" || edit.Crop != "Plant_Rice" {
		t.Fatalf("sole viable crop refused: %+v %q %v", edit, reason, ok)
	}
}

func TestFieldBlockCreatesThenGrows(t *testing.T) {
	facts := blockFacts()
	anchor := domain.Cell{X: 0, Z: 0}
	edit, reason, ok := planFieldBlock(facts, anchor, rice(4), nil)
	if !ok || edit.Zone != "" || edit.Crop != "Plant_Rice" || len(edit.Cells) != 4 {
		t.Fatalf("create %+v %q %v", edit, reason, ok)
	}
	for _, c := range edit.Cells {
		if c.X > 3 {
			t.Fatalf("created outside the first block: %v", edit.Cells)
		}
	}
	zoneBlockCells(&facts, "Zone_1", edit.Cells...)
	facts.Farms = []observation.FarmZoneFact{{ID: "Zone_1", Crop: "Plant_Potato"}}
	grow, reason, ok := planFieldBlock(facts, anchor, rice(4), nil)
	if !ok || grow.Zone != "Zone_1" || grow.Crop != "Plant_Potato" || len(grow.Cells) != 4 {
		t.Fatalf("grow %+v %q %v", grow, reason, ok)
	}
	for _, c := range grow.Cells {
		if c.X > 3 {
			t.Fatalf("grew outside the block: %v", grow.Cells)
		}
	}
}

func TestFieldBlockNextOnlyWhenFull(t *testing.T) {
	facts := blockFacts()
	anchor := domain.Cell{X: 0, Z: 0}
	facts.Farms = []observation.FarmZoneFact{{ID: "Zone_1", Crop: "Plant_Rice"}}
	var first []domain.Cell
	for z := int32(0); z < 2; z++ {
		for x := int32(0); x < 3; x++ {
			first = append(first, domain.Cell{X: x, Z: z})
		}
	}
	zoneBlockCells(&facts, "Zone_1", first...)
	edit, _, ok := planFieldBlock(facts, anchor, rice(8), nil)
	if !ok || edit.Zone != "Zone_1" || len(edit.Cells) != 2 {
		t.Fatalf("partial block should grow first: %+v %v", edit, ok)
	}
	zoneBlockCells(&facts, "Zone_1", edit.Cells...)
	next, _, ok := planFieldBlock(facts, anchor, rice(8), nil)
	if !ok || next.Zone != "" || len(next.Cells) != 8 || next.Cells[0].X != 6 {
		t.Fatalf("full block should open the next: %+v %v", next, ok)
	}
	zoneBlockCells(&facts, "Zone_2", next.Cells...)
	if _, reason, ok := planFieldBlock(facts, anchor, rice(8), nil); ok || reason != "every field block is full" {
		t.Fatalf("all full: %q %v", reason, ok)
	}
}

// A block whose free soil is split by a rock column still creates one
// connected zone, in its largest free part, even when the need exceeds
// the block (#1252).
func TestFieldBlockCreateIsConnected(t *testing.T) {
	facts := blockFacts()
	for i := range facts.Cells {
		if facts.Cells[i].Cell.X == 1 {
			facts.Cells[i].Walkable = domain.Known(false)
		}
	}
	edit, reason, ok := planFieldBlock(facts, domain.Cell{X: 0, Z: 0}, rice(100), nil)
	if !ok || edit.Zone != "" || len(edit.Cells) != 4 || edit.Cells[0].X != 2 {
		t.Fatalf("create %+v %q %v", edit, reason, ok)
	}
	if _, err := domain.NewZoneCreate(domain.GrowingZone, edit.Crop, edit.Cells); err != nil {
		t.Fatalf("disconnected create %v: %v", edit.Cells, err)
	}
}

func TestFieldBlockRefusesWithoutBlocks(t *testing.T) {
	facts := blockFacts()
	facts.LayoutPlan = domain.Known(policy.LayoutPlan{})
	if _, reason, ok := planFieldBlock(facts, domain.Cell{}, rice(4), nil); ok || reason != "layout plan has no field blocks" {
		t.Fatalf("%q %v", reason, ok)
	}
	facts.LayoutPlan = domain.Unknown[policy.LayoutPlan]()
	if _, reason, ok := planFieldBlock(facts, domain.Cell{}, rice(4), nil); ok || reason != "no layout plan" {
		t.Fatalf("%q %v", reason, ok)
	}
}

// Hay and social crops claim their own crop blocks (#1226): neither they
// nor food grow into the other's zone; the new block sits in the same
// patch against the standing zone (#1283).
func TestFieldBlockHayClaimsItsOwnBlock(t *testing.T) {
	facts := blockFacts()
	anchor := domain.Cell{X: 0, Z: 0}
	facts.Definitions = []observation.PlanningDefinition{{Name: "Plant_Haygrass", Edible: domain.Known(false)}}
	zoneBlockCells(&facts, "Zone_1", domain.Cell{X: 0, Z: 0}, domain.Cell{X: 1, Z: 0})
	facts.Farms = []observation.FarmZoneFact{{ID: "Zone_1", Crop: "Plant_Rice"}}
	hay := policy.CropChoice{Name: "Plant_Haygrass", Edible: domain.Known(false), FertilityMin: domain.Known(0.5)}
	ownBlock := func(what string, edit fieldBlockEdit, reason string, ok bool) {
		t.Helper()
		if !ok || edit.Zone != "" || len(edit.Cells) != 4 {
			t.Fatalf("%s: %+v %q %v", what, edit, reason, ok)
		}
		for _, c := range edit.Cells {
			if c.X > 3 || c.Z == 0 && c.X < 2 {
				t.Fatalf("%s: not beside the zone in its patch: %v", what, edit.Cells)
			}
		}
	}
	edit, reason, ok := planFieldBlock(facts, anchor, []policy.FieldBlockOption{{Crop: hay, Needed: 4}}, nil)
	ownBlock("hay grew the rice zone", edit, reason, ok)
	if edit.Crop != "Plant_Haygrass" {
		t.Fatalf("crop %q", edit.Crop)
	}
	facts.Farms[0].Crop = "Plant_Haygrass"
	edit, reason, ok = planFieldBlock(facts, anchor, rice(4), nil)
	ownBlock("food grew the hay zone", edit, reason, ok)
	if edit.Crop != "Plant_Rice" {
		t.Fatalf("crop %q", edit.Crop)
	}
	edit, _, ok = planFieldBlock(facts, anchor, []policy.FieldBlockOption{{Crop: hay, Needed: 4}}, nil)
	if !ok || edit.Zone != "Zone_1" || edit.Crop != "Plant_Haygrass" {
		t.Fatalf("hay should grow its own zone: %+v %v", edit, ok)
	}
}

// A block is laid as a clean rectangle where free soil holds one, not grown
// around notches (a hole beside the anchor must not shape the block).
func TestConnectedPickPrefersACleanRectangle(t *testing.T) {
	t.Parallel()
	free := map[domain.Cell]bool{}
	for z := int32(0); z < 8; z++ {
		for x := int32(0); x < 8; x++ {
			free[domain.Cell{X: x, Z: z}] = true
		}
	}
	delete(free, domain.Cell{X: 1, Z: 1})
	delete(free, domain.Cell{X: 2, Z: 3})
	got := connectedPick(free, nil, nil, domain.Cell{X: 1, Z: 1}, 12)
	if len(got) != 12 {
		t.Fatalf("picked %d cells, want 12", len(got))
	}
	minX, maxX, minZ, maxZ := got[0].X, got[0].X, got[0].Z, got[0].Z
	for _, c := range got {
		minX, maxX, minZ, maxZ = min(minX, c.X), max(maxX, c.X), min(minZ, c.Z), max(maxZ, c.Z)
	}
	if area := (maxX - minX + 1) * (maxZ - minZ + 1); area > 16 {
		t.Errorf("block spans a %dx%d box for 12 cells: %v", maxX-minX+1, maxZ-minZ+1, got)
	}
}

// Rich soil outranks a tidy shape: with rich ground wide enough, the block
// sits on it even when plain soil is nearer the anchor.
func TestConnectedPickSitsOnRichSoil(t *testing.T) {
	t.Parallel()
	free, rich := map[domain.Cell]bool{}, map[domain.Cell]bool{}
	for z := int32(0); z < 6; z++ {
		for x := int32(0); x < 12; x++ {
			c := domain.Cell{X: x, Z: z}
			free[c] = true
			if x >= 6 {
				rich[c] = true
			}
		}
	}
	for _, c := range connectedPick(free, nil, rich, domain.Cell{}, 12) {
		if !rich[c] {
			t.Fatalf("cell %v is plain soil while rich soil was free", c)
		}
	}
}
