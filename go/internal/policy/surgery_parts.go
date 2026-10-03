package policy

import (
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// SurgeryPartBill is MaintainSurgery's bill purpose (#1168): one batch bill
// fabricating a part a restore operation lacks, at a bench whose recipe is
// researched and usable. ProductionBillContext.Parts carries the wants.
const SurgeryPartBill BillPurpose = "surgery_part"

// surgeryPartPriceCeiling bounds a part purchase's unit price. Bionics
// price near 1500 silver in vanilla; the trade's silver reserve is the
// real bound on what a purchase may spend.
const surgeryPartPriceCeiling = 5000.0

// SurgeryPart is one missing part a restore wants: the items that would
// serve it, best first, and the surgery's rank as a demand priority.
type SurgeryPart struct {
	Pawn     PawnID
	Part     int
	Items    []Resource
	Priority int
}

// SurgeryParts turns the part-short wants into part demand, highest
// priority first. Priority scales the want's value (the best part's tier
// times the body part's weight, at most 1.5) onto ResourceDemand's 1..100.
func SurgeryParts(wants []SurgeryWant) []SurgeryPart {
	var out []SurgeryPart
	for _, want := range wants {
		if want.Reason != SurgeryPartShort {
			continue
		}
		part := SurgeryPart{Pawn: want.Pawn, Part: want.Part, Priority: min(100, max(1, int(math.Ceil(want.Value/1.5*100))))}
		for _, item := range want.Items {
			if validResource(item) {
				part.Items = append(part.Items, item)
			}
		}
		if len(part.Items) > 0 {
			out = append(out, part)
		}
	}
	sort.SliceStable(out, func(i, j int) bool { return out[i].Priority > out[j].Priority })
	return out
}

// SurgeryPartDemand is the part demand as resource targets: one unit per
// part of its best item, merged by item at the highest priority.
func SurgeryPartDemand(parts []SurgeryPart) []ResourceDemand {
	var out []ResourceDemand
	index := map[Resource]int{}
	for _, part := range parts {
		item := part.Items[0]
		if i, ok := index[item]; ok {
			out[i].Count++
			out[i].Priority = max(out[i].Priority, part.Priority)
			continue
		}
		index[item] = len(out)
		out = append(out, ResourceDemand{Key: ResourceKey{Def: item}, Count: 1, Priority: part.Priority})
	}
	return out
}

// FabricableParts are the items some usable bench has a researched recipe
// for, by the recipe producing each.
func FabricableParts(benches []ProductionBench) map[Resource]bool {
	out := map[Resource]bool{}
	for _, bench := range benches {
		if usable, known := bench.Usable.Value(); !known || !usable {
			continue
		}
		for _, recipe := range bench.Recipes {
			if available, known := recipe.Available.Value(); known && available {
				for _, product := range recipe.Products {
					out[Resource(product.Name)] = true
				}
			}
		}
	}
	return out
}

// TradeSurgeryParts are the parts no item of which can be fabricated: the
// ones a trader must supply.
func TradeSurgeryParts(parts []SurgeryPart, fabricable map[Resource]bool) []SurgeryPart {
	var out []SurgeryPart
	for _, part := range parts {
		made := false
		for _, item := range part.Items {
			made = made || fabricable[item]
		}
		if !made {
			out = append(out, part)
		}
	}
	return out
}

// SurgeryTradeNeed adds the parts only a trader can supply to the trade
// need; an unknown need stays unknown.
func SurgeryTradeNeed(need domain.Fact[TradeNeed], parts []SurgeryPart) domain.Fact[TradeNeed] {
	n, known := need.Value()
	if !known || len(parts) == 0 {
		return need
	}
	n.SurgeryParts = append(append([]SurgeryPart(nil), n.SurgeryParts...), parts...)
	return domain.Known(n)
}

// selectSurgeryPartBill picks the highest-priority part's best fabricable
// item. A part whose items already have a bill on some bench is in
// production and gets no second one.
func selectSurgeryPartBill(benches []ProductionBench, parts []SurgeryPart) (BillSelection, bool) {
	billed := map[string]bool{}
	for _, bench := range benches {
		for _, bill := range bench.Bills {
			if active, known := bill.Active.Value(); !known || active {
				billed[bill.Recipe] = true
			}
		}
	}
	for _, part := range parts {
		var pick *BillSelection
		inProduction := false
		for _, item := range part.Items {
			for _, bench := range benches {
				usable, uk := bench.Usable.Value()
				token, tk := bench.Token.Value()
				for _, recipe := range bench.Recipes {
					if !producesItem(recipe, item) {
						continue
					}
					inProduction = inProduction || billed[recipe.Name]
					available, ak := recipe.Available.Value()
					if pick == nil && uk && usable && tk && foodID(token) && ak && available && len(bench.Bills) < 15 {
						pick = &BillSelection{Bench: bench.ID, Recipe: recipe.Name, Token: token, Mode: domain.GearBatch, Target: 1}
					}
				}
			}
		}
		if !inProduction && pick != nil {
			return *pick, true
		}
	}
	return BillSelection{}, false
}

func producesItem(recipe ProductionRecipe, item Resource) bool {
	for _, product := range recipe.Products {
		if Resource(product.Name) == item {
			return true
		}
	}
	return false
}
