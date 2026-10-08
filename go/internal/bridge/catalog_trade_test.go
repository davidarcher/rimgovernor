package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
	"testing"
)

func TestTraderKindSellCompatibility(t *testing.T) {
	generator := &d.StockGeneratorAny{Value: &d.StockGeneratorAny_StockGenerator_SingleDef{StockGenerator_SingleDef: &d.StockGenerator_SingleDef{ThingDef: "Steel", CountRange: &d.IntRange{Max: 10}}}}
	row := &d.TraderKindDef{DefName: "bulk", StockGenerators: []*d.Opt_StockGeneratorAny{{Value: generator}}}
	catalog := &DefinitionCatalog{ThingDefs: map[string]*d.ThingDef{"Steel": {DefName: "Steel"}, "WoodLog": {DefName: "WoodLog"}}, Defs: map[protoreflect.FullName]map[string]proto.Message{row.ProtoReflect().Descriptor().FullName(): {"bulk": row}}}
	if yes, known := catalog.TraderKindCanSupply("bulk", policy.ResourceKey{Def: "Steel"}).Value(); !known || !yes {
		t.Fatal("known steel sell generator rejected")
	}
	if yes, known := catalog.TraderKindCanSupply("bulk", policy.ResourceKey{Def: "WoodLog"}).Value(); !known || yes {
		t.Fatal("invented wood stock compatibility")
	}
	row.StockGenerators = append(row.StockGenerators, &d.Opt_StockGeneratorAny{Value: &d.StockGeneratorAny{Value: &d.StockGeneratorAny_StockGenerator_MarketValue{StockGenerator_MarketValue: &d.StockGenerator_MarketValue{}}}})
	if _, known := catalog.TraderKindCanSupply("bulk", policy.ResourceKey{Def: "WoodLog"}).Value(); known {
		t.Fatal("unsupported generator must remain unknown")
	}
	row.StockGenerators = []*d.Opt_StockGeneratorAny{{Value: &d.StockGeneratorAny{Value: &d.StockGeneratorAny_StockGenerator_BuySingleDef{StockGenerator_BuySingleDef: &d.StockGenerator_BuySingleDef{ThingDef: "Steel"}}}}}
	if yes, known := catalog.TraderKindCanSupply("bulk", policy.ResourceKey{Def: "Steel"}).Value(); !known || yes {
		t.Fatal("buy-only generator counted as supply")
	}
}
