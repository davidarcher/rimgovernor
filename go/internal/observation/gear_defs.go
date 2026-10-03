package observation

import (
	"context"
	"fmt"
	"math"
	"slices"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// Apparel facts are Go views over the catalog's ThingDef rows and stat table
// (#1732): layers, body part groups, wear filters, outfit tags, equipped stat
// offsets and comps come from the apparel row, armor, insulation and market
// value from the game's own stat values for the (def, stuff) pair.

// GearDefinitions is what a gear census is decoded against: the load's
// catalog and its finished research projects (unknown without a research
// read).
type GearDefinitions struct {
	Catalog  *bridge.DefinitionCatalog
	Finished domain.Fact[map[string]bool]
}

// ReadGearDefinitions reads the gear definitions from source: its catalog
// and, when it is a ResearchSource, the finished projects. A source with no
// catalog yields none, which leaves every gear fact unknown.
func ReadGearDefinitions(ctx context.Context, source any, id *c.Identity) (GearDefinitions, error) {
	facts, _, err := readDefinitionFacts(ctx, source, id, nil)
	return GearDefinitions{Catalog: facts.catalog, Finished: facts.finished}, err
}

// The game's stats a loadout option takes from the stat table.
const (
	statArmorSharp     = "ArmorRating_Sharp"
	statArmorBlunt     = "ArmorRating_Blunt"
	statInsulationCold = "Insulation_Cold"
	statInsulationHeat = "Insulation_Heat"
	statMarketValue    = "MarketValue"
	statMoveSpeed      = "MoveSpeed"
	statPsychic        = "PsychicSensitivity"
)

// gearWearer is a pawn's wear inputs: what the apparel rows cannot say.
type gearWearer struct {
	gender d.Gender
	stage  d.DevelopmentalStage
	groups map[string]bool
}

// wearerOf is the wear inputs of one gear row; false when the producer sent
// none.
func wearerOf(p *o.GearLoadout) (gearWearer, bool) {
	if p.Gender == nil || p.DevelopmentalStage == nil {
		return gearWearer{}, false
	}
	w := gearWearer{gender: p.GetGender(), stage: p.GetDevelopmentalStage(), groups: map[string]bool{}}
	for _, g := range p.BodyPartGroups {
		w.groups[g] = true
	}
	return w, true
}

// canWear is the game's wear filter for apparel properties: the gender
// (ApparelProperties.CorrectGenderForWearing), the developmental stage filter
// (PawnCanWear) and a present part in one of the garment's body part groups
// (ApparelUtility.HasPartsToWear).
func (w gearWearer) canWear(a *d.ApparelProperties) bool {
	if a.GetGender() != d.Gender_GENDER_NONE && w.gender != d.Gender_GENDER_NONE && a.GetGender() != w.gender {
		return false
	}
	if int32(a.GetDevelopmentalStageFilter())&int32(w.stage) == 0 {
		return false
	}
	return slices.ContainsFunc(a.GetBodyPartGroups(), func(g string) bool { return w.groups[g] })
}

// apparelDefinitions is every apparel def the wearer can wear, by name, with
// the role facts of the apparel policy: armor is a def only the game's Soldier
// outfit tag names (not Worker too), covers-body one covering the torso or
// legs.
func apparelDefinitions(catalog *bridge.DefinitionCatalog, w gearWearer) []policy.ApparelDefinition {
	var out []policy.ApparelDefinition
	for name, row := range catalog.ThingDefs {
		a := row.GetApparel()
		if a == nil || !w.canWear(a) {
			continue
		}
		stage := int32(a.GetDevelopmentalStageFilter())
		out = append(out, policy.ApparelDefinition{Name: name,
			Armor:      slices.Contains(a.GetDefaultOutfitTags(), "Soldier") && !slices.Contains(a.GetDefaultOutfitTags(), "Worker"),
			Child:      stage&int32(d.DevelopmentalStage_DEVELOPMENTAL_STAGE_CHILD) != 0,
			Adult:      stage&int32(d.DevelopmentalStage_DEVELOPMENTAL_STAGE_ADULT) != 0,
			CoversBody: slices.Contains(a.GetBodyPartGroups(), "Torso") || slices.Contains(a.GetBodyPartGroups(), "Legs")})
	}
	slices.SortFunc(out, func(a, b policy.ApparelDefinition) int { return strings.Compare(a.Name, b.Name) })
	return out
}

// apparelRow is def's apparel properties, nil when the catalog has no such
// row or it is not apparel.
func apparelRow(catalog *bridge.DefinitionCatalog, def string) (*d.ThingDef, *d.ApparelProperties) {
	row := catalog.ThingDef(def)
	return row, row.GetApparel()
}

// distinct is values without repeats, in order.
func distinct(values []string) []string {
	out := make([]string, 0, len(values))
	for _, v := range values {
		if !slices.Contains(out, v) {
			out = append(out, v)
		}
	}
	return out
}

// statOffset is the sum of a def's equipped stat offsets for stat.
func statOffset(row *d.ThingDef, stat string) float32 {
	var sum float32
	for _, entry := range row.GetEquippedStatOffsets() {
		if m := entry.GetValue(); m.GetStat() == stat {
			sum += m.GetValue()
		}
	}
	return sum
}

// hasShield reports a def with the shield comp.
func hasShield(row *d.ThingDef) bool {
	return slices.ContainsFunc(row.GetComps(), func(c *d.Opt_CompPropertiesAny) bool { return c.GetValue().GetCompProperties_Shield() != nil })
}

// optionStat is one stat of the option's (def, stuff) at Normal quality
// before condition, never negative. A stat the game hides for the def is an
// error unless tolerateHidden: armor and insulation are shown for every
// apparel (their StatDefs do not set showIfUndefined false), so a hidden one
// is a catalog gap, never a garment without it. Market value is the exception:
// the game hides it for gear nobody can trade (mech apparel), which is zero
// cost to the planner, not an unknown.
func optionStat(catalog *bridge.DefinitionCatalog, def, stuff, stat string, tolerateHidden bool) (float64, error) {
	value, shown, err := catalog.ShownStatValue(def, stuff, stat)
	if err != nil {
		return 0, err
	}
	if !shown {
		if tolerateHidden {
			return 0, nil
		}
		return 0, fmt.Errorf("stat %s is not shown for apparel %s with stuff %q", stat, def, stuff)
	}
	return math.Max(0, float64(value)), nil
}
