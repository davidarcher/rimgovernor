package policy

import (
	"slices"

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

// ClothingHorizonDays is how far ahead a wearing garment's replacement is
// wanted: replacement is gradual, so a field that delivers within it (cotton
// grows in about a week) serves the demand as well as a hunt.
const ClothingHorizonDays = 15.0

// ClothingDemandInput is what the clothing runway is computed from: the
// allowed garments, each stuff's categories, the gear census (worn garments
// from the loadout models, the garments in storage) and the stock.
type ClothingDemandInput struct {
	Garments   []ClothingGarment
	Categories map[Resource][]string
	Gear       domain.Fact[GearObservation]
	Stock      StockReader
}

// ClothingRunway is the material the worn garments about to wear out take to
// replace: Needs the stock levels MaintainResource must reach, and Serves each
// stuff that may serve a demanded stuff (the loadout stuff and the stuffs
// sharing a catalog category with it, gearFilter's rule) mapped to the
// cheapest member the demand names.
type ClothingRunway struct {
	Needs  map[Resource]int64
	Serves map[Resource]Resource
}

// ClothingRunway is PlanClothingRunway of the review's facts.
func (f RoundsFacts) ClothingRunway() ClothingRunway {
	return PlanClothingRunway(ClothingDemandInput{Garments: f.Garments, Categories: f.Items.StuffCategories, Gear: f.Gear, Stock: StockReader{Resources: f.Resources, Wood: f.Wood}})
}

// TatteredWithin reports a worn option at or under the tattered threshold
// now, or projected to cross it within days: it loses WearPerDay hit points a
// day of its MaxHitPoints. An option without a wear rate or hit points does
// not wear.
func (o GearOption) TatteredWithin(days float64) bool {
	if o.Condition <= GearTatteredCondition {
		return true
	}
	if !(o.MaxHitPoints > 0) || !(o.WearPerDay > 0) {
		return false
	}
	return (o.Condition-GearTatteredCondition)*o.MaxHitPoints/o.WearPerDay <= days
}

// PlanClothingRunway is the replacement demand of every worn garment that
// is tattered or projected to be within ClothingHorizonDays: one garment's
// recipe slots at the cheapest member of its filter (OpenBillDemand's rule),
// less the serviceable stored garments covering the same core group. A
// garment without a recipe, a pawn without a loadout model or an unread
// census asks for nothing. The demand is an absolute stock level, counted
// only where the stock falls short.
func PlanClothingRunway(in ClothingDemandInput) ClothingRunway {
	bills, _ := clothingReplacements(in)
	out := ClothingRunway{}
	for resource, n := range OpenBillDemand(bills, in.Stock) {
		if out.Needs == nil {
			out.Needs, out.Serves = map[Resource]int64{}, map[Resource]Resource{}
		}
		out.Needs[resource] = n
		out.Serves[resource] = resource
	}
	for _, bill := range bills {
		for _, pick := range bill.picks() {
			if _, demanded := out.Needs[pick.Resource]; !demanded {
				continue
			}
			for _, m := range bill.Filter {
				member := Resource(m)
				if _, set := out.Serves[member]; !set && sharesCategory(in.Categories, member, pick.Resource) {
					out.Serves[member] = pick.Resource
				}
			}
		}
	}
	return out
}

// clothingReplacements walks the worn garments about to wear out: the bills
// for those no serviceable stored garment covers, and per definition the
// serviceable stored garments that cover the rest (the spare reserve a sale
// must leave in storage).
func clothingReplacements(in ClothingDemandInput) (bills []OpenBill, reserve map[Resource]int) {
	gear, known := in.Gear.Value()
	if !known {
		return nil, nil
	}
	spare, reserve := map[Resource]int{}, map[Resource]int{}
	if stored, ok := gear.Stored.Value(); ok {
		for _, s := range stored {
			if s.Serviceable() {
				spare[s.Definition] += s.Count
			}
		}
	}
	for _, pawn := range gear.Pawns {
		model, ok := pawn.LoadoutModel.Value()
		if !ok {
			continue
		}
		for _, worn := range model.Worn {
			if !worn.TatteredWithin(ClothingHorizonDays) {
				continue
			}
			g, ok := garmentOf(in.Garments, worn.Definition)
			if !ok {
				continue
			}
			if taken, covered := spareGarment(in.Garments, g, spare); covered {
				reserve[taken]++
				continue
			}
			filter, ok := gearFilter(g.Slots, worn.Stuff, in.Categories)
			if !ok {
				continue
			}
			bill := OpenBill{Count: 1, Slots: domain.Known(g.Slots)}
			for _, r := range filter {
				bill.Filter = append(bill.Filter, string(r))
			}
			bills = append(bills, bill)
		}
	}
	return bills, reserve
}

// ClothingSpareReserve is, per definition, the serviceable stored garments
// PlanClothingRunway counts as the replacement of a worn garment about to
// wear out: spare the colony keeps for the runway and never sells.
func ClothingSpareReserve(in ClothingDemandInput) map[Resource]int {
	_, reserve := clothingReplacements(in)
	return reserve
}

// garmentOf is the allowed garment of definition.
func garmentOf(garments []ClothingGarment, definition Resource) (ClothingGarment, bool) {
	i := slices.IndexFunc(garments, func(g ClothingGarment) bool { return g.Definition == definition })
	if i < 0 {
		return ClothingGarment{}, false
	}
	return garments[i], true
}

// spareGarment consumes one serviceable stored garment covering a core group
// g covers, returning its definition and whether one was left.
func spareGarment(garments []ClothingGarment, g ClothingGarment, spare map[Resource]int) (Resource, bool) {
	for _, c := range garments {
		if spare[c.Definition] > 0 && slices.ContainsFunc(g.Groups, func(group string) bool {
			return slices.Contains(GearCoreGroups, group) && slices.Contains(c.Groups, group)
		}) {
			spare[c.Definition]--
			return c.Definition, true
		}
	}
	return "", false
}

// sharesCategory reports two stuffs that are the same or share a catalog
// category.
func sharesCategory(categories map[Resource][]string, a, b Resource) bool {
	return a == b || slices.ContainsFunc(categories[a], func(c string) bool { return slices.Contains(categories[b], c) })
}

// ResourceSourceFor is the census row as the acquisition candidate of
// resource prices it, false when it does not serve it. A row serves a
// resource it names or one serves (ClothingRunway.Serves) maps it to; a hunt
// the stuffs its animal's butchery yields (a deer's leather), at the leather's
// units: the hunt is one candidate whatever else it yields, its labor charged
// once.
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
