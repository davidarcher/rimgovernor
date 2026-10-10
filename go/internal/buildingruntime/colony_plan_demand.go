package buildingruntime

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// coreRoomsWanted is the demand-grown core rooms (policy.CoreRoomsOwed) the
// colony needs now. Each reads the signal its own builder acts on, so the
// room is planned when something is about to shell it and not before: a
// planned room costs ground and, in rock, a dig.
func coreRoomsWanted(projection observation.ColonyProjection) []policy.PlannedRole {
	var wanted []policy.PlannedRole
	want := func(on bool, role policy.PlannedRole) {
		if on {
			wanted = append(wanted, role)
		}
	}
	// A patient the bed ladder can only serve with a new hospital bed.
	choice, err := policy.SelectHospitalBed(hospitalRequest(projection))
	want(err == nil && choice.Method == policy.HospitalBuild, policy.PlannedHospital)
	// No research bench stands yet: the research ladder shells the lab.
	built, known := policy.ResearchBenchBuilt(projection.Facts.CurrentConstruction).Value()
	// Or the high-tech bench or its analyzer is buildable and owed.
	_, advanced := advancedLabOwed(projection)
	want(known && !built || advanced, policy.PlannedLab)
	want(recRoomWanted(projection), policy.PlannedRec)
	benches, bk := projection.ButcheringBenches.Value()
	want(bk && butcherTableWanted(projection, benches), policy.PlannedButchery)
	prisoners, pk := projection.Facts.Prisoners.Value()
	want(pk && heldPrisoners(prisoners) > 0, policy.PlannedPrison)
	// A generator stands: batteries bank its surplus.
	topology, tk := projection.PowerPlanning.Value()
	want(tk && slices.ContainsFunc(topology.Buildings, generatorSite), policy.PlannedBattery)
	return wanted
}

// generatorSite is a standing generator, geothermal included.
func generatorSite(site policy.PowerSite) bool {
	return site.Definition == policy.GeothermalDefinition || slices.Contains(policy.GeneratorDefinitions, site.Definition)
}

// recRoomWanted is the comfort ladder's foothold method: recreation is short
// and the dining room is not.
func recRoomWanted(projection observation.ColonyProjection) bool {
	v, known := projection.Facts.Comfort.Value()
	if !known {
		return false
	}
	review, err := policy.ReviewComfort(projection.Facts.Comfort, policy.ComfortHistory{}, projection.Identity.Tick)
	if err != nil {
		return false
	}
	method, err := policy.SelectComfortMethod(v, review)
	if err != nil {
		return false
	}
	switch method {
	case policy.ComfortNoMethod, policy.ComfortWait, policy.ComfortAccessBlocked:
		return false
	}
	return v.IsFoothold(method)
}
