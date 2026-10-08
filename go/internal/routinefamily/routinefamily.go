// Package routinefamily names the routine planner families `rimgovernor
// serve` composes. Each family is one identifier, so a case that asks for a
// family the binary no longer has fails to compile instead of failing the
// service's startup (the haul family outlived its removal that way, #1802).
package routinefamily

// Family is the name of one routine planner family, as the serve process
// reads it from RIMGOVERNOR_ROUTINE_FAMILIES.
type Family string

var all []Family

// All lists every family, in declaration order.
func All() []Family { return append([]Family(nil), all...) }

func define(name string) Family {
	f := Family(name)
	all = append(all, f)
	return f
}

// Join renders families the way RIMGOVERNOR_ROUTINE_FAMILIES lists them.
func Join(families []Family) string {
	var out []byte
	for i, f := range families {
		if i > 0 {
			out = append(out, ',')
		}
		out = append(out, f...)
	}
	return string(out)
}

var (
	Sleeping            = define("sleeping")
	Bill                = define("bill")
	Field               = define("field")
	Acquisition         = define("acquisition")
	Work                = define("work")
	Supply              = define("supply")
	Cooking             = define("cooking")
	Shelter             = define("shelter")
	Comfort             = define("comfort")
	Workshop            = define("workshop")
	Research            = define("research")
	Hospital            = define("hospital")
	Expansion           = define("expansion")
	Temperature         = define("temperature")
	Power               = define("power")
	Defense             = define("defense")
	Tend                = define("tend")
	Rescue              = define("rescue")
	Equip               = define("equip")
	Repair              = define("repair")
	Fire                = define("fire")
	Clean               = define("clean")
	Burial              = define("burial")
	Incineration        = define("incineration")
	Blight              = define("blight")
	Pollution           = define("pollution")
	Mechcharger         = define("mechcharger")
	Genebank            = define("genebank")
	Armory              = define("armory")
	Clearance           = define("clearance")
	Shrine              = define("shrine")
	Stockpiles          = define("stockpiles")
	Mood                = define("mood")
	Gear                = define("gear")
	Medical             = define("medical")
	FoodStorageUpkeep   = define("food-storage-upkeep")
	Refrigeration       = define("refrigeration")
	Lighting            = define("lighting")
	Art                 = define("art")
	Mechs               = define("mechs")
	Flooring            = define("flooring")
	Routes              = define("routes")
	AnimalContainment   = define("animal-containment")
	Recovery            = define("recovery")
	Husbandry           = define("husbandry")
	Rules               = define("rules")
	PrisonerInteraction = define("prisoner-interaction")
	PopulationCustody   = define("population-custody")
	PopulationJoiner    = define("population-joiner")
	HomeCoverage        = define("home-coverage")
	Sheltering          = define("sheltering")
	Firebreak           = define("firebreak")
	Psylink             = define("psylink")
	Creepjoiner         = define("creepjoiner")
	Permits             = define("permits")
	IdeoRoles           = define("ideo-roles")
	Ideoligion          = define("ideoligion-reform")
	Rituals             = define("rituals")
	StoneShell          = define("stone-shell")
	DefensiveLayout     = define("defensive-layout")
	Dialog              = define("dialog")
	Trade               = define("trade")
	Resource            = define("resource")
	AnimalFeed          = define("animal-feed")
)
