package observation

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// joyMethods are the joy buildings a colony can add for variety, in policy
// preference order: each policy.RecreationDefinitions def the catalog lists
// as a buildable joy building, with the joy kind its def gives and the power
// its planning row draws (#1733). Research and builders are checked by the
// planner, not here.
func joyMethods(catalog *bridge.DefinitionCatalog) ([]policy.JoyBuildingMethod, error) {
	var methods []policy.JoyBuildingMethod
	if catalog == nil {
		return methods, nil
	}
	for _, name := range policy.RecreationDefinitions {
		planning := catalog.Definition(name)
		if planning == nil {
			continue
		}
		kind, err := catalog.JoyKind(name)
		if err != nil {
			return nil, err
		}
		if kind == "" {
			continue
		}
		methods = append(methods, policy.JoyBuildingMethod{Definition: name, Kind: kind, PowerW: math.Max(0, planning.GetPowerW())})
	}
	return methods, nil
}

func colonyComfort(v *o.ColonyFactsSnapshot, catalog *bridge.DefinitionCatalog) domain.Fact[policy.ComfortObservation] {
	value := v.GetUpkeep().GetObserved().GetComfort().GetObserved()
	if value == nil {
		return domain.Unknown[policy.ComfortObservation]()
	}
	ids := func(values []string) []policy.PawnID {
		rows := make([]policy.PawnID, 0, len(values))
		for _, id := range values {
			rows = append(rows, policy.PawnID(id))
		}
		return rows
	}
	facilities := func(values []*o.ComfortFacility) []policy.ComfortFacility {
		rows := make([]policy.ComfortFacility, 0, len(values))
		for _, f := range values {
			rows = append(rows, policy.ComfortFacility{ID: f.GetId(), RoomID: f.GetRoom().GetId(), Kind: f.GetKind(), AccessibleTo: ids(bridge.RefIDs(f.AccessibleTo)), Users: ids(bridge.RefIDs(f.Users))})
		}
		return rows
	}
	r := policy.ComfortObservation{People: ids(value.People), Dining: facilities(value.Dining), Recreation: facilities(value.Recreation)}
	if j := value.Joy; j != nil {
		r.Joy = &policy.RecreationCensus{Kinds: append([]string(nil), j.Kinds...)}
		for _, p := range j.Pawns {
			r.Joy.Pawns = append(r.Joy.Pawns, policy.JoyTolerance{Pawn: policy.PawnID(p.Pawn), Tolerance: append([]float64(nil), p.Tolerance...), Bored: append([]bool(nil), p.Bored...)})
		}
		methods, err := joyMethods(catalog)
		if err != nil {
			return domain.Unknown[policy.ComfortObservation]()
		}
		r.Joy.Methods = methods
	}
	for _, s := range value.Surfaces {
		row := policy.DiningSurface{ID: s.GetId(), RoomID: s.GetRoom().GetId()}
		for _, c := range s.Adjacent {
			row.Adjacent = append(row.Adjacent, domain.Cell{X: c.GetX(), Z: c.GetZ()})
		}
		r.Surfaces = append(r.Surfaces, row)
	}
	return domain.Known(r)
}
