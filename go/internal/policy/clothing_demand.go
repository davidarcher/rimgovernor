package policy

import (
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ClothingGarment is a garment the apparel policy lets the colonists wear that
// covers a core body-part group (GearCoreGroups): its groups and the native
// recipe's ingredient slots, each the stuff alternatives with their counts.
type ClothingGarment struct {
	Definition Resource
	Groups     []string
	Slots      [][]Amount
}

// ClothingDemandInput is what the standing clothing-material floor is
// computed from: the allowed garments, each stuff's categories, the colonists,
// the stock and the outfits already stored.
type ClothingDemandInput struct {
	Garments   []ClothingGarment
	Categories map[Resource][]string
	Colonists  domain.Fact[int64]
	Stock      domain.Fact[[]Amount]
	Stored     domain.Fact[[]GearStock]
}

// ClothingMaterial is one stuff category's share of the standing floor: the
// units one replacement outfit of every colonist without a stored outfit
// takes (Floor), what the category's stuffs already hold (Held), and the
// stuff (Material) the units are asked of. Members are every stuff of the
// category the outfit's recipes accept: a hunt's leather or a field's cotton
// of any member serves Material's demand.
type ClothingMaterial struct {
	Category string
	Material Resource
	Members  []Resource
	Floor    int64
	Held     int64
}

// Deficit is the units short of the floor.
func (m ClothingMaterial) Deficit() int64 { return max(0, m.Floor-m.Held) }

// ClothingMaterials is ClothingMaterials of the review's facts: the apparel
// census's stored outfits, the catalog's stuff categories and the stock.
func (f RoundsFacts) ClothingMaterials() []ClothingMaterial {
	in := ClothingDemandInput{Garments: f.Garments, Categories: f.Items.StuffCategories, Colonists: f.Colonists, Stock: f.Resources, Stored: domain.Unknown[[]GearStock]()}
	if gear, known := f.Gear.Value(); known {
		in.Stored = gear.Stored
	}
	return ClothingMaterials(in)
}

// ClothingMaterials are the floors of the colonists' replacement outfits: per
// stuff category, colonists x the units of the cheapest garment of each core
// group (a garment covering several groups once), less the colonists whose
// outfit is already stored. The cost is the recipes' own count, so it scales
// with the recipe and the colony. A category some core group cannot be made
// from has no floor, and an unknown colonist count, stock or recipe asks for
// nothing. Material is the member holding the most (then the cheapest, then
// by name).
func ClothingMaterials(in ClothingDemandInput) []ClothingMaterial {
	colonists, known := in.Colonists.Value()
	stock, stockKnown := in.Stock.Value()
	if !known || !stockKnown || colonists <= 0 {
		return nil
	}
	need := colonists - spareOutfits(in)
	if need <= 0 {
		return nil
	}
	held := map[Resource]int64{}
	for _, a := range stock {
		held[a.Resource] += a.Count
	}
	categories := map[string]bool{}
	for _, g := range in.Garments {
		for _, slot := range g.Slots {
			for _, a := range slot {
				for _, c := range in.Categories[a.Resource] {
					categories[c] = true
				}
			}
		}
	}
	names := make([]string, 0, len(categories))
	for c := range categories {
		names = append(names, c)
	}
	sort.Strings(names)
	var out []ClothingMaterial
	for _, category := range names {
		if m, ok := clothingMaterial(in, category, need, held); ok {
			out = append(out, m)
		}
	}
	return out
}

// clothingMaterial prices the outfit in one stuff category.
func clothingMaterial(in ClothingDemandInput, category string, need int64, held map[Resource]int64) (ClothingMaterial, bool) {
	outfit, ok := cheapestOutfit(in, category, 0, nil)
	if !ok {
		return ClothingMaterial{}, false
	}
	// Members are the stuffs every garment of the outfit accepts.
	var members []Resource
	for i, g := range outfit {
		accepted := stuffsOf(g, in.Categories, category)
		if i == 0 {
			members = accepted
			continue
		}
		members = slices.DeleteFunc(members, func(r Resource) bool { return !slices.Contains(accepted, r) })
	}
	if len(members) == 0 {
		return ClothingMaterial{}, false
	}
	cost := func(stuff Resource) (total int64) {
		for _, g := range outfit {
			for _, slot := range g.Slots {
				for _, a := range slot {
					if a.Resource == stuff {
						total += a.Count
					}
				}
			}
		}
		return total
	}
	m := ClothingMaterial{Category: category, Members: members}
	for _, stuff := range members {
		m.Held += held[stuff]
		if m.Material == "" || held[stuff] > held[m.Material] || held[stuff] == held[m.Material] && cost(stuff) < cost(m.Material) {
			m.Material = stuff
		}
	}
	m.Floor = need * cost(m.Material)
	return m, m.Floor > 0
}

// cheapestOutfit is the cheapest set of garments covering every core group
// from group onward, given the garments chosen so far; a garment covering
// several groups is counted once.
func cheapestOutfit(in ClothingDemandInput, category string, group int, chosen []ClothingGarment) ([]ClothingGarment, bool) {
	if group == len(GearCoreGroups) {
		return chosen, true
	}
	if slices.ContainsFunc(chosen, func(g ClothingGarment) bool { return slices.Contains(g.Groups, GearCoreGroups[group]) }) {
		return cheapestOutfit(in, category, group+1, chosen)
	}
	var best []ClothingGarment
	bestCost := int64(0)
	for _, g := range in.Garments {
		if !slices.Contains(g.Groups, GearCoreGroups[group]) {
			continue
		}
		if _, ok := garmentCost(g, in.Categories, category); !ok {
			continue
		}
		outfit, ok := cheapestOutfit(in, category, group+1, append(slices.Clone(chosen), g))
		if !ok {
			continue
		}
		var cost int64
		for _, o := range outfit {
			c, _ := garmentCost(o, in.Categories, category)
			cost += c
		}
		if best == nil || cost < bestCost {
			best, bestCost = outfit, cost
		}
	}
	return best, best != nil
}

// garmentCost is the cheapest total of the garment's slots in category's
// stuffs; false when a slot has no stuff of the category.
func garmentCost(g ClothingGarment, categories map[Resource][]string, category string) (total int64, ok bool) {
	for _, slot := range g.Slots {
		least := int64(0)
		for _, a := range slot {
			if slices.Contains(categories[a.Resource], category) && a.Count > 0 && (least == 0 || a.Count < least) {
				least = a.Count
			}
		}
		if least == 0 {
			return 0, false
		}
		total += least
	}
	return total, len(g.Slots) > 0
}

// stuffsOf are the stuffs of category the garment's recipe accepts.
func stuffsOf(g ClothingGarment, categories map[Resource][]string, category string) []Resource {
	var out []Resource
	for _, slot := range g.Slots {
		for _, a := range slot {
			if slices.Contains(categories[a.Resource], category) && !slices.Contains(out, a.Resource) {
				out = append(out, a.Resource)
			}
		}
	}
	slices.Sort(out)
	return out
}

// spareOutfits is the outfits already in storage: the fewest serviceable
// stored garments covering any core group, over the allowed garments.
func spareOutfits(in ClothingDemandInput) int64 {
	stored, known := in.Stored.Value()
	if !known {
		return 0
	}
	spare := int64(-1)
	for _, group := range GearCoreGroups {
		var n int64
		for _, s := range stored {
			if !s.Serviceable() {
				continue
			}
			if slices.ContainsFunc(in.Garments, func(g ClothingGarment) bool { return g.Definition == s.Definition && slices.Contains(g.Groups, group) }) {
				n += int64(s.Count)
			}
		}
		if spare < 0 || n < spare {
			spare = n
		}
	}
	return max(spare, 0)
}

// maxResourceTarget is the largest MaintainResource target ValidateResourceTargets takes.
const maxResourceTarget = 10000

// ClothingHorizonDays is how far ahead the outfit floor is wanted: replacement
// is gradual, so a field that delivers within it (cotton grows in about a
// week) serves the floor as well as a hunt.
const ClothingHorizonDays = 15.0

// ClothingServes maps each stuff that serves a targeted material's demand to
// that material: the targets that are a Material or a member of a category
// with a floor.
func ClothingServes(materials []ClothingMaterial, targets map[Resource]int64) map[Resource]Resource {
	out := map[Resource]Resource{}
	for _, m := range materials {
		for _, member := range m.Members {
			if _, asked := targets[member]; asked && m.Deficit() > 0 {
				for _, served := range m.Members {
					out[served] = member
				}
				break
			}
		}
	}
	return out
}

// ResourceSourceFor is the census row as the acquisition candidate of
// resource prices it, false when it does not serve it. A row serves a
// resource it names; a clothing material also takes its category's other
// stuffs, and a hunt the stuffs its animal's butchery yields (a deer's
// leather), at the leather's units: the hunt is one candidate whatever else
// it yields, its labor charged once.
func ResourceSourceFor(s AcquisitionSource, resource Resource, serves map[Resource]Resource) (AcquisitionSource, bool) {
	if Resource(s.Resource) == resource {
		return s, true
	}
	if s.Hunt {
		var units float64
		for _, p := range s.Products {
			if serves[p.Def] == resource {
				units += p.Amount
			}
		}
		s.Resource, s.Yield = string(resource), units
		return s, units > 0
	}
	if serves[Resource(s.Resource)] == resource {
		s.Resource = string(resource)
		return s, true
	}
	return AcquisitionSource{}, false
}

// FieldHarvest describes a field of a harvested stuff to sow: the cells it
// covers, the days to its first harvest, the units a cell yields, the work to
// create the zone and sow it, and the daily harvest work.
type FieldHarvest struct {
	Cells        int64
	GrowDays     float64
	UnitsPerCell float64
	SetupTicks   float64
	WorkPerDay   float64
}

// FieldHarvestCandidate is a field (cotton) as a harvest candidate: the zone
// and sowing are its upfront labor, the first harvest its lead, the cells'
// yield over the grow time its rate and the standing crop its stock cap.
func FieldHarvestCandidate(resource Resource, id string, f FieldHarvest) (SupplyCandidate, bool) {
	if f.Cells <= 0 || !(f.GrowDays > 0) || !(f.UnitsPerCell > 0) {
		return SupplyCandidate{}, false
	}
	units := float64(f.Cells) * f.UnitsPerCell
	return SupplyCandidate{
		Kind: CandidateHarvest, ID: id, State: domain.Known(CandidateClosed),
		Yields:      []CandidateYield{{Good: ResourceKey{Def: resource}, PerDay: domain.Known(units / f.GrowDays), StockCap: domain.Known(int64(units))}},
		LeadDays:    domain.Known(f.GrowDays),
		LaborPerDay: domain.Known(f.WorkPerDay),
		UpfrontCost: CandidateCost{LaborTicks: domain.Known(f.SetupTicks)},
	}, true
}

// ClothingResourceNeeds are the materials' MaintainResource floors: each
// Material's target is what it holds plus its category's deficit, so the
// ordinary deficit (target - stock) is the category's.
func ClothingResourceNeeds(materials []ClothingMaterial, stock domain.Fact[[]Amount]) map[Resource]int64 {
	rows, _ := stock.Value()
	var out map[Resource]int64
	for _, m := range materials {
		if m.Deficit() == 0 {
			continue
		}
		var have int64
		for _, a := range rows {
			if a.Resource == m.Material {
				have = a.Count
			}
		}
		if out == nil {
			out = map[Resource]int64{}
		}
		out[m.Material] = min(have+m.Deficit(), maxResourceTarget)
	}
	return out
}
