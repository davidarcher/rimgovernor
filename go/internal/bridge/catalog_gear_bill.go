package bridge

import (
	"slices"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/bridge/stateval"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

const smokePopVerbClass = "RimWorld.Verb_SmokePop"

// ApparelBill is what a bill option of an apparel def asks of the colony:
// the research projects its recipe requires and the adjusted cost of the
// def made from the stuff (empty for a def not made from stuff), both read
// from the rows. The recipe is the first by defName whose only product is the
// def (RecipeDef.ProducedThingDef); false when no recipe makes it.
func (catalog *DefinitionCatalog) ApparelBill(def, stuff string) (research []string, ingredients []stateval.Cost, ok bool, err error) {
	rows := catalog.Defs[(&d.RecipeDef{}).ProtoReflect().Descriptor().FullName()]
	names := make([]string, 0, len(rows))
	for name := range rows {
		names = append(names, name)
	}
	slices.SortFunc(names, strings.Compare)
	for _, name := range names {
		row := rows[name].(*d.RecipeDef)
		if len(row.GetSpecialProducts()) > 0 || len(row.GetProducts()) != 1 || row.GetProducts()[0].GetValue().GetThingDef() != def {
			continue
		}
		for _, project := range append([]string{row.GetResearchPrerequisite()}, row.GetResearchPrerequisites()...) {
			if project != "" && !slices.Contains(research, project) {
				research = append(research, project)
			}
		}
		slices.Sort(research)
		eval, err := catalog.StatEvaluator()
		if err != nil {
			return nil, nil, false, err
		}
		ingredients, err = eval.CostListAdjusted(def, stuff)
		if err != nil {
			return nil, nil, false, err
		}
		return research, ingredients, true, nil
	}
	return nil, nil, false, nil
}

// SmokePop is whether the def has a smoke-pop verb (a Verb_SmokePop in its
// verbs).
func (catalog *DefinitionCatalog) SmokePop(def string) bool {
	row := catalog.ThingDefs[def]
	for _, verb := range row.GetVerbs() {
		if verb.GetValue().GetVerbClass() == smokePopVerbClass {
			return true
		}
	}
	return false
}
