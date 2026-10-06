package domain

// Stockpile role keys (#724). A planner claims the zones it creates by role;
// parameterized roles append ":<key>".
const (
	// GeneralRole is the warehouse: the roofed general store (#1770).
	GeneralRole           = "general"
	IngredientsPrefix     = "ingredients:"
	MealsRolePrefix       = "meals:"
	RawFoodRolePrefix     = "rawfood:"
	RawMeatRolePrefix     = "rawmeat:"
	RawVegRolePrefix      = "rawveg:"
	CorpsesRolePrefix     = "corpses:"
	PerishablesRolePrefix = "perishables:"
	TombRolePrefix        = "tomb:"
	MorgueRolePrefix      = "morgue:"
	YardRole              = "yard" // the materials yard (#1771): unroofed, Low priority
	MedicineRolePrefix    = "medicine:"
	ArmoryRolePrefix      = "armory:"   // the armory (#1774): weapons and armor, filling its room
	WardrobeRolePrefix    = "wardrobe:" // the wardrobe (#1774): clothing, filling its room
	// DumpRole is the waste yard's one dump zone, a Sanitation store.
	DumpRole = "wastedump"
	// IncineratorRole is the walled incinerator's zone (#1814), a Sanitation store.
	IncineratorRole = "incinerator"
	// FoodRole is the opening food stockpile: Preferred, so an indoor food
	// zone above it draws the food in once one stands.
	FoodRole = "food"
)

// Gear stockpiles keep serviceable gear only: at least half its hit points
// and Normal quality. What falls below is burnable waste or sale gear.
const GearHitPointFloor = 0.5

var GearQualityFloor Quality = "Normal"

// The selector names below are RimWorld Core ThingCategoryDef and
// SpecialThingFilterDef defNames (Data/Core/Defs/ThingCategoryDefs,
// Misc/SpecialThingFilterDefs/SpecialThingFilters.xml). A "nothing" base
// clears every item but leaves every special filter allowed, so special
// filters only ever appear as disallows here.

func mustFilter(f StockpileFilter, err error) StockpileFilter {
	if err != nil {
		panic(err)
	}
	return f
}

func gearFilter(allow []FilterSelector, disallow ...FilterSelector) (StockpileFilter, error) {
	f, err := NewStockpileFilter(BaseNothing, allow, disallow)
	if err == nil {
		f, err = f.WithHitPoints(GearHitPointFloor, 1)
	}
	if err == nil {
		f, err = f.WithQuality(GearQualityFloor, "Legendary")
	}
	return f, err
}

// IsArmoryFilter and IsWardrobeFilter recognise the gear stores' filters by
// their shape (a gear floor over weapons, or over apparel without them).
func IsArmoryFilter(f StockpileFilter) bool {
	return f.hasHitPoints && f.hasQuality && f.base == BaseNothing && hasSelector(f.Allow(), CategoryDef("Weapons")) && !hasSelector(f.Allow(), CategoryDef("Apparel"))
}

func IsWardrobeFilter(f StockpileFilter) bool {
	return f.hasHitPoints && f.hasQuality && f.base == BaseNothing && hasSelector(f.Allow(), CategoryDef("Apparel")) && !hasSelector(f.Allow(), CategoryDef("Weapons"))
}

func hasSelector(rows []FilterSelector, s FilterSelector) bool {
	for _, r := range rows {
		if r == s {
			return true
		}
	}
	return false
}

// ArmoryFilter is usable weapons and armor: no biocoded weapons or apparel,
// no tainted (dead man's) apparel, nothing burnable (the sell and burn
// boundary, #2176), hit points and quality above the gear
// floors. armor names the apparel defs that count as armor (the catalog's
// ItemFacts.Armor); every other apparel def is the wardrobe's.
func ArmoryFilter(armor []string) (StockpileFilter, error) {
	allow := []FilterSelector{CategoryDef("Weapons")}
	for _, def := range armor {
		allow = append(allow, ThingDef(def))
	}
	return gearFilter(allow, SpecialFilter("AllowBiocodedWeapons"), SpecialFilter("AllowBiocodedApparel"), SpecialFilter("AllowDeadmansApparel"), SpecialFilter(BurnableFilterDef))
}

// WardrobeFilter is wearable clothing: all apparel but the armor defs, no
// tainted or biocoded pieces, hit points and quality above the gear floors.
func WardrobeFilter(armor []string) (StockpileFilter, error) {
	disallow := []FilterSelector{SpecialFilter("AllowDeadmansApparel"), SpecialFilter("AllowBiocodedApparel"), SpecialFilter(BurnableFilterDef)}
	for _, def := range armor {
		disallow = append(disallow, ThingDef(def))
	}
	return gearFilter([]FilterSelector{CategoryDef("Apparel")}, disallow...)
}

// DumpFilter is the waste yard dump's filter: all storable items except what
// the native rule calls not burnable, so the burnable waste lies there for the
// incinerator and the rest stays in the warehouse. It is the incinerator's
// filter at Low priority.
func DumpFilter() StockpileFilter { return IncineratorFilter() }

// IncineratorFilter is what the incinerator takes: everything the mod's native
// burnable rule does not refuse (NotBurnableFilterDef). Burnable is defined
// once, natively: rottables past Fresh except colonist and slave corpses, and
// gear below the static silver cutoff. The zone outranks the Low dump, so
// waste hauls in from it.
func IncineratorFilter() StockpileFilter {
	return mustFilter(NewStockpileFilter(BaseEverything, nil, []FilterSelector{SpecialFilter(NotBurnableFilterDef)}))
}
