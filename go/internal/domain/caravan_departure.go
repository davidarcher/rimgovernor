package domain

import (
	"encoding/json"
	"errors"
	"sort"
)

// CaravanDeparture is explicit intent to form and send one already-selected
// crew, carrying already-selected cargo, toward one already-scouted world
// tile. It reuses the native FormCaravan operation, the same one Python's
// expedition_policy.evaluate_expedition gates through home/world_progression
// before any caravan/travel_caravan call. Native reachability, home staffing,
// route/food adequacy and destination risk are established by policy before
// dispatch, not here; a canonical crew/cargo encoding keeps Action comparable
// the same way WorkAssignment encodes its settings list.
type CargoItem struct {
	Definition string
	Count      uint64
}

type CaravanDeparture struct {
	crew            string // canonical JSON-encoded sorted, deduplicated []PawnID
	cargo           string // canonical JSON-encoded sorted, deduplicated []CargoItem
	destinationTile int32
}

func NewCaravanDeparture(crew []PawnID, cargo []CargoItem, destinationTile int32) (CaravanDeparture, error) {
	if len(crew) == 0 || len(crew) > 64 || destinationTile < 0 {
		return CaravanDeparture{}, errors.New("caravan departure requires a nonempty bounded crew and a valid destination tile")
	}
	rows := append([]PawnID(nil), crew...)
	sort.Slice(rows, func(i, j int) bool { return rows[i] < rows[j] })
	seenCrew := make(map[PawnID]bool, len(rows))
	for _, pawn := range rows {
		if !validID(string(pawn)) || seenCrew[pawn] {
			return CaravanDeparture{}, errors.New("invalid or duplicate caravan crew pawn")
		}
		seenCrew[pawn] = true
	}
	items := append([]CargoItem(nil), cargo...)
	sort.Slice(items, func(i, j int) bool { return items[i].Definition < items[j].Definition })
	if len(items) > 256 {
		return CaravanDeparture{}, errors.New("caravan cargo exceeds storage bound")
	}
	seenCargo := make(map[string]bool, len(items))
	for _, item := range items {
		if !validID(item.Definition) || item.Count == 0 || item.Count > 1<<32 || seenCargo[item.Definition] {
			return CaravanDeparture{}, errors.New("invalid or duplicate caravan cargo item")
		}
		seenCargo[item.Definition] = true
	}
	crewData, err := json.Marshal(rows)
	if err != nil {
		return CaravanDeparture{}, err
	}
	cargoData, err := json.Marshal(items)
	if err != nil {
		return CaravanDeparture{}, err
	}
	if len(crewData) > 8000 || len(cargoData) > 30000 {
		return CaravanDeparture{}, errors.New("caravan departure exceeds storage bound")
	}
	return CaravanDeparture{crew: string(crewData), cargo: string(cargoData), destinationTile: destinationTile}, nil
}

func (c CaravanDeparture) Crew() []PawnID {
	var rows []PawnID
	_ = json.Unmarshal([]byte(c.crew), &rows)
	return rows
}
func (c CaravanDeparture) Cargo() []CargoItem {
	var rows []CargoItem
	_ = json.Unmarshal([]byte(c.cargo), &rows)
	return rows
}
func (c CaravanDeparture) DestinationTile() int32 { return c.destinationTile }

func NewCaravanDepartureAction(id ActionID, departure CaravanDeparture) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	canonical, err := NewCaravanDeparture(departure.Crew(), departure.Cargo(), departure.destinationTile)
	if err != nil || canonical != departure {
		return Action{}, errors.New("invalid caravan departure")
	}
	return Action{id: id, kind: CaravanDepartureAction, caravanDeparture: departure}, nil
}

func (a Action) CaravanDeparture() (CaravanDeparture, bool) {
	return a.caravanDeparture, a.kind == CaravanDepartureAction
}
