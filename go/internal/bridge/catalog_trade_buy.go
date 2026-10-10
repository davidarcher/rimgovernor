package bridge

import (
	"slices"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// TraderKindBuys reads buy-generator compatibility: whether a trader of kind
// will buy def from the colony, as its StockGenerator_BuyTradeTag,
// _BuySingleDef and _BuyExpensiveSimple rows handle it. It reads neither the
// trader's cash nor its price. Unknown for an unread kind or def and for a
// generator form it cannot classify; a def the player cannot sell is a known
// no.
func (catalog *DefinitionCatalog) TraderKindBuys(kind string, def policy.Resource) domain.Fact[bool] {
	row := DefRow[*d.TraderKindDef](catalog, kind)
	thing := catalog.ThingDefs[string(def)]
	if row == nil || thing == nil {
		return domain.Unknown[bool]()
	}
	if sale := thing.Tradeability; sale != d.Tradeability_TRADEABILITY_SELLABLE && sale != d.Tradeability_TRADEABILITY_ALL {
		return domain.Known(false)
	}
	unknown := false
	for _, opt := range row.StockGenerators {
		any := opt.GetValue()
		if any == nil {
			unknown = true
			continue
		}
		ref := any.ProtoReflect()
		selected := ref.WhichOneof(ref.Descriptor().Oneofs().Get(0))
		if selected == nil {
			unknown = true
			continue
		}
		class := string(selected.Name())
		if !strings.Contains(class, "_Buy") {
			continue
		}
		gen := ref.Get(selected).Message()
		// An unset maxTechLevelBuy is the game's default: no ceiling.
		if tech := gen.Descriptor().Fields().ByName("maxTechLevelBuy"); tech != nil && gen.Has(tech) && int32(thing.TechLevel) > int32(gen.Get(tech).Enum()) {
			continue
		}
		switch class {
		case "StockGenerator_BuyTradeTag":
			if slices.Contains(thing.TradeTags, tradeGenString(gen, "tag")) {
				return domain.Known(true)
			}
		case "StockGenerator_BuySingleDef":
			if tradeGenString(gen, "thingDef") == string(def) {
				return domain.Known(true)
			}
		case "StockGenerator_BuyExpensiveSimple":
			items, err := catalog.ItemFacts()
			value, priced := items.Market[def]
			// A def with no base market value (a stuffed good) does not qualify
			// by value: it is bought only through a tag or a def row.
			if err != nil || !priced {
				continue
			}
			if floor := gen.Descriptor().Fields().ByName("minValuePerUnit"); floor != nil && value >= gen.Get(floor).Float() {
				return domain.Known(true)
			}
		case "StockGenerator_BuySlaves":
		default:
			unknown = true
		}
	}
	if unknown {
		return domain.Unknown[bool]()
	}
	return domain.Known(false)
}

// TraderKindSilver is the low end of the silver a trader of kind carries: the
// countRange minimum of its currency stock generator. Unknown when the kind
// has no currency row or the catalog has no currency.
func (catalog *DefinitionCatalog) TraderKindSilver(kind string) domain.Fact[int64] {
	row := DefRow[*d.TraderKindDef](catalog, kind)
	if row == nil || catalog.Derived.CurrencyDef == "" {
		return domain.Unknown[int64]()
	}
	var total int64
	found := false
	for _, opt := range row.StockGenerators {
		single := opt.GetValue().GetStockGenerator_SingleDef()
		if single == nil || single.ThingDef != catalog.Derived.CurrencyDef || single.CountRange == nil {
			continue
		}
		total += int64(single.CountRange.Min)
		found = true
	}
	if !found {
		return domain.Unknown[int64]()
	}
	return domain.Known(total)
}
