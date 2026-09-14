package domain

import "errors"

// TravelKind selects one native caravan travel disposition: routing an
// already-formed player caravan to a new tile, routing it to visit a
// settlement there, sending it home, or holding it in place through its
// native path follower. It mirrors the native TravelKind enum
// (contracts/proto/operations.proto) exactly.
type TravelKind string

const (
	TravelMove       TravelKind = "move"
	TravelVisit      TravelKind = "visit"
	TravelReturnHome TravelKind = "return_home"
	TravelStop       TravelKind = "stop"
)

// TravelCaravan is explicit intent to route or hold one already-observed,
// already-formed player caravan. It reuses the native TravelCaravan
// operation (NativeCaravanTravel.cs), the same subsystem FormCaravan already
// exits into (CaravanExitMapUtility) and the one home/world_progression
// already observes (CaravanEffect.PathStarted/Stopped). Destination
// selection, route/food adequacy and expedition risk are established by
// policy before dispatch, not here; this canonical encoding keeps Action
// comparable the same way CaravanDeparture's does.
//
// destinationTile is meaningful only for Move/Visit (>=0); ReturnHome and
// Stop carry no destination (-1 sentinel) since native derives their target
// itself (home tile, or the caravan's own current tile).
type TravelCaravan struct {
	caravan         CaravanID
	kind            TravelKind
	destinationTile int32
}

func NewTravelCaravan(caravan CaravanID, kind TravelKind, destinationTile int32) (TravelCaravan, error) {
	if !validID(string(caravan)) {
		return TravelCaravan{}, errors.New("travel caravan requires a valid caravan identity")
	}
	switch kind {
	case TravelMove, TravelVisit:
		if destinationTile < 0 {
			return TravelCaravan{}, errors.New("route requires a valid destination tile")
		}
	case TravelReturnHome, TravelStop:
		if destinationTile != -1 {
			return TravelCaravan{}, errors.New("return-home and hold carry no destination tile")
		}
	default:
		return TravelCaravan{}, errors.New("invalid travel kind")
	}
	return TravelCaravan{caravan: caravan, kind: kind, destinationTile: destinationTile}, nil
}

func (t TravelCaravan) Caravan() CaravanID     { return t.caravan }
func (t TravelCaravan) Kind() TravelKind       { return t.kind }
func (t TravelCaravan) DestinationTile() int32 { return t.destinationTile }

const TravelCaravanAction ActionKind = "travel_caravan"

func NewTravelCaravanAction(id ActionID, travel TravelCaravan) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewTravelCaravan(travel.caravan, travel.kind, travel.destinationTile); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: TravelCaravanAction, travelCaravan: travel}, nil
}

func (a Action) TravelCaravan() (TravelCaravan, bool) {
	return a.travelCaravan, a.kind == TravelCaravanAction
}
