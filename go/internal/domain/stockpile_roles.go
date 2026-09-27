package domain

// Stockpile role keys (#724). A planner claims the zones it creates by role;
// parameterized roles append ":<key>".
const (
	GeneralRole       = "general"
	CoveredRolePrefix = "covered:"
	IngredientsPrefix = "ingredients:"
	MealsRolePrefix   = "meals:"
	RawFoodRolePrefix = "rawfood:"
	ApparelRole       = "apparel"
	WeaponsRole       = "weapons"
	WornDumpRole      = "dump:worn"
	RottenDumpRole    = "dump:rotten"
	CorpseDumpRole    = "dump:corpses"
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

func gearFilter(category string, disallow ...FilterSelector) StockpileFilter {
	f := mustFilter(NewStockpileFilter(BaseNothing, []FilterSelector{CategoryDef(category)}, disallow))
	f = mustFilter(f.WithHitPoints(GearHitPointFloor, 1))
	return mustFilter(f.WithQuality(GearQualityFloor, "Legendary"))
}

// ApparelFilter is wearable apparel: no tainted (dead man's) or biocoded
// pieces, hit points and quality above the gear floors.
func ApparelFilter() StockpileFilter {
	return gearFilter("Apparel", SpecialFilter("AllowDeadmansApparel"), SpecialFilter("AllowBiocodedApparel"))
}

// WeaponsFilter is usable weapons: no biocoded ones, hit points and quality
// above the gear floors.
func WeaponsFilter() StockpileFilter {
	return gearFilter("Weapons", SpecialFilter("AllowBiocodedWeapons"))
}

// WornDumpFilter takes every apparel and weapon; at Low priority it only
// keeps what the gear stockpiles refuse (tainted, worn, poor, biocoded)
// for burning or smelting.
func WornDumpFilter() StockpileFilter {
	return mustFilter(NewStockpileFilter(BaseNothing, []FilterSelector{CategoryDef("Apparel"), CategoryDef("Weapons")}, nil))
}

// RottenDumpFilter takes rotten food and rotten non-human corpses only.
func RottenDumpFilter() StockpileFilter {
	return mustFilter(NewStockpileFilter(BaseNothing,
		[]FilterSelector{CategoryDef("CorpsesAnimal"), CategoryDef("CorpsesInsect"), CategoryDef("Foods")},
		[]FilterSelector{SpecialFilter("AllowFresh")}))
}

// CorpseDumpFilter takes human corpses, fresh or rotten, so none lie in a
// room colonists sleep or eat in.
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

// GearAndDumpRoles are the fixed-filter roles #724 adds: the gear
// stockpiles above the general store, the dumps below it.
func GearAndDumpRoles() []StockpileRoleSpec {
	return []StockpileRoleSpec{
		{ApparelRole, ApparelFilter(), PreferredPriority},
		{WeaponsRole, WeaponsFilter(), PreferredPriority},
		{WornDumpRole, WornDumpFilter(), LowPriority},
		{RottenDumpRole, RottenDumpFilter(), LowPriority},
		{CorpseDumpRole, CorpseDumpFilter(), LowPriority},
	}
}

// NewRoleStockpileZone is spec's zone over cells, tagged with its role.
func NewRoleStockpileZone(spec StockpileRoleSpec, cells []Cell) (ZoneCreate, error) {
	z, err := NewFilteredStockpileZone(spec.Filter, spec.Priority, cells)
	if err != nil {
		return ZoneCreate{}, err
	}
	return z.WithRole(spec.Role)
}
