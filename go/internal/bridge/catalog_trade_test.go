package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
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

func buyCatalog(generators ...*d.StockGeneratorAny) *DefinitionCatalog {
	row := &d.TraderKindDef{DefName: "buyer"}
	for _, g := range generators {
		row.StockGenerators = append(row.StockGenerators, &d.Opt_StockGeneratorAny{Value: g})
	}
	return &DefinitionCatalog{
		Constants: &o.CatalogConstants{CurrencyDef: "Silver"},
		ThingDefs: map[string]*d.ThingDef{
			"Jewel":  {DefName: "Jewel", Tradeability: d.Tradeability_TRADEABILITY_SELLABLE, TradeTags: []string{"Art"}, TechLevel: d.TechLevel_TECH_LEVEL_MEDIEVAL},
			"Gun":    {DefName: "Gun", Tradeability: d.Tradeability_TRADEABILITY_ALL, TradeTags: []string{"Weapon"}, TechLevel: d.TechLevel_TECH_LEVEL_INDUSTRIAL},
			"Secret": {DefName: "Secret", Tradeability: d.Tradeability_TRADEABILITY_NONE, TradeTags: []string{"Art"}},
		},
		Defs: map[protoreflect.FullName]map[string]proto.Message{row.ProtoReflect().Descriptor().FullName(): {"buyer": row}},
	}
}

func buyTag(tag string, maxTech d.TechLevel) *d.StockGeneratorAny {
	return &d.StockGeneratorAny{Value: &d.StockGeneratorAny_StockGenerator_BuyTradeTag{StockGenerator_BuyTradeTag: &d.StockGenerator_BuyTradeTag{Tag: tag, MaxTechLevelBuy: maxTech}}}
}

// A kind buys what its buy generators name, within their tech ceiling, and
// never a good the player cannot sell.
func TestTraderKindBuys(t *testing.T) {
	catalog := buyCatalog(buyTag("Art", d.TechLevel_TECH_LEVEL_UNDEFINED))
	for def, want := range map[policy.Resource]bool{"Jewel": true, "Gun": false, "Secret": false} {
		if got, known := catalog.TraderKindBuys("buyer", def).Value(); !known || got != want {
			t.Errorf("%s: got %v known %v want %v", def, got, known, want)
		}
	}
	if _, known := catalog.TraderKindBuys("nobody", "Jewel").Value(); known {
		t.Error("an unread kind is known")
	}
	if _, known := catalog.TraderKindBuys("buyer", "Missing").Value(); known {
		t.Error("an unread def is known")
	}
	// A tech ceiling below the good's level stops the buy.
	capped := buyCatalog(buyTag("Art", d.TechLevel_TECH_LEVEL_NEOLITHIC))
	if got, known := capped.TraderKindBuys("buyer", "Jewel").Value(); !known || got {
		t.Error("a tech ceiling below the good was ignored")
	}
}

func TestTraderKindSilver(t *testing.T) {
	silver := &d.StockGeneratorAny{Value: &d.StockGeneratorAny_StockGenerator_SingleDef{StockGenerator_SingleDef: &d.StockGenerator_SingleDef{ThingDef: "Silver", CountRange: &d.IntRange{Min: 800, Max: 2000}}}}
	if got, known := buyCatalog(silver).TraderKindSilver("buyer").Value(); !known || got != 800 {
		t.Errorf("silver = %v known %v", got, known)
	}
	if _, known := buyCatalog(buyTag("Art", 0)).TraderKindSilver("buyer").Value(); known {
		t.Error("a kind with no currency row has a cash cap")
	}
}
