package observation

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/bridge/recordedrows"
	"github.com/davidarcher/RimGovernor/go/internal/testkit"
	"github.com/davidarcher/RimGovernor/go/internal/testkit/recordedcatalog"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// recordedRows is a slice of the game's own recorded rows (the named defs and
// what they join to), for a test to edit where it needs a row the game does
// not have.
func recordedRows(t testing.TB, names ...string) *recordedrows.Slice {
	t.Helper()
	s := recordedrows.Take(t, recordedrows.Named(), "thing_category_defs")
	s.Add(names...)
	return s
}

// decodeRows decodes a slice as the catalog of a load.
func decodeRows(s *recordedrows.Slice) *bridge.DefinitionCatalog {
	s.T.Helper()
	catalog, err := recordedcatalog.FromSlice(s, "load")
	if err != nil {
		s.T.Fatalf("%v", err)
	}
	return catalog
}

// catalogOf is the catalog of the named recorded defs.
func catalogOf(t testing.TB, names ...string) *bridge.DefinitionCatalog {
	t.Helper()
	return decodeRows(recordedRows(t, names...))
}

// addRecordedEnvironment adds the recorded weather, game condition and room
// stat rows (with the class chains they name) to a hand-built catalog wire,
// and the invented biomes the darkness tests read: FixtureDarkBiome holds a
// condition of a mod subclass of the no-sunlight family, FixtureLitBiome an
// unrelated one.
func addRecordedEnvironment(t testing.TB, v *o.DefinitionCatalog) {
	t.Helper()
	rec, err := testkit.LoadRecordedCatalogWire()
	if err != nil {
		t.Fatalf("%v", err)
	}
	if v.Defs == nil {
		v.Defs = &d.DefSets{}
	}
	for _, row := range rec.Defs.WeatherDefs {
		v.Defs.WeatherDefs = append(v.Defs.WeatherDefs, proto.Clone(row).(*d.WeatherDef))
	}
	for _, row := range rec.Defs.GameConditionDefs {
		v.Defs.GameConditionDefs = append(v.Defs.GameConditionDefs, proto.Clone(row).(*d.GameConditionDef))
	}
	for _, row := range rec.Defs.RoomStatDefs {
		v.Defs.RoomStatDefs = append(v.Defs.RoomStatDefs, proto.Clone(row).(*d.RoomStatDef))
	}
	held := map[string]bool{}
	for _, chain := range v.ClassChains {
		held[chain.GetName()] = true
	}
	for _, row := range rec.ClassChains {
		if !held[row.GetName()] {
			v.ClassChains = append(v.ClassChains, proto.Clone(row).(*o.ClassChain))
		}
	}
	const base, dark = "RimWorld.GameCondition", "RimWorld.GameCondition_NoSunlight"
	v.ClassChains = append(v.ClassChains, &o.ClassChain{Name: "FixtureMod.GameCondition_PermanentDark", Bases: []string{dark, base}})
	v.Defs.GameConditionDefs = append(v.Defs.GameConditionDefs,
		&d.GameConditionDef{DefName: "FixtureDarkness", ConditionClass: "FixtureMod.GameCondition_PermanentDark"},
		&d.GameConditionDef{DefName: "FixtureBreeze", ConditionClass: "RimWorld.GameCondition_TemperatureOffset"})
	v.Defs.BiomeDefs = append(v.Defs.BiomeDefs, &d.BiomeDef{DefName: "FixtureDarkBiome", BiomeMapConditions: []string{"FixtureBreeze", "FixtureDarkness"}},
		&d.BiomeDef{DefName: "FixtureLitBiome", BiomeMapConditions: []string{"FixtureBreeze"}}, &d.BiomeDef{DefName: "FixtureBareBiome"})
}
