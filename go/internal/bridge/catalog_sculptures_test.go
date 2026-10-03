package bridge

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// TestSculpturesComeFromTheRows (#1721): every art recipe is a sculpture with
// its building's footprint and stuff cost and its work (the recipe's own, else
// the product's WorkToMake), smallest first by cost.
func TestSculpturesComeFromTheRows(t *testing.T) {
	catalog := recipeFixture()
	recipes := catalog.Defs[(&d.RecipeDef{}).ProtoReflect().Descriptor().FullName()]
	recipes["Make_SculptureGrand"] = &d.RecipeDef{DefName: "Make_SculptureGrand", WorkAmount: 105000, Products: []*d.Opt_ThingDefCountClass{product("SculptureGrand", 1)}}
	stat := func(name string, v float32) *d.Opt_StatModifier {
		return &d.Opt_StatModifier{Value: &d.StatModifier{Stat: name, Value: v}}
	}
	catalog.ThingDefs["SculptureSmall"].CostStuffCount = 50
	catalog.ThingDefs["SculptureSmall"].StatBases = []*d.Opt_StatModifier{stat("WorkToMake", 18000)}
	catalog.ThingDefs["SculptureGrand"] = &d.ThingDef{DefName: "SculptureGrand", ThingClass: "Verse.ThingWithComps", CostStuffCount: 400,
		Size: &d.IntVec2{X: 2, Z: 2}, Comps: []*d.Opt_CompPropertiesAny{artComp()}}
	got, err := catalog.sculptures()
	if err != nil {
		t.Fatal(err)
	}
	want := []policy.Sculpture{
		{Recipe: "Make_SculptureSmall", Def: "SculptureSmall", Size: domain.Cell{X: 1, Z: 1}, Cost: 50, Work: 18000},
		{Recipe: "Make_SculptureGrand", Def: "SculptureGrand", Size: domain.Cell{X: 2, Z: 2}, Cost: 400, Work: 105000},
	}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("sculptures %+v, want %+v", got, want)
	}
}
