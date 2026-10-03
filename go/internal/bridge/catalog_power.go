package bridge

import (
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
