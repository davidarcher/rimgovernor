package observation

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// colonyComfort decodes the comfort census. The joy buildings, the recreation
// foothold and the watch-cell buildings are rules over the catalog rows; a
// catalog that cannot answer is an error, never an unknown census.
func colonyComfort(v *o.ColonyFactsSnapshot, catalog *bridge.DefinitionCatalog) (domain.Fact[policy.ComfortObservation], error) {
	value := v.GetUpkeep().GetObserved().GetComfort().GetObserved()
	if value == nil {
		return domain.Unknown[policy.ComfortObservation](), nil
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
		methods, err := catalog.JoyBuildings()
		if err != nil {
			return domain.Unknown[policy.ComfortObservation](), fmt.Errorf("comfort census: %w", err)
		}
		r.Joy.Methods = methods
	}
	foothold, err := catalog.RecreationFoothold()
	if err != nil {
		return domain.Unknown[policy.ComfortObservation](), fmt.Errorf("comfort census: %w", err)
	}
	watch, err := catalog.WatchBuildings()
	if err != nil {
		return domain.Unknown[policy.ComfortObservation](), fmt.Errorf("comfort census: %w", err)
	}
	furniture, err := catalog.DiningFurniture()
	if err != nil {
		return domain.Unknown[policy.ComfortObservation](), fmt.Errorf("comfort census: %w", err)
	}
	r.RecreationFoothold, r.WatchBuildings, r.Furniture = foothold, watch, furniture
	for _, s := range value.Surfaces {
		row := policy.DiningSurface{ID: s.GetId(), RoomID: s.GetRoom().GetId()}
		for _, c := range s.Adjacent {
			row.Adjacent = append(row.Adjacent, domain.Cell{X: c.GetX(), Z: c.GetZ()})
		}
		r.Surfaces = append(r.Surfaces, row)
	}
	return domain.Known(r), nil
}
