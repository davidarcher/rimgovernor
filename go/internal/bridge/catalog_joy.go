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
// with the power its planning row draws. The preference order is the joy one
// session gives, most first: the JobDef joyGainRate times joyDuration of the
// JoyGiverDef that offers the building (the best giver when several do);
// ties go to the cheaper building (the market value of its adjusted costs
// at the planning row's stuff), then to the name. A joy building no
// JoyGiverDef offers, a giver without its job row and a cost the stat table
// cannot value are contract errors. Research and builders are the planner's
// to check.
func (catalog *DefinitionCatalog) JoyBuildings() ([]policy.JoyBuildingMethod, error) {
	if catalog == nil {
		return nil, nil
	}
	sessions, err := catalog.joyPerSession()
	if err != nil {
		return nil, err
	}
	type ranked struct {
		method    policy.JoyBuildingMethod
		joy, cost float64
	}
	var found []ranked
	for name, planning := range catalog.Definitions {
		row := catalog.ThingDefs[name]
		if row == nil {
			if planning.GetTerrain() {
				continue
			}
			return nil, contract("catalog has no def row for %s", name)
		}
		kind := row.GetBuilding().GetJoyKind()
		if kind == "" {
			continue
		}
		joy, offered := sessions[name]
		if !offered {
			return nil, contract("joy building %s is offered by no joy giver", name)
		}
		cost, err := catalog.costValue(name, planning.GetStuff())
		if err != nil {
			return nil, err
		}
		found = append(found, ranked{policy.JoyBuildingMethod{Definition: name, Kind: kind, PowerW: math.Max(0, planning.GetPowerW())}, joy, cost})
	}
	slices.SortFunc(found, func(a, b ranked) int {
		return cmp.Or(cmp.Compare(b.joy, a.joy), cmp.Compare(a.cost, b.cost), cmp.Compare(a.method.Definition, b.method.Definition))
	})
	methods := make([]policy.JoyBuildingMethod, len(found))
	for i, r := range found {
		methods[i] = r.method
	}
	return methods, nil
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
