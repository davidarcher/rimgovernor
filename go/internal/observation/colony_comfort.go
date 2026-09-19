package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func colonyComfort(v *o.ColonyFactsSnapshot) domain.Fact[policy.ComfortObservation] {
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
			rows = append(rows, policy.ComfortFacility{ID: f.GetId(), RoomID: f.GetRoomId(), Kind: f.GetKind(), AccessibleTo: ids(f.AccessibleTo), Users: ids(f.Users)})
		}
		return rows
	}
	r := policy.ComfortObservation{People: ids(value.People), Dining: facilities(value.Dining), Recreation: facilities(value.Recreation)}
	if j := value.Joy; j != nil {
		r.Joy = &policy.RecreationCensus{Kinds: append([]string(nil), j.Kinds...)}
		for _, p := range j.Pawns {
			r.Joy.Pawns = append(r.Joy.Pawns, policy.JoyTolerance{Pawn: policy.PawnID(p.Pawn), Tolerance: append([]float64(nil), p.Tolerance...), Bored: append([]bool(nil), p.Bored...)})
		}
		for _, m := range j.Methods {
			r.Joy.Methods = append(r.Joy.Methods, policy.JoyBuildingMethod{Definition: m.Definition, Kind: m.Kind, PowerW: m.PowerW})
		}
	}
	for _, s := range value.Surfaces {
		row := policy.DiningSurface{ID: s.GetId(), RoomID: s.GetRoomId()}
		for _, c := range s.Adjacent {
			row.Adjacent = append(row.Adjacent, domain.Cell{X: c.GetX(), Z: c.GetZ()})
		}
		r.Surfaces = append(r.Surfaces, row)
	}
	return domain.Known(r)
}
