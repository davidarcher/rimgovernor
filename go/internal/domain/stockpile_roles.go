package domain

// Stockpile role keys (#724). A planner claims the zones it creates by role;
// parameterized roles append ":<key>".
const (
	// GeneralRole is the warehouse: the roofed general store (#1770).
	GeneralRole = "general"
	// OpeningGeneralRole is the opening outdoor general store, deleted once
	// the warehouse stands.
	OpeningGeneralRole    = "opening_general"
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
	// ApparelRole and WeaponsRole are the retired fixed 2x2 gear zones: the
	// armory and wardrobe replaced them, and a zone still claimed under them
	// is deleted (see the role registry).
	ApparelRole    = "apparel"
	WeaponsRole    = "weapons"
	WornDumpRole   = "dump:worn"
	RottenDumpRole = "dump:rotten"
	CorpseDumpRole = "dump:corpses"
	// FreshDumpRole holds fresh animal and insect corpses for the butcher
	// while no freezer corpse shelf stands.
	FreshDumpRole = "dump:fresh"
	// IncineratorRole is the walled incinerator's zone (#1814), a Sanitation store.
	IncineratorRole = "incinerator"
	// FoodRole is the opening food stockpile: Preferred, so an indoor food
	// zone above it draws the food in once one stands.
	FoodRole = "food"
)

// Gear stockpiles keep serviceable gear only: at least half its hit points
// and Normal quality. What falls below lands in the worn dump.
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

// WornDumpFilter takes every apparel and weapon; at Low priority it only
// keeps what the gear stockpiles refuse (tainted, worn, poor, biocoded)
// to disintegrate in the weather on the unroofed dump site (#1813).
func WornDumpFilter() StockpileFilter {
	return mustFilter(NewStockpileFilter(BaseNothing, []FilterSelector{CategoryDef("Apparel"), CategoryDef("Weapons")}, nil))
}

// RottenDumpFilter takes rotten food and rotten non-human corpses only. The
// incinerator (#1814) takes the same things off it.
func RottenDumpFilter() StockpileFilter {
	return mustFilter(NewStockpileFilter(BaseNothing,
		[]FilterSelector{CategoryDef("CorpsesAnimal"), CategoryDef("CorpsesInsect"), CategoryDef("Foods")},
		[]FilterSelector{SpecialFilter("AllowFresh")}))
}

// IncineratorFilter is what the incinerator takes: everything the mod's native
// burnable rule does not refuse (NotBurnableFilterDef). Burnable is defined
// once, natively: rottables past Fresh except colonist and slave corpses, and
// gear below the static silver cutoff. The zone outranks the Low dump, so
// waste hauls in from it.
func IncineratorFilter() StockpileFilter {
	return mustFilter(NewStockpileFilter(BaseEverything, nil, []FilterSelector{SpecialFilter(NotBurnableFilterDef)}))
}

// CorpseDumpFilter takes humanlike corpses only, the ones with no better
// home yet: strangers wait for the butcher or the incinerator and colonists
// for the tomb, so none lie in a room colonists sleep or eat in. Animal and
// insect corpses are not here (#1812): fresh ones belong on the freezer
// shelf or the fresh dump, and a rotting one goes to the rotten dump.
func CorpseDumpFilter() StockpileFilter {
	return mustFilter(NewStockpileFilter(BaseNothing, []FilterSelector{CategoryDef("CorpsesHumanlike")}, nil))
}

// StockpileRoleSpec is the filter and priority a role's zone is created
// with.
type StockpileRoleSpec struct {
	Role     string
	Filter   StockpileFilter
	Priority StockpilePriority
}

// DumpRoles are the fixed-filter dump roles #724 adds, below the general
// store. The gear stores are room-bound sites of the storage planner.
func DumpRoles() []StockpileRoleSpec {
	return []StockpileRoleSpec{
		{WornDumpRole, WornDumpFilter(), LowPriority},
		{RottenDumpRole, RottenDumpFilter(), LowPriority},
		{CorpseDumpRole, CorpseDumpFilter(), LowPriority},
		{FreshDumpRole, CorpseLarderFilter(), LowPriority},
	}
}
