package bridge

import (
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// TestSurveyFromCellsReadsTheMirror is #2272: the survey cell comes from the
// mirror's tile columns, thing list and the catalog's terrain, thing and roof
// defs; a fogged cell reads as plain rock.
func TestSurveyFromCellsReadsTheMirror(t *testing.T) {
	catalog := roofCatalog(&d.RoofDef{DefName: "RoofRockThick", IsNatural: true, IsThickRoof: true})
	catalog.TerrainDefs = map[string]*d.TerrainDef{
		"Soil":   {DefName: "Soil"},
		"Marsh":  {DefName: "Marsh", DriesTo: "Soil"},
		"Lava":   {DefName: "Lava", Dangerous: true},
		"Gravel": {DefName: "Gravel"},
	}
	catalog.ThingDefs = map[string]*d.ThingDef{
		"Granite":      {DefName: "Granite"},
		"MineableGold": {DefName: "MineableGold", Building: &d.BuildingProperties{IsResourceRock: true}},
		"Plant_Oak":    {DefName: "Plant_Oak", Plant: &d.PlantProperties{TreeCategory: d.TreeCategory_TREE_CATEGORY_FULL}},
		"Plant_Rice":   {DefName: "Plant_Rice", Plant: &d.PlantProperties{}},
	}
	cell := func(x int32, terrain, affordances string, things ...policy.Thing) policy.SiteCell {
		return policy.SiteCell{Cell: domain.Cell{X: x, Z: 1}, Walkable: domain.Known(true), Terrain: domain.Known(terrain), FoundationAffordances: domain.Known(affordances), Things: things}
	}
	rock := func(def string) policy.Thing {
		return policy.Thing{Def: def, Category: policy.ThingBuilding, Flags: policy.FlagEdifice | policy.FlagImpassable | policy.FlagNaturalRock, Count: 1, Building: &policy.BuildingState{}}
	}
	gold := cell(1, "Gravel", "Heavy,Light", rock("MineableGold"))
	gold.Walkable, gold.Roof = domain.Known(false), domain.Known("RoofRockThick")
	cells := []policy.SiteCell{
		cell(0, "Soil", "Heavy,Light", rock("Granite")),
		gold,
		cell(2, "Soil", "Heavy,Light", policy.OccupantThings(true)...),
		cell(3, "Marsh", "Bridgeable,Light", policy.Thing{Def: "Plant_Oak", Category: policy.ThingPlant, Count: 1}),
		cell(4, "Marsh", "Bridgeable"),
		cell(5, "Lava", "Heavy", policy.Thing{Def: "Plant_Rice", Category: policy.ThingPlant, Count: 1}),
	}
	cells[3].Fertility = domain.Known(1.4)
	survey, err := SurveyFromCells(cells, policy.Bounds{Width: 8, Height: 2}, catalog)
	if err != nil || len(survey.Cells) != 16 {
		t.Fatal(err, len(survey.Cells))
	}
	by := map[domain.Cell]policy.SurveyCell{}
	for _, c := range survey.Cells {
		by[c.Cell] = c
	}
	at := func(x int32) policy.SurveyCell { return by[domain.Cell{X: x, Z: 1}] }
	if got := at(0); !got.Rock || got.Ore || got.Footing != policy.FootingFirm {
		t.Fatalf("granite %+v", got)
	}
	if got := at(1); !got.Rock || !got.Ore || !got.ThickRoof || got.Walkable || got.Prop {
		t.Fatalf("gold %+v", got)
	}
	if got := at(2); !got.Prop || !got.Walkable {
		t.Fatalf("prop %+v", got)
	}
	if got := at(3); got.Footing != policy.FootingLight || !got.Bridgeable || !got.Dries || !got.Tree || got.Fertility != 1.4 {
		t.Fatalf("marsh %+v", got)
	}
	if got := at(4); got.Footing != policy.FootingNone || !got.Bridgeable || got.Tree {
		t.Fatalf("water %+v", got)
	}
	if got := at(5); !got.Hazard || got.Tree || at(4).Hazard {
		t.Fatalf("hazard %+v", got)
	}
	if got := at(7); !got.Rock || got.Walkable || got.Ore {
		t.Fatalf("fogged cell reads as plain rock %+v", got)
	}
}

func TestSurveyFromCellsRefusesUnknownDefs(t *testing.T) {
	catalog := roofCatalog(&d.RoofDef{DefName: "RoofConstructed"})
	catalog.TerrainDefs = map[string]*d.TerrainDef{"Soil": {DefName: "Soil"}}
	bounds := policy.Bounds{Width: 2, Height: 1}
	cell := policy.SiteCell{Cell: domain.Cell{X: 0, Z: 0}, Terrain: domain.Known("Ghost")}
	if _, err := SurveyFromCells([]policy.SiteCell{cell}, bounds, catalog); !errors.Is(err, ErrContract) {
		t.Fatal("unknown terrain def accepted", err)
	}
	cell = policy.SiteCell{Cell: domain.Cell{X: 0, Z: 0}, Roof: domain.Known("RoofGhost")}
	if _, err := SurveyFromCells([]policy.SiteCell{cell}, bounds, catalog); !errors.Is(err, policy.ErrUnknownRoof) {
		t.Fatal("unknown roof accepted", err)
	}
}
