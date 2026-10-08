package bridge

import (
	"slices"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	"google.golang.org/protobuf/reflect/protoreflect"
)

// TraderKindCanSupply reads sell-generator compatibility, never probability,
// stock or prices. Unsupported generator forms keep a negative answer unknown.
func (catalog *DefinitionCatalog) TraderKindCanSupply(kind string, good policy.ResourceKey) domain.Fact[bool] {
	row := DefRow[*d.TraderKindDef](catalog, kind)
	if row == nil {
		return domain.Unknown[bool]()
	}
	items, err := catalog.ItemFacts()
	if err != nil {
		return domain.Unknown[bool]()
	}
	names := []string{string(good.Def)}
	if good == policy.NutritionKey {
		names = nil
		for name, nutrition := range items.Nutrition {
			if nutrition <= 0 {
				continue
			}
			if _, food, err := catalog.TradeFood(string(name)); err == nil && food {
				names = append(names, string(name))
			}
		}
		if len(names) == 0 {
			return domain.Unknown[bool]()
		}
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
		if strings.Contains(class, "_Buy") {
			continue
		}
		gen := ref.Get(selected).Message()
		switch class {
		case "StockGenerator_SingleDef", "StockGenerator_MultiDef", "StockGenerator_Tag", "StockGenerator_Category":
			for _, name := range names {
				def := catalog.ThingDefs[name]
				if def == nil {
					unknown = true
					continue
				}
				if tradeGenStrings(gen, "excludedThingDefs", name) {
					continue
				}
				count := gen.Descriptor().Fields().ByName("countRange")
				if count == nil || !gen.Has(count) {
					unknown = true
					continue
				}
				bounds := gen.Get(count).Message()
				maxField := bounds.Descriptor().Fields().ByName("max")
				if maxField == nil || bounds.Get(maxField).Int() <= 0 {
					continue
				}
				tech := gen.Descriptor().Fields().ByName("maxTechLevelGenerate")
				if tech != nil && int32(def.TechLevel) > int32(gen.Get(tech).Enum()) {
					continue
				}
				compatible := false
				switch class {
				case "StockGenerator_SingleDef":
					compatible = tradeGenString(gen, "thingDef") == name
				case "StockGenerator_MultiDef":
					compatible = tradeGenStrings(gen, "thingDefs", name)
				case "StockGenerator_Tag":
					compatible = slices.Contains(def.TradeTags, tradeGenString(gen, "tradeTag"))
				case "StockGenerator_Category":
					cats, known := items.Categories[policy.Resource(name)]
					if !known {
						unknown = true
						continue
					}
					compatible = slices.Contains(cats, tradeGenString(gen, "categoryDef"))
					for _, cat := range cats {
						if tradeGenStrings(gen, "excludedCategories", cat) {
							compatible = false
						}
					}
				}
				if compatible {
					return domain.Known(true)
				}
			}
		default:
			unknown = true
		}
	}
	if unknown {
		return domain.Unknown[bool]()
	}
	return domain.Known(false)
}
func tradeGenString(m protoreflect.Message, name protoreflect.Name) string {
	f := m.Descriptor().Fields().ByName(name)
	if f == nil {
		return ""
	}
	return m.Get(f).String()
}
func tradeGenStrings(m protoreflect.Message, field protoreflect.Name, value string) bool {
	f := m.Descriptor().Fields().ByName(field)
	if f == nil {
		return false
	}
	list := m.Get(f).List()
	for i := 0; i < list.Len(); i++ {
		if list.Get(i).String() == value {
			return true
		}
	}
	return false
}
