package bridge

import (
	"cmp"
	"math"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// StatMarketValue is the StatDef whose abstract value is what a def is worth.
const StatMarketValue = "MarketValue"

// JoyBuildings are the joy buildings the colony can build for recreation,
// chosen by a rule over the catalog rows and never by name: a buildable
// definition whose ThingDef gives a joy kind (BuildingProperties.joyKind),
// with the power its power comp draws. The preference order is the joy one
// session gives, most first: the JobDef joyGainRate times joyDuration of the
// JoyGiverDef that offers the building (the best giver when several do);
// ties go to the cheaper building (the market value of its adjusted costs
// at its cheapest stuff), then to the name. A joy building no
// JoyGiverDef offers, a giver without its job row and a cost the stat table
// cannot value are contract errors. Research and builders are the planner's
// to check.
func (catalog *DefinitionCatalog) JoyBuildings() ([]policy.JoyBuildingMethod, error) {
	if catalog == nil {
		return nil, nil
	}
	found, err := catalog.rankedJoyBuildings()
	if err != nil {
		return nil, err
	}
	methods := make([]policy.JoyBuildingMethod, len(found))
	for i, r := range found {
		methods[i] = r.method
	}
	return methods, nil
}

type rankedJoyBuilding struct {
	method    policy.JoyBuildingMethod
	row       *d.ThingDef
	joy, cost float64
}

// rankedJoyBuildings is JoyBuildings with the rows the rules over them read,
// in the order of the joy a session gives.
func (catalog *DefinitionCatalog) rankedJoyBuildings() ([]rankedJoyBuilding, error) {
	if catalog == nil {
		return nil, nil
	}
	sessions, err := catalog.joyPerSession()
	if err != nil {
		return nil, err
	}
	var found []rankedJoyBuilding
	for name, row := range catalog.ThingDefs {
		kind := row.GetBuilding().GetJoyKind()
		if kind == "" || !Buildable(row) {
			continue
		}
		joy, offered := sessions[name]
		if !offered {
			return nil, contract("joy building %s is offered by no joy giver", name)
		}
		cost, err := catalog.CheapestCostValue(name)
		if err != nil {
			return nil, err
		}
		watts, _, err := catalog.PowerDraw(row)
		if err != nil {
			return nil, err
		}
		found = append(found, rankedJoyBuilding{policy.JoyBuildingMethod{Definition: name, Kind: kind, PowerW: math.Max(0, watts)}, row, joy, cost})
	}
	slices.SortFunc(found, func(a, b rankedJoyBuilding) int {
		return cmp.Or(cmp.Compare(b.joy, a.joy), cmp.Compare(a.cost, b.cost), cmp.Compare(a.method.Definition, b.method.Definition))
	})
	return found, nil
}

// RecreationFoothold is the joy building the colony's first recreation
// facility is: the cheapest joy building that draws no power and needs no
// research (the one a colony can build before anything else), ties to the
// joy one session gives, then the name. A catalog with none is an error.
func (catalog *DefinitionCatalog) RecreationFoothold() (string, error) {
	found, err := catalog.rankedJoyBuildings()
	if err != nil {
		return "", err
	}
	var best *rankedJoyBuilding
	for i := range found {
		r := &found[i]
		if r.method.PowerW > 0 || len(r.row.GetResearchPrerequisites()) > 0 {
			continue
		}
		if best == nil || r.cost < best.cost {
			best = r
		}
	}
	if best == nil {
		return "", contract("catalog has no joy building that draws no power and needs no research")
	}
	return best.method.Definition, nil
}

// watchGiverClass is the JoyGiverDef class whose buildings are used from
// watch cells around them (WatchBuildingUtility).
const watchGiverClass = "RimWorld.JoyGiver_WatchBuilding"

// WatchBuildings are the building defs a JoyGiverDef of the watch-building
// class offers: the ones whose placement preview reports whether every
// colonist can reach a watch cell. Sorted by name.
func (catalog *DefinitionCatalog) WatchBuildings() ([]string, error) {
	if catalog == nil {
		return nil, contract("no definition catalog")
	}
	var names []string
	for _, giver := range catalog.Defs[(&d.JoyGiverDef{}).ProtoReflect().Descriptor().FullName()] {
		row, _ := giver.(*d.JoyGiverDef)
		if row.GetGiverClass() == "" {
			continue
		}
		watch, err := catalog.ClassIsA(row.GetGiverClass(), watchGiverClass)
		if err != nil {
			return nil, err
		}
		if watch {
			for _, building := range row.GetThingDefs() {
				if !slices.Contains(names, building) {
					names = append(names, building)
				}
			}
		}
	}
	slices.Sort(names)
	return names, nil
}

// joyPerSession is the joy one session of each building gives, by building
// def: the best of its JoyGiverDefs' jobs, joyGainRate times joyDuration.
func (catalog *DefinitionCatalog) joyPerSession() (map[string]float64, error) {
	out := map[string]float64{}
	for name, giver := range catalog.Defs[(&d.JoyGiverDef{}).ProtoReflect().Descriptor().FullName()] {
		row, _ := giver.(*d.JoyGiverDef)
		if len(row.GetThingDefs()) == 0 {
			continue
		}
		job := DefRow[*d.JobDef](catalog, row.GetJobDef())
		if job == nil {
			return nil, contract("joy giver %s names job %q the catalog has no row for", name, row.GetJobDef())
		}
		joy := float64(job.GetJoyGainRate()) * float64(job.GetJoyDuration())
		for _, building := range row.GetThingDefs() {
			if catalog.ThingDefs[building] == nil {
				return nil, contract("joy giver %s offers %s, which the catalog has no def row for", name, building)
			}
			if best, seen := out[building]; !seen || joy > best {
				out[building] = joy
			}
		}
	}
	return out, nil
}

// costValue is the market value of def's adjusted cost list at stuff.
func (catalog *DefinitionCatalog) costValue(def, stuff string) (float64, error) {
	costs, err := catalog.AdjustedCosts(def, stuff)
	if err != nil {
		return 0, err
	}
	var total float64
	for _, cost := range costs {
		value, err := catalog.StatValue(cost.GetDefName(), "", StatMarketValue)
		if err != nil {
			return 0, err
		}
		total += float64(value) * float64(cost.GetUnits())
	}
	return total, nil
}
