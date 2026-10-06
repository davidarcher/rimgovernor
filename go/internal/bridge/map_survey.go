package bridge

import (
	"fmt"
	"slices"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// Terrain affordance names of the mirror's foundation_affordances column.
const (
	affordanceHeavy      = "Heavy"
	affordanceLight      = "Light"
	affordanceBridgeable = "Bridgeable"
)

// SurveyFromCells is the whole-map survey the layout plan is derived from
// (#727), read off the cell mirror's planning window (#2272): terrain and
// foundation affordances from the columns, ore and trees from the thing list
// and the catalog's defs, roofs from the catalog's roof rules (#1890). A
// fogged cell is not held; it reads as solid rock to mine out, since the fog
// hides mountain far more often than a cavern, and the excavation steps
// check the cell once it is seen.
//
// Hazard is the top terrain's own def flags. Dries is the top terrain's
// driesTo: the mirror carries no base terrain def, so ground a floor or
// bridge covers is not told to dry.
func SurveyFromCells(cells []policy.SiteCell, bounds policy.Bounds, catalog *DefinitionCatalog) (policy.MapSurvey, error) {
	if bounds.Width < 1 || bounds.Height < 1 {
		return policy.MapSurvey{}, contract("invalid map survey bounds")
	}
	roofs, err := catalog.RoofRules()
	if err != nil {
		return policy.MapSurvey{}, err
	}
	out := policy.MapSurvey{Bounds: bounds, Cells: make([]policy.SurveyCell, 0, len(cells))}
	for _, cell := range cells {
		if cell.Cell.X < 0 || cell.Cell.X >= bounds.Width || cell.Cell.Z < 0 || cell.Cell.Z >= bounds.Height {
			continue
		}
		row, err := surveyCell(cell, roofs, catalog)
		if err != nil {
			return policy.MapSurvey{}, err
		}
		out.Cells = append(out.Cells, row)
	}
	out.Cells = append(out.Cells, unseenRock(out.Cells, bounds)...)
	return out, nil
}

// unseenRock is a rock cell for every cell of bounds that read left out
// (fog hides it): plannable ground, mined out like the stone around it.
func unseenRock(held []policy.SurveyCell, bounds policy.Bounds) []policy.SurveyCell {
	seen := make([]bool, int(bounds.Width)*int(bounds.Height))
	for _, c := range held {
		if c.Cell.X >= 0 && c.Cell.X < bounds.Width && c.Cell.Z >= 0 && c.Cell.Z < bounds.Height {
			seen[int(c.Cell.Z)*int(bounds.Width)+int(c.Cell.X)] = true
		}
	}
	var out []policy.SurveyCell
	for i, ok := range seen {
		if !ok {
			out = append(out, policy.SurveyCell{Cell: domain.Cell{X: int32(i) % bounds.Width, Z: int32(i) / bounds.Width}, Rock: true, Footing: policy.FootingFirm, ThickRoof: true})
		}
	}
	return out
}

// surveyCell decodes one held cell. A thick roof is the roof rules' (#1890);
// a roof def they lack is an error wrapping policy.ErrUnknownRoof.
func surveyCell(cell policy.SiteCell, roofs policy.RoofRules, catalog *DefinitionCatalog) (policy.SurveyCell, error) {
	affordances := strings.Split(value(cell.FoundationAffordances), ",")
	footing := policy.FootingFirm
	if !slices.Contains(affordances, affordanceHeavy) {
		footing = policy.FootingNone
		if slices.Contains(affordances, affordanceLight) {
			footing = policy.FootingLight
		}
	}
	roof := value(cell.Roof)
	rule, known := roofs[roof]
	if roof != "" && !known {
		return policy.SurveyCell{}, fmt.Errorf("%w %q at %v", policy.ErrUnknownRoof, roof, cell.Cell)
	}
	var terrain *d.TerrainDef
	if name := value(cell.Terrain); name != "" {
		if terrain = catalog.TerrainDefs[name]; terrain == nil {
			return policy.SurveyCell{}, contract("catalog has no def row for terrain %s at %v", name, cell.Cell)
		}
	}
	rock, ruin := cell.NaturalRock(), cell.Ruin()
	edifice := cell.PlayerEdifice()
	ore, tree := false, false
	for _, thing := range cell.Things {
		def := catalog.ThingDef(thing.Def)
		switch {
		case thing.Has(policy.FlagEdifice):
			ore = ore || def.GetBuilding().GetIsResourceRock()
		case thing.Category == policy.ThingPlant:
			plant := def.GetPlant()
			tree = tree || plant.GetForceIsTree() || plant.GetTreeCategory() != d.TreeCategory_TREE_CATEGORY_NONE
		}
	}
	return policy.SurveyCell{
		Cell:       cell.Cell,
		Walkable:   value(cell.Walkable),
		Rock:       rock,
		Built:      edifice != "",
		Footing:    footing,
		Bridgeable: slices.Contains(affordances, affordanceBridgeable),
		Dries:      terrain.GetDriesTo() != "",
		Hazard:     terrain.GetDangerous() || terrain.GetBurnDamage() > 0 || terrain.GetHeatPerTick() > 0 || terrain.GetToxicBuildupFactor() > 0,
		ThickRoof:  rule.Thick,
		Fertility:  value(cell.Fertility),
		Ore:        ore,
		Tree:       tree,
		// Occupied off rock, player edifice and clearable ruin is a
		// standing prop (#1533).
		Prop: cell.Occupied() && !rock && edifice == "" && !ruin,
		Ruin: ruin,
	}, nil
}

// value is a fact's value, the zero value when unknown.
func value[T any](f domain.Fact[T]) T {
	v, _ := f.Value()
	return v
}
