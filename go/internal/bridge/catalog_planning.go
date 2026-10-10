package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"math"
	"slices"

	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	"google.golang.org/protobuf/reflect/protoreflect"
)

// The StatDefs the planning views read besides the floor stats.
const (
	StatMaxHitPoints         = "MaxHitPoints"
	StatBedRestEffectiveness = "BedRestEffectiveness"
	StatWorkToBuild          = "WorkToBuild"
	StatDoorOpenSpeed        = "DoorOpenSpeed"
)

// The CLR classes the planning views match by base class, never by name.
const (
	ClassPowerComp             = "RimWorld.CompProperties_Power"
	ClassGlowerComp            = "RimWorld.CompProperties_Glower"
	ClassExplosiveComp         = "RimWorld.CompProperties_Explosive"
	ClassToxifierComp          = "RimWorld.CompProperties_Toxifier"
	ClassPolluteComp           = "RimWorld.CompProperties_PolluteOverTime"
	ClassWasteComp             = "RimWorld.CompProperties_WasteProducer"
	ClassDeathrestComp         = "RimWorld.CompProperties_DeathrestBindable"
	ClassMechChargerThng       = "RimWorld.Building_MechCharger"
	ClassGenepackContainerComp = "RimWorld.CompProperties_GenepackContainer"
	ClassBedThing              = "RimWorld.Building_Bed"
)

// Buildable is ThingDef.BuildableByPlayer: the def has a designation category.
func Buildable(row *d.ThingDef) bool { return row.GetDesignationCategory() != "" }

// TerrainBuildable is BuildableDef.BuildableByPlayer of a TerrainDef.
func TerrainBuildable(row *d.TerrainDef) bool { return row.GetDesignationCategory() != "" }

// IsFood is whether the def is a food a policy can allow (a nutrition-giving
// ingestible that is no drug and no corpse), from the game-computed flags.
func (catalog *DefinitionCatalog) IsFood(name string) (bool, error) {
	row, err := catalog.thingFactsRow(name)
	return row.FoodKind != nil, err
}

// GameRoomRoles are the furniture roles the game's room-role code scores the
// def for by name (Toy, Decoration, Board, Desk): at most one.
func GameRoomRoles(name string) []string {
	if role, ok := policy.GameRoomRoleDefs[name]; ok {
		return []string{string(role)}
	}
	return nil
}

// CompOf is the first comp of row whose class is base or derives from it (the
// game's GetCompProperties<T>), nil when the def has none. Comps are the
// oneof messages of CompPropertiesAny, matched by the class chains the
// catalog carries.
func (catalog *DefinitionCatalog) CompOf(row *d.ThingDef, base string) (protoreflect.Message, error) {
	for _, comp := range row.GetComps() {
		msg := comp.GetValue().ProtoReflect()
		which := msg.WhichOneof(msg.Descriptor().Oneofs().ByName("value"))
		if which == nil {
			continue
		}
		sub := msg.Get(which).Message()
		match, err := catalog.RowIsA(sub.Interface(), base)
		if err != nil {
			return nil, err
		}
		if match {
			return sub, nil
		}
	}
	return nil, nil
}

// CompFloat is the named float field of a comp message; a message without
// the field is a contract error.
func CompFloat(comp protoreflect.Message, field string) (float64, error) {
	fd := comp.Descriptor().Fields().ByName(protoreflect.Name(field))
	if fd == nil || fd.Kind() != protoreflect.FloatKind || fd.IsList() {
		return 0, contract("comp %s has no float field %s", comp.Descriptor().FullName(), field)
	}
	return float64(comp.Get(fd).Float()), nil
}

// HasComp is whether row carries a comp of class base or a subclass.
func (catalog *DefinitionCatalog) HasComp(row *d.ThingDef, base string) (bool, error) {
	comp, err := catalog.CompOf(row, base)
	return comp != nil, err
}

// RoomRoleRows are the names of the definitions that carry a room-role
// furniture role: a role the game names (ThingDefFacts), a baby bed (a bed
// flagged bed_crib) or a deathrest-bindable building. Sorted.
func (catalog *DefinitionCatalog) RoomRoleRows() ([]string, error) {
	if catalog == nil {
		return nil, nil
	}
	var names []string
	for name, row := range catalog.ThingDefs {
		if !Buildable(row) {
			continue
		}
		roles := GameRoomRoles(name)
		rest, err := catalog.HasComp(row, ClassDeathrestComp)
		if err != nil {
			return nil, err
		}
		if len(roles) > 0 || row.GetBuilding().GetBedCrib() || rest {
			names = append(names, name)
		}
	}
	slices.Sort(names)
	return names, nil
}

// PowerDraw is the base consumption (negative when it generates) of the def's
// power comp (CompProperties_Power or a subclass such as a battery), false
// when the def has none. It is the def's static draw: the research upgrade
// factors a finished project applies are the game's to state, not the row's.
func (catalog *DefinitionCatalog) PowerDraw(row *d.ThingDef) (watts float64, ok bool, err error) {
	comp, err := catalog.CompOf(row, ClassPowerComp)
	if comp == nil || err != nil {
		return 0, false, err
	}
	watts, err = CompFloat(comp, "basePowerConsumption")
	return watts, err == nil, err
}

// CostValue is the market value of def's adjusted cost list at stuff (empty for
// a def not made from stuff).
func (catalog *DefinitionCatalog) CostValue(def, stuff string) (float64, error) {
	return catalog.costValue(def, stuff)
}

// MechChargers are the names of the Building_MechCharger defs, sorted.
func (catalog *DefinitionCatalog) MechChargers() ([]string, error) {
	var names []string
	for name, row := range catalog.ThingDefs {
		if !Buildable(row) {
			continue
		}
		charger, err := catalog.ClassIsA(row.GetThingClass(), ClassMechChargerThng)
		if err != nil {
			return nil, err
		}
		if charger {
			names = append(names, name)
		}
	}
	slices.Sort(names)
	return names, nil
}

// GeneBanks are the names of the buildable defs carrying the genepack
// container comp (the gene bank), sorted.
func (catalog *DefinitionCatalog) GeneBanks() ([]string, error) {
	var names []string
	for name, row := range catalog.ThingDefs {
		if !Buildable(row) {
			continue
		}
		bank, err := catalog.HasComp(row, ClassGenepackContainerComp)
		if err != nil {
			return nil, err
		}
		if bank {
			names = append(names, name)
		}
	}
	slices.Sort(names)
	return names, nil
}

// CheapestCostValue is the least market value of def's adjusted cost list
// over its allowed stuffs, or at no stuff for a def not made from stuff: the
// price of the cheapest way to build it, whatever is in stock.
func (catalog *DefinitionCatalog) CheapestCostValue(def string) (float64, error) {
	stuffs, err := catalog.AllowedStuffs(def)
	if err != nil {
		return 0, err
	}
	if len(stuffs) == 0 {
		return catalog.costValue(def, "")
	}
	best := math.Inf(1)
	for _, stuff := range stuffs {
		value, err := catalog.costValue(def, stuff)
		if err != nil {
			return 0, err
		}
		best = math.Min(best, value)
	}
	return best, nil
}
