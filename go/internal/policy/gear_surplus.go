package policy

import (
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// GearSurplus is the one definition of the gear the colony holds beyond its
// demand, the gear planners' own figures subtracted from what lies in storage:
// the garments no loadout, replacement need or tattered-garment runway claims
// and the loose weapons no colonist is or will be assigned. It is what a sale
// may take while the colony is short of silver; the best of a kind stays for
// the loadouts, the cheapest surplus is sold first (SaleGearSurplus).
type GearSurplus struct {
	// Apparel are the serviceable stored garment kinds above demand, each row
	// a (definition, stuff, quality, hit-point band) with its surplus count.
	Apparel []GearStock
	// Weapons are the loose weapon counts above the armory's demand, per
	// definition (ArmorySpareWeapons).
	Weapons map[Resource]int
}

// Empty reports no surplus.
func (s GearSurplus) Empty() bool {
	return len(s.Apparel) == 0 && len(s.Weapons) == 0
}

// ApparelSurplus is the serviceable stored apparel above demand. Demand is
// the gear planners' own: the stored garments the loadout model assigns to a
// gap (unassignedGearStock), the replacements the census still asks for
// (GearPawn.Replacements, best quality first) and the spare the clothing
// runway keeps for the worn garments about to wear out (ClothingSpareReserve).
// known is false while the census, the storage rows or any replacement is
// unread, or the loadout model refuses a pawn: nothing is then surplus.
func ApparelSurplus(in ClothingDemandInput) (rows []GearStock, known bool) {
	gear, ok := in.Gear.Value()
	if !ok {
		return nil, false
	}
	stored, ok := gear.Stored.Value()
	if !ok {
		return nil, false
	}
	review, err := ReviewGear(in.Gear)
	if err != nil {
		return nil, false
	}
	left := []GearStock{}
	for _, s := range unassignedGearStock(stored, review.Loadouts) {
		if s.Serviceable() && s.Count > 0 {
			left = append(left, s)
		}
	}
	// The best of a kind is claimed first, so the cheapest is what remains.
	sort.SliceStable(left, func(i, j int) bool {
		a, b := left[i], left[j]
		if a.Definition != b.Definition {
			return a.Definition < b.Definition
		}
		if a.Stuff != b.Stuff {
			return a.Stuff < b.Stuff
		}
		if a.Quality != b.Quality {
			return a.Quality > b.Quality
		}
		return a.HPBand > b.HPBand
	})
	claim := func(definition, stuff Resource) bool {
		for i := range left {
			row := &left[i]
			if row.Count > 0 && row.Definition == definition && (stuff == "" || row.Stuff == stuff) {
				row.Count--
				return true
			}
		}
		return false
	}
	for _, pawn := range gear.Pawns {
		replacements, ok := pawn.Replacements.Value()
		if !ok {
			return nil, false
		}
		for _, n := range replacements {
			claim(n.Definition, n.Stuff)
		}
	}
	reserve := ClothingSpareReserve(in)
	definitions := make([]Resource, 0, len(reserve))
	for definition := range reserve {
		definitions = append(definitions, definition)
	}
	sort.Slice(definitions, func(i, j int) bool { return definitions[i] < definitions[j] })
	for _, definition := range definitions {
		for i := 0; i < reserve[definition]; i++ {
			claim(definition, "")
		}
	}
	for _, row := range left {
		if row.Count > 0 {
			rows = append(rows, row)
		}
	}
	return rows, true
}

// SpareWeaponCounts is the loose weapons above the armory's demand, per
// definition: those the colony holds less the weapons AssignEquip hands a
// colonist and the ones an armed fighter's upgrade (ArmoryWeaponDemand's loose
// accounting) will take. A makeshift or biocoded thing is never counted.
func SpareWeaponCounts(tier ArmoryTier, pawns []EquipCandidatePawn, primaries map[domain.PawnID]ArmoryPrimary, weapons []EquipCandidateWeapon, recipes []GearRecipe, products map[Resource]WeaponDef) map[Resource]int {
	_, _, spare := armoryWeaponAccounting(tier, pawns, primaries, weapons, recipes, products)
	out := map[Resource]int{}
	for _, w := range weapons {
		if w.Class == WeaponMakeshift || w.Biocoded {
			delete(spare, Resource(w.Definition))
		}
	}
	for definition, n := range spare {
		if n > 0 {
			out[definition] = n
		}
	}
	return out
}

// SaleGearSurplus is the surplus gear sale: the sheet rows to sell, by thing
// id, cheapest first, until their prices cover the silver gap. Apparel rows
// must lie in a warehouse zone and match a surplus kind (definition, stuff,
// quality, hit-point band); a weapon row matches a surplus definition. The
// caller offers it only while the gap is open.
func SaleGearSurplus(rows []TradeSheetRowFact, warehouses map[string]bool, surplus GearSurplus, gap float64) map[string]bool {
	out := map[string]bool{}
	if surplus.Empty() || !(gap > 0) {
		return out
	}
	apparel := map[GearStock]int{}
	for _, s := range surplus.Apparel {
		key := s
		key.Count = 0
		apparel[key] += s.Count
	}
	weapons := map[Resource]int{}
	for definition, n := range surplus.Weapons {
		weapons[definition] = n
	}
	candidates := []TradeSheetRowFact{}
	for _, row := range rows {
		if row.ThingID == "" || row.Pawn || !row.SellPriceKnown || !finite(row.SellPrice) || row.SellPrice <= 0 {
			continue
		}
		candidates = append(candidates, row)
	}
	sort.SliceStable(candidates, func(i, j int) bool {
		if candidates[i].SellPrice != candidates[j].SellPrice {
			return candidates[i].SellPrice < candidates[j].SellPrice
		}
		return candidates[i].ThingID < candidates[j].ThingID
	})
	covered := 0.0
	for _, row := range candidates {
		if covered >= gap {
			break
		}
		if weapons[Resource(row.DefName)] > 0 {
			weapons[Resource(row.DefName)]--
		} else if key, ok := apparelKey(row, warehouses); ok && apparel[key] > 0 {
			apparel[key]--
		} else {
			continue
		}
		out[row.ThingID] = true
		covered += row.SellPrice
	}
	return out
}

// apparelKey is the surplus kind of a warehouse gear row.
func apparelKey(row TradeSheetRowFact, warehouses map[string]bool) (GearStock, bool) {
	if !warehouses[row.ZoneID] || !row.HitPointsKnown || !row.QualityKnown {
		return GearStock{}, false
	}
	return GearStock{Definition: Resource(row.DefName), Stuff: Resource(row.Stuff), Quality: int(row.Quality), HPBand: min(9, int(math.Floor(row.HitPoints*10)))}, true
}
