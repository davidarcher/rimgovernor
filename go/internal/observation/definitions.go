package observation

import (
	"context"
	"errors"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// StarterDefinitions are the planning definitions every planning read
// resolves from the definition catalog (#1340); a read names any other it
// needs.
var StarterDefinitions = []string{
	"Barricade", "Battery", "Bed", "ButcherSpot", "Campfire", "ChemfuelPoweredGenerator", "Cooler", "DiningChair", "Door",
	"Fence", "FenceGate", "FueledStove", "GeothermalGenerator", "Heater", "HorseshoesPin", "PassiveCooler", "PenMarker",
	"Plant_Corn", "Plant_Potato", "Plant_Rice", "PowerConduit", "Sandbags", "SimpleResearchBench", "SleepingSpot",
	"SolarGenerator", "StandingLamp", "Table1x2c", "TableStonecutter", "Wall", "WindTurbine", "WoodFiredGenerator",
}

// DefinitionSource serves the load's definition catalog. A colony source
// without it reads no definitions; one that is also a ResearchSource
// makes a definition available by its finished research.
type DefinitionSource interface {
	DefinitionCatalog(context.Context, *c.Identity) (*bridge.DefinitionCatalog, error)
}

// ResearchSource is the research read whose finished projects make a
// definition available.
type ResearchSource interface {
	ReadResearch(context.Context, *c.Identity) (bridge.ResearchRead, bridge.Result, error)
}

// definitionFacts is what resolves a definition name: the catalog, the
// finished research projects (unknown without a research read) and the
// colony's edible crop rows.
type definitionFacts struct {
	catalog  *bridge.DefinitionCatalog
	finished domain.Fact[map[string]bool]
	crops    map[string]*o.EdibleCrop
}

// readDefinitionFacts reads the catalog and finished research through
// source; ok is false when source serves no definitions (no catalog). A research read
// that fails leaves the finished projects unknown.
func readDefinitionFacts(ctx context.Context, source any, id *c.Identity, planning *o.PlanningFacts) (definitionFacts, bool, error) {
	native, ok := source.(DefinitionSource)
	if !ok {
		return definitionFacts{}, false, nil
	}
	catalog, err := native.DefinitionCatalog(ctx, id)
	if err != nil || catalog == nil {
		return definitionFacts{}, false, err
	}
	facts := definitionFacts{catalog: catalog, crops: cropRows(planning)}
	if research, ok := source.(ResearchSource); ok {
		read, _, err := research.ReadResearch(ctx, id)
		if err == nil {
			facts.finished = finishedSet(read.Finished)
		} else if !errors.Is(err, bridge.ErrUnavailable) {
			return definitionFacts{}, false, err
		}
	}
	return facts, true, nil
}

func finishedSet(names []string) domain.Fact[map[string]bool] {
	set := make(map[string]bool, len(names))
	for _, name := range names {
		set[name] = true
	}
	return domain.Known(set)
}

func cropRows(planning *o.PlanningFacts) map[string]*o.EdibleCrop {
	rows := map[string]*o.EdibleCrop{}
	for _, row := range planning.GetCrops() {
		rows[row.GetDefName()] = row
	}
	return rows
}

// appendDefinitions appends to held the resolved row of every name it
// does not already hold.
func (f definitionFacts) appendDefinitions(held []PlanningDefinition, names []string) []PlanningDefinition {
	for _, name := range names {
		if slices.ContainsFunc(held, func(d PlanningDefinition) bool { return d.Name == name }) {
			continue
		}
		held = append(held, f.resolve(name))
	}
	return held
}

// resolve is name's planning row: the catalog's static facts, available
// when every research prerequisite is finished, with the map's crop facts.
// A name the catalog lacks is unavailable.
func (f definitionFacts) resolve(name string) PlanningDefinition {
	row := f.catalog.Definition(name)
	if row == nil {
		return PlanningDefinition{Name: name, Available: domain.Known(false)}
	}
	d := planningDefinition(row)
	if finished, known := f.finished.Value(); known {
		available := true
		for _, prerequisite := range d.Research {
			available = available && finished[prerequisite]
		}
		d.Available = domain.Known(available)
	} else if len(d.Research) == 0 {
		d.Available = domain.Known(true)
	}
	if crop := f.crops[name]; crop != nil {
		d.NutritionDemandPerDay = optional(crop.NutritionDemandPerDay)
		d.DietAllowed = optional(crop.DietAllowed)
	}
	return d
}
