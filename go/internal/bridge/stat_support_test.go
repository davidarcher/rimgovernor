package bridge

import (
	"sync"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/testkit"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

// withStatSupport gives a hand-built catalog what the stat evaluator reads
// beyond its thing and terrain rows: the recorded game's stat defs, class
// chains and stat environment. A def's stat values then come from its own
// statBases and cost list, through the same evaluator the live game's catalog
// uses.
func withStatSupport(t testing.TB, v *o.DefinitionCatalog) *o.DefinitionCatalog {
	t.Helper()
	full := testkit.RecordedCatalogWire(t)
	v.StatEnv = proto.Clone(full.StatEnv).(*o.StatEnv)
	have := map[string]bool{}
	for _, chain := range v.ClassChains {
		have[chain.GetName()] = true
	}
	for _, chain := range full.ClassChains {
		if !have[chain.GetName()] {
			v.ClassChains = append(v.ClassChains, proto.Clone(chain).(*o.ClassChain))
		}
	}
	if v.Defs == nil {
		v.Defs = &d.DefSets{}
	}
	v.Defs.StatDefs, v.Defs.StatCategoryDefs = nil, nil
	for _, stat := range full.Defs.StatDefs {
		v.Defs.StatDefs = append(v.Defs.StatDefs, proto.Clone(stat).(*d.StatDef))
	}
	for _, category := range full.Defs.StatCategoryDefs {
		v.Defs.StatCategoryDefs = append(v.Defs.StatCategoryDefs, proto.Clone(category).(*d.StatCategoryDef))
	}
	v.GameConstants = proto.Clone(full.GameConstants).(*d.GameConstants)
	return v
}

// setStats sets a def's base stats from (stat, value) pairs, replacing a stat
// the def already has.
func setStats(def *d.ThingDef, pairs ...any) {
	for i := 0; i < len(pairs); i += 2 {
		name, value := pairs[i].(string), float32(0)
		switch n := pairs[i+1].(type) {
		case int:
			value = float32(n)
		case float64:
			value = float32(n)
		case float32:
			value = n
		}
		for _, mod := range def.StatBases {
			if mod.GetValue().GetStat() == name {
				mod.Value.Value = value
				name = ""
			}
		}
		if name != "" {
			def.StatBases = append(def.StatBases, &d.Opt_StatModifier{Value: &d.StatModifier{Stat: name, Value: value}})
		}
	}
}

var recordedFull = sync.OnceValues(func() (*DefinitionCatalog, error) {
	wire, err := testkit.LoadRecordedCatalogWire()
	if err != nil {
		return nil, err
	}
	return DecodeDefinitionCatalog(wire, wire.GetContext().GetIdentity())
})

// tradeItem makes a hand-built def an item the player can haul and trade, so
// the game shows its trade stats.
func tradeItem(defs ...*d.ThingDef) {
	for _, def := range defs {
		def.Category, def.Tradeability, def.AlwaysHaulable = d.ThingCategory_THING_CATEGORY_ITEM, d.Tradeability_TRADEABILITY_ALL, true
	}
}

// withRecordedStats gives a catalog built by hand what its stat evaluator
// reads beyond the rows the test wrote: the recorded game's stat defs, class
// chains, constants and environment. It copies the named recorded thing defs
// as they are, so their stats are the game's.
func (catalog *DefinitionCatalog) withRecordedStats(t testing.TB, things ...string) *DefinitionCatalog {
	t.Helper()
	full, err := recordedFull()
	if err != nil {
		t.Fatal(err)
	}
	catalog.statEnv = full.statEnv
	catalog.gameConstants = full.gameConstants
	if catalog.classBases == nil {
		catalog.classBases = map[string][]string{}
	}
	for class, bases := range full.classBases {
		if _, ok := catalog.classBases[class]; !ok {
			catalog.classBases[class] = bases
		}
	}
	stats := (&d.StatDef{}).ProtoReflect().Descriptor().FullName()
	if catalog.Defs == nil {
		catalog.Defs = map[protoreflect.FullName]map[string]proto.Message{}
	}
	catalog.Defs[stats] = full.Defs[stats]
	categories := (&d.StatCategoryDef{}).ProtoReflect().Descriptor().FullName()
	catalog.Defs[categories] = full.Defs[categories]
	for _, name := range things {
		row := full.ThingDefs[name]
		if row == nil {
			t.Fatalf("the recorded game has no def %s", name)
		}
		catalog.ThingDefs[name] = proto.Clone(row).(*d.ThingDef)
	}
	return catalog
}
