package bridge

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	"google.golang.org/protobuf/reflect/protoreflect"
)

// The CompProperties_Power compClass bases that decide how a producer
// delivers its nominal watts over a day.
const (
	ClassSolarPlantComp = "RimWorld.CompPowerPlantSolar"
	ClassWindPlantComp  = "RimWorld.CompPowerPlantWind"
)

// CompString is the named string field of a comp message; a message without
// the field is a contract error.
func CompString(comp protoreflect.Message, field string) (string, error) {
	fd := comp.Descriptor().Fields().ByName(protoreflect.Name(field))
	if fd == nil || fd.Kind() != protoreflect.StringKind || fd.IsList() {
		return "", contract("comp %s has no string field %s", comp.Descriptor().FullName(), field)
	}
	return comp.Get(fd).String(), nil
}

// PowerSources is the delivery profile of every def whose power comp
// generates (a negative draw): a solar plant by its comp class
// (CompPowerPlantSolar or a subclass) delivers nothing at night, a wind plant
// the weather average, every other plant its nominal watts around the clock.
func (catalog *DefinitionCatalog) PowerSources() (map[string]policy.PowerSourceProfile, error) {
	if catalog == nil {
		return nil, contract("no definition catalog")
	}
	catalog.powerOnce.Do(func() { catalog.powerSources, catalog.powerErr = catalog.buildPowerSources() })
	return catalog.powerSources, catalog.powerErr
}

func (catalog *DefinitionCatalog) buildPowerSources() (map[string]policy.PowerSourceProfile, error) {
	out := map[string]policy.PowerSourceProfile{}
	for name, row := range catalog.ThingDefs {
		comp, err := catalog.CompOf(row, ClassPowerComp)
		if err != nil {
			return nil, err
		}
		if comp == nil {
			continue
		}
		watts, err := CompFloat(comp, "basePowerConsumption")
		if err != nil {
			return nil, err
		}
		if watts >= 0 {
			continue
		}
		class, err := CompString(comp, "compClass")
		if err != nil {
			return nil, err
		}
		profile := policy.ConstantPowerProfile
		if class != "" {
			solar, err := catalog.ClassIsA(class, ClassSolarPlantComp)
			if err != nil {
				return nil, err
			}
			wind, err := catalog.ClassIsA(class, ClassWindPlantComp)
			if err != nil {
				return nil, err
			}
			switch {
			case solar:
				profile = policy.SolarPowerProfile
			case wind:
				profile = policy.WindPowerProfile
			}
		}
		out[name] = profile
	}
	return out, nil
}

// PowerBattery is the storage of the named battery def: the capacity and
// charge efficiency of its CompProperties_Battery. A def without the comp is
// an error.
func (catalog *DefinitionCatalog) PowerBattery(name string) (policy.PowerBattery, error) {
	row, err := catalog.thingRow(name)
	if err != nil {
		return policy.PowerBattery{}, err
	}
	battery := compOf(row, (*d.CompPropertiesAny).GetCompProperties_Battery)
	if battery == nil {
		return policy.PowerBattery{}, contract("def %s has no battery comp", name)
	}
	out := policy.PowerBattery{CapacityWD: float64(battery.GetStoredEnergyMax()), Efficiency: float64(battery.GetEfficiency())}
	if !(out.CapacityWD > 0) || !(out.Efficiency > 0 && out.Efficiency <= 1) {
		return policy.PowerBattery{}, contract("battery %s has capacity %v and efficiency %v", name, out.CapacityWD, out.Efficiency)
	}
	return out, nil
}

// BatteryCapacityWD is the stored-energy capacity of the named def's
// CompProperties_Battery; a def without the comp is unknown, not an error.
func (catalog *DefinitionCatalog) BatteryCapacityWD(name string) (domain.Fact[float64], error) {
	row, err := catalog.thingRow(name)
	if err != nil {
		return domain.Fact[float64]{}, err
	}
	battery := compOf(row, (*d.CompPropertiesAny).GetCompProperties_Battery)
	if battery == nil {
		return domain.Unknown[float64](), nil
	}
	return domain.Known(float64(battery.GetStoredEnergyMax())), nil
}

// The CompProperties_Power compClass of a bare conduit, and the thing class
// of a plain building: a plain Building whose power comp only transmits.
const (
	ClassConduitComp   = "RimWorld.CompPowerTransmitter"
	ClassPlainBuilding = "Verse.Building"
)

// PlainConduit is whether the named def is a power conduit: a plain Building
// whose power comp is a bare CompPowerTransmitter (no generator, consumer,
// battery or switch class).
func (catalog *DefinitionCatalog) PlainConduit(name string) (bool, error) {
	row, err := catalog.thingRow(name)
	if err != nil {
		return false, err
	}
	if row.GetThingClass() != ClassPlainBuilding {
		return false, nil
	}
	comp, err := catalog.CompOf(row, ClassPowerComp)
	if err != nil || comp == nil {
		return false, err
	}
	class, err := CompString(comp, "compClass")
	return class == ClassConduitComp, err
}

// PowerBaseW is the base wattage a power row states for the named def, in the
// sign the power policy reads: negative for a consumer, positive for a
// producer, zero for a battery. It is the def's basePowerConsumption with the
// factor of every power upgrade whose research is finished applied in order,
// as the game's CompProperties_Power.PowerConsumption does. A def with upgrades
// and no finished-research census yields an unknown fact; a def with no power
// comp is an error.
func (catalog *DefinitionCatalog) PowerBaseW(name string, finished domain.Fact[[]string]) (domain.Fact[float64], error) {
	row, err := catalog.thingRow(name)
	if err != nil {
		return domain.Fact[float64]{}, err
	}
	comp, err := catalog.CompOf(row, ClassPowerComp)
	if err != nil {
		return domain.Fact[float64]{}, err
	}
	if comp == nil {
		return domain.Fact[float64]{}, contract("def %s has no power comp", name)
	}
	draw, err := CompFloat(comp, "basePowerConsumption")
	if err != nil {
		return domain.Fact[float64]{}, err
	}
	fd := comp.Descriptor().Fields().ByName("powerUpgrades")
	if fd == nil || !fd.IsList() || fd.Message() == nil {
		return domain.Fact[float64]{}, contract("comp %s has no powerUpgrades list", comp.Descriptor().FullName())
	}
	upgrades := comp.Get(fd).List()
	if upgrades.Len() == 0 {
		return domain.Known(0 - float64(float32(draw))), nil
	}
	done, known := finished.Value()
	if !known {
		return domain.Fact[float64]{}, nil
	}
	num := float32(draw)
	for i := 0; i < upgrades.Len(); i++ {
		wrapper := upgrades.Get(i).Message()
		valueField := wrapper.Descriptor().Fields().ByName("value")
		if valueField == nil || !wrapper.Has(valueField) {
			return domain.Fact[float64]{}, contract("def %s has an empty power upgrade", name)
		}
		upgrade := wrapper.Get(valueField).Message()
		project := upgrade.Get(upgrade.Descriptor().Fields().ByName("researchProject")).String()
		if project != "" && slices.Contains(done, project) {
			num *= float32(upgrade.Get(upgrade.Descriptor().Fields().ByName("factor")).Float())
		}
	}
	return domain.Known(0 - float64(num)), nil
}
