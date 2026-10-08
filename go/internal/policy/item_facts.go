package policy

import (
	"fmt"
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
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
	// Armor are the apparel defs the apparel policy counts as armor (the
	// Soldier outfit tag without Worker), sorted: the armory's apparel. Every
	// other apparel def is clothing for the wardrobe. Empty on a frame whose
	// stat table is missing or whose catalog names no armor.
	Armor []Resource
	// Currency is the coin every price is in and the census counts: the
	// def Tradeable.IsCurrency tests (ThingDefOf.Silver).
	Currency Resource
	// Wort is what the fermenting barrel takes in and turns into beer
	// (ThingDefOf.Wort): the intermediate the beer reserve produces (#1721).
	Wort Resource
	// Drugs are the catalog's drug defs (a CompProperties_Drug with a
	// chemical) in preference order: social drugs before hard ones, then the
	// game's listOrder, then name.
	Drugs []Drug
	// Chemicals are the chemicals the drugs build tolerance of and
	// addiction to, by name.
	Chemicals map[string]Chemical
	// Prevention is the drug that makes its taker immune to diseases, nil
	// when the game has none.
	Prevention *Prevention
	// Sculptures are the art recipes (RoleSculpture), smallest first (#1721).
	Sculptures []Sculpture
	// DeepResources are the defs the game generates as deep deposits (a
	// positive ThingDef.deepCommonality): what a deep drill can yield, sorted.
	DeepResources []Resource
}

// IsDeepResource reports whether def is a deep deposit def.
func (i ItemFacts) IsDeepResource(def Resource) bool {
	return slices.Contains(i.DeepResources, def)
}

// Sculpture is one art recipe: the building it makes, the building's
// footprint, the stuff it takes and its work (the recipe's workAmount, else
// the product's WorkToMake).
type Sculpture struct {
	Recipe, Def string
	Size        domain.Cell
	Cost        int64
	Work        float64
}

// Drug is one drug def: the chemical it builds addiction to, whether the
// game files it as a social drug (IngestibleProperties.drugCategory) and
// whether it enhances combat (CompProperties_Drug.isCombatEnhancingDrug).
type Drug struct {
	Def      Resource
	Chemical string
	Social   bool
	Combat   bool
}

// Chemical is what the colony needs to know of a chemical: whether its
// addiction hediff fades on its own severity per day, which is what a
// weaning plan relies on.
type Chemical struct {
	Weanable bool
}

// Prevention is a drug whose hediff makes the taker immune to Diseases for
// Days (the hediff's disappearance time).
type Prevention struct {
	Drug     Resource
	Days     float64
	Diseases []string
}

// RecreationDrugs are the social drugs a per-pawn policy may allow for joy.
func (i ItemFacts) RecreationDrugs() []Drug {
	return i.drugsWhere(func(d Drug) bool { return d.Social })
}

// CombatDrugs are the combat-enhancing drugs, preferred first.
func (i ItemFacts) CombatDrugs() []Drug {
	return i.drugsWhere(func(d Drug) bool { return d.Combat })
}

// DrugsOf are the drugs that build chemical, preferred first.
func (i ItemFacts) DrugsOf(chemical string) []Drug {
	return i.drugsWhere(func(d Drug) bool { return d.Chemical == chemical })
}

// DependencyDrug is the drug a chemical-dependency gene's chemical is
// satisfied with: the preferred drug of the chemical.
func (i ItemFacts) DependencyDrug(chemical string) (Resource, bool) {
	drugs := i.DrugsOf(chemical)
	if len(drugs) == 0 {
		return "", false
	}
	return drugs[0].Def, true
}

// DrugChemicals are the chemicals some drug builds, by name.
func (i ItemFacts) DrugChemicals() []string {
	var out []string
	for _, d := range i.Drugs {
		if !slices.Contains(out, d.Chemical) {
			out = append(out, d.Chemical)
		}
	}
	slices.Sort(out)
	return out
}

func (i ItemFacts) drugsWhere(keep func(Drug) bool) []Drug {
	var out []Drug
	for _, d := range i.Drugs {
		if keep(d) {
			out = append(out, d)
		}
	}
	return out
}

// stoneBlocksCategory is the thing category the game files every stone
// block def under.
const stoneBlocksCategory = "StoneBlocks"

// IsStoneBlocks reports whether def is a stone block def: one filed under
// the StoneBlocks thing category.
func (i ItemFacts) IsStoneBlocks(def Resource) bool {
	return slices.Contains(i.Categories[def], stoneBlocksCategory)
}

// StoneBlock is the stone block def a stone build uses: the one stock holds
// most of, ties by name (so the first by name when stock holds none). A
// catalog with no stone block def is an error.
func (i ItemFacts) StoneBlock(stock map[Resource]int64) (Resource, error) {
	best, found := Resource(""), false
	for def := range i.Categories {
		if !i.IsStoneBlocks(def) {
			continue
		}
		if !found || stock[def] > stock[best] || stock[def] == stock[best] && def < best {
			best, found = def, true
		}
	}
	if !found {
		return "", fmt.Errorf("the catalog has no def in thing category %s", stoneBlocksCategory)
	}
	return best, nil
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
