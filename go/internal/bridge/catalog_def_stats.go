package bridge

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/bridge/stateval"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// ClassChangeableProjectileComp is the comp a mortar's turret gun carries to
// take loaded shells.
const ClassChangeableProjectileComp = "RimWorld.CompProperties_ChangeableProjectile"

// StackLimit is the def's ThingDef.stackLimit: the most one stack of it
// holds, which is what one storage cell holds of it.
func (catalog *DefinitionCatalog) StackLimit(def string) (int64, error) {
	row, err := catalog.thingRow(def)
	if err != nil {
		return 0, err
	}
	return int64(row.GetStackLimit()), nil
}

// Classes the repair order of a structure is read from.
const (
	ClassTempControlComp = "RimWorld.CompTempControl"
	ClassPowerPlantComp  = "RimWorld.CompPowerPlant"
	ClassWorkTableThing  = "RimWorld.Building_WorkTable"
)

// UsesHitPoints is ThingDef.useHitPoints: the def has hit points that can be
// damaged and repaired.
func (catalog *DefinitionCatalog) UsesHitPoints(def string) (bool, error) {
	row, err := catalog.thingRow(def)
	if err != nil {
		return false, err
	}
	return row.GetUseHitPoints(), nil
}

// hasCompClass is whether any comp of the def has a comp class that is base
// or a subclass of it (Thing.TryGetComp<base>() is non-null).
func (catalog *DefinitionCatalog) hasCompClass(row *d.ThingDef, base string) (bool, error) {
	for _, comp := range row.GetComps() {
		msg := comp.GetValue().ProtoReflect()
		which := msg.WhichOneof(msg.Descriptor().Oneofs().ByName("value"))
		if which == nil {
			continue
		}
		class, err := CompString(msg.Get(which).Message(), "compClass")
		if err != nil {
			return false, err
		}
		if class == "" {
			continue
		}
		match, err := catalog.ClassIsA(class, base)
		if err != nil || match {
			return match, err
		}
	}
	return false, nil
}

// RepairPriority is the order the colony repairs a structure in: 0 for what
// holds the temperature or the power, or a medical bed, 1 for what holds a
// roof, a work table or a bed, 2 for the rest.
func (catalog *DefinitionCatalog) RepairPriority(def string, medicalBed bool) (int, error) {
	row, err := catalog.thingRow(def)
	if err != nil {
		return 0, err
	}
	control, err := catalog.hasCompClass(row, ClassTempControlComp)
	if err != nil {
		return 0, err
	}
	plant, err := catalog.hasCompClass(row, ClassPowerPlantComp)
	if err != nil {
		return 0, err
	}
	bed, err := catalog.ClassIsA(row.GetThingClass(), ClassBedThing)
	if err != nil {
		return 0, err
	}
	if control || plant || bed && medicalBed {
		return 0, nil
	}
	table, err := catalog.ClassIsA(row.GetThingClass(), ClassWorkTableThing)
	if err != nil {
		return 0, err
	}
	if row.GetHoldsRoof() || table || bed {
		return 1, nil
	}
	return 2, nil
}

// BedHumanlike is BuildingProperties.bed_humanlike of the def.
func (catalog *DefinitionCatalog) BedHumanlike(def string) (bool, error) {
	row, err := catalog.thingRow(def)
	if err != nil {
		return false, err
	}
	return row.GetBuilding().GetBedHumanlike(), nil
}

// PlannerStatValue is the stat of a thing made of stuff, read whether or not
// the game shows the stat for the def: a stat the def does not set has the
// stat def's default.
func (catalog *DefinitionCatalog) PlannerStatValue(def, stuff, stat string) (float32, error) {
	value, _, err := catalog.shownValue(stat, stateval.ThingSubject(def, stuff), true)
	return value, err
}

// StuffMaterials are the stuffs of the given stuff category that can make def
// (GenStuff.AllowedStuffsFor), by defName, each with the def's adjusted cost
// when made from it (CostListAdjusted(stuff)).
func (catalog *DefinitionCatalog) StuffMaterials(def, category string) ([]WallMaterial, error) {
	stuffs, err := catalog.AllowedStuffs(def)
	if err != nil {
		return nil, err
	}
	var out []WallMaterial
	for _, stuff := range stuffs {
		row, err := catalog.thingRow(stuff)
		if err != nil {
			return nil, err
		}
		if !slices.Contains(row.GetStuffProps().GetCategories(), category) {
			continue
		}
		costs, err := catalog.AdjustedCosts(def, stuff)
		if err != nil {
			return nil, err
		}
		material := WallMaterial{Stuff: stuff}
		for _, cost := range costs {
			material.Costs = append(material.Costs, Amount{Resource: cost.GetDefName(), Units: cost.GetUnits()})
		}
		out = append(out, material)
	}
	return out, nil
}

// IsMortar is the game's BuildingProperties.IsMortar of the def: a turret
// whose gun has a primary verb with an overhead projectile, or whose gun
// takes loaded shells and accepts the high-explosive shell. A def that is
// not a turret is not a mortar.
func (catalog *DefinitionCatalog) IsMortar(def string) (bool, error) {
	row, err := catalog.thingRow(def)
	if err != nil {
		return false, err
	}
	gunName := row.GetBuilding().GetTurretGunDef()
	if gunName == "" {
		return false, nil
	}
	gun, err := catalog.thingRow(gunName)
	if err != nil {
		return false, err
	}
	for _, entry := range gun.GetVerbs() {
		verb := entry.GetValue()
		if !verb.GetIsPrimary() || verb.GetDefaultProjectile() == "" {
			continue
		}
		projectile, err := catalog.thingRow(verb.GetDefaultProjectile())
		if err != nil {
			return false, err
		}
		if projectile.GetProjectile().GetFlyOverhead() {
			return true, nil
		}
	}
	loads, err := catalog.HasComp(gun, ClassChangeableProjectileComp)
	if err != nil || !loads {
		return false, err
	}
	return catalog.FilterAccepts(gun.GetBuilding().GetFixedStorageSettings().GetFilter(), policy.MortarProbeShells[0])
}
