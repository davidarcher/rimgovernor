package observation

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/testkit"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// decodeCatalog decodes v the way the client does, with the context, def
// sets, constants and the one terrain def a catalog needs filled in.
func decodeCatalog(t *testing.T, v *o.DefinitionCatalog) *bridge.DefinitionCatalog {
	t.Helper()
	id := &c.Identity{ColonyId: proto.String("colony"), LoadToken: proto.String("load"), MapId: proto.Int32(0)}
	v.Context = &c.ObservationContext{Identity: id, Tick: proto.Int64(12), NativeGeneration: proto.Uint64(7)}
	if v.Defs == nil {
		v.Defs = &d.DefSets{}
	}
	addRecordedEnvironment(t, v)
	v.Derived, v.GameConstants = &o.CatalogDerived{CurrencyDef: "Silver", WortDef: "Wort", FullRotRateC: 10}, testGameConstants()
	if len(v.TerrainDefs) == 0 {
		v.TerrainDefs = []*d.TerrainDef{{DefName: "AnchorTerrain"}}
	}
	catalog, err := bridge.DecodeDefinitionCatalog(v, id)
	if err != nil {
		t.Fatal(err)
	}
	return catalog
}

// itemFact is what a catalog test says about one def: its base deterioration
// (stuffless), whether the game calls it medicine and whether it is raw meat.
type itemFact struct {
	deterioration float32
	medicine, raw bool
}

// itemCatalog is a decoded definition catalog whose ThingDefs are the items,
// with the game-computed flags and the stat values of each, and whose
// TerrainDefs are the terrains, each with its stats (cleanliness, beauty,
// flammability) in that order.
func itemCatalog(t *testing.T, items map[string]itemFact, terrains map[string][3]float32) *bridge.DefinitionCatalog {
	t.Helper()
	v := &o.DefinitionCatalog{ThingDefs: []*d.ThingDef{{DefName: "Anchor"}}}
	stat := func(name string, value float32) *d.Opt_StatModifier {
		return &d.Opt_StatModifier{Value: &d.StatModifier{Stat: name, Value: value}}
	}
	for name, item := range items {
		def := &d.ThingDef{DefName: name, StatBases: []*d.Opt_StatModifier{stat("DeteriorationRate", item.deterioration)}}
		if item.medicine {
			def.StatBases = append(def.StatBases, stat("MedicalPotency", 1))
		}
		if item.raw {
			def.Category, def.ThingCategories = d.ThingCategory_THING_CATEGORY_ITEM, []string{"MeatRaw"}
		}
		v.ThingDefs = append(v.ThingDefs, def)
		v.ThingFacts = append(v.ThingFacts, &o.ThingDefFacts{DefName: name})
	}
	for name, stats := range terrains {
		v.TerrainDefs = append(v.TerrainDefs, &d.TerrainDef{DefName: name, PathCost: 2, Natural: name == "Soil",
			StatBases: []*d.Opt_StatModifier{stat("Cleanliness", stats[0]), stat("Beauty", stats[1]), stat("Flammability", stats[2])}})
	}
	return decodeCatalog(t, v)
}

// testGameConstants are the game constants a hand-built reply carries: the
// recorded game's, which the calendar check and the stat evaluator read.
func testGameConstants() *d.GameConstants {
	rec, err := testkit.LoadRecordedCatalogWire()
	if err != nil {
		panic(err)
	}
	return proto.Clone(rec.GameConstants).(*d.GameConstants)
}
