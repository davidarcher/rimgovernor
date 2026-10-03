package policy

import (
	"fmt"
	"slices"
	"sort"
)

// ItemFacts are the game's item numbers from the definition catalog (#1734),
// read once per load: planners look prices, nutrition, potency and stuff
// factors up here instead of carrying tables of their own. The zero value is
// a frame without a catalog; every lookup on it is an error and the planners
// that need one wait.
type ItemFacts struct {
	// Market and Nutrition are the game's MarketValue and Nutrition stat of
	// every item def that shows one.
	Market, Nutrition map[Resource]float64
	// MedicalPotency is the game's MedicalPotency of every medicine def.
	MedicalPotency map[Resource]float64
	// StuffBeauty is each stuff's Beauty stat factor (stuffProps.statFactors,
	// 1 when absent); the stuff's own statBases Beauty is never it.
	StuffBeauty map[Resource]float64
	// StuffCategories are each stuff's categories; AcceptedStuff each
	// stuff-made def's accepted stuff categories.
	StuffCategories, AcceptedStuff map[Resource][]string
	// Categories are every item def's thing categories.
	Categories map[Resource][]string
}

// MarketValue is def's market value.
func (i ItemFacts) MarketValue(def Resource) (float64, error) {
	v, ok := i.Market[def]
	if !ok {
		return 0, fmt.Errorf("the catalog has no market value for %s", def)
	}
	return v, nil
}

// StuffScore ranks a stuff by beauty factor times market value: the better
// the stuff for a room's beauty and wealth, the higher.
func (i ItemFacts) StuffScore(stuff Resource) (float64, error) {
	beauty, ok := i.StuffBeauty[stuff]
	if !ok {
		return 0, fmt.Errorf("the catalog has no stuff %s", stuff)
	}
	value, err := i.MarketValue(stuff)
	return beauty * value, err
}

// StuffsFor are the stuffs def accepts, by name: those sharing a stuff
// category with it. A def not made from stuff has none.
func (i ItemFacts) StuffsFor(def Resource) []Resource {
	accepted := i.AcceptedStuff[def]
	var out []Resource
	for stuff, categories := range i.StuffCategories {
		if slices.ContainsFunc(categories, func(c string) bool { return slices.Contains(accepted, c) }) {
			out = append(out, stuff)
		}
	}
	slices.Sort(out)
	return out
}

// CheapestMedicine is the lowest-priced medicine def and its market value:
// the one the routine trade buys.
func (i ItemFacts) CheapestMedicine() (Resource, float64, error) {
	var best Resource
	var price float64
	for _, def := range i.MedicineTiers() {
		v, err := i.MarketValue(def)
		if err != nil {
			return "", 0, err
		}
		if best == "" || v < price {
			best, price = def, v
		}
	}
	if best == "" {
		return "", 0, fmt.Errorf("the catalog has no medicine")
	}
	return best, price, nil
}

// MedicineTiers are the medicine defs by ascending medical potency, ties by
// name: the herbal tier first, the best last.
func (i ItemFacts) MedicineTiers() []Resource {
	out := make([]Resource, 0, len(i.MedicalPotency))
	for def := range i.MedicalPotency {
		out = append(out, def)
	}
	sort.Slice(out, func(a, b int) bool {
		if i.MedicalPotency[out[a]] != i.MedicalPotency[out[b]] {
			return i.MedicalPotency[out[a]] < i.MedicalPotency[out[b]]
		}
		return out[a] < out[b]
	})
	return out
}

// MedicineAt is the medicine of the given potency rank (0 the lowest), the
// game's own ranking: the care categories cap a pawn's medicine by potency
// (herbal-or-worse at rank 0, normal-or-worse at rank 1). An error when the
// catalog has fewer medicines.
func (i ItemFacts) MedicineAt(rank int) (Resource, error) {
	tiers := i.MedicineTiers()
	if rank < 0 || rank >= len(tiers) {
		return "", fmt.Errorf("the catalog has no medicine of potency rank %d (%d medicines)", rank, len(tiers))
	}
	return tiers[rank], nil
}

// BestMedicine is the highest-potency medicine.
func (i ItemFacts) BestMedicine() (Resource, error) {
	return i.MedicineAt(len(i.MedicalPotency) - 1)
}

// IsMedicine reports whether def is a medicine.
func (i ItemFacts) IsMedicine(def Resource) bool {
	_, ok := i.MedicalPotency[def]
	return ok
}

// RawFoodNutritionPrice is the market value of one nutrition as the
// cheapest raw plant food: what the colony pays to buy food as ingredients.
func (i ItemFacts) RawFoodNutritionPrice() (float64, error) {
	best, found := 0.0, false
	for def, categories := range i.Categories {
		nutrition := i.Nutrition[def]
		value, priced := i.Market[def]
		if nutrition <= 0 || !priced || !slices.Contains(categories, "PlantFoodRaw") {
			continue
		}
		if price := value / nutrition; !found || price < best {
			best, found = price, true
		}
	}
	if !found {
		return 0, fmt.Errorf("the catalog has no raw plant food")
	}
	return best, nil
}
