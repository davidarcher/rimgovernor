package domain

import (
	"bytes"
	"encoding/json"
	"errors"
	"io"
	"slices"
)

const TradeMissionConcern ConcernID = "trade-mission"

type TradeMissionPhase string

const (
	TradeMissionPlanned   TradeMissionPhase = "planned"
	TradeMissionDeparting TradeMissionPhase = "departing"
	TradeMissionOutbound  TradeMissionPhase = "outbound"
	TradeMissionBuying    TradeMissionPhase = "buying"
	TradeMissionReturning TradeMissionPhase = "returning"
	TradeMissionDelivered TradeMissionPhase = "delivered"
	TradeMissionEnded     TradeMissionPhase = "ended"
)

// TradeMission is intent carried by Project.Record. It holds no native
// inventory, caravan identity, position, prices or routes.
type TradeMission struct {
	HomeColony     ColonyID
	HomeMap        MapID
	HomeTile       int32
	Settlement     string
	SettlementTile int32
	Crew           []PawnID
	Negotiator     PawnID
	SilverBudget   int64
	Demand         []CargoItem
	Pack           []CargoItem
	Phase          TradeMissionPhase
	ReturnHome     bool
}

func (m TradeMission) Validate() error {
	if !validID(string(m.HomeColony)) || m.HomeMap < 0 || m.HomeTile < 0 || !validID(m.Settlement) || m.SettlementTile < 0 || m.SettlementTile == m.HomeTile || m.SilverBudget <= 0 || !m.ReturnHome || !slices.Contains(m.Crew, m.Negotiator) {
		return errors.New("invalid trade mission identity or budget")
	}
	departure, err := NewCaravanDeparture(m.Crew, m.Pack, m.SettlementTile)
	if err != nil || !slices.Equal(m.Crew, departure.Crew()) || !slices.Equal(m.Pack, departure.Cargo()) {
		return errors.New("invalid trade mission crew or pack")
	}
	demand, err := NewCaravanDeparture(m.Crew, m.Demand, m.SettlementTile)
	if err != nil || len(m.Demand) == 0 || !slices.Equal(m.Demand, demand.Cargo()) {
		return errors.New("invalid trade mission demand")
	}
	silver := uint64(0)
	for _, item := range m.Pack {
		if item.Definition == "Silver" {
			silver = item.Count
		}
	}
	if silver != uint64(m.SilverBudget) {
		return errors.New("trade mission budget differs from packed silver")
	}
	switch m.Phase {
	case TradeMissionPlanned, TradeMissionDeparting, TradeMissionOutbound, TradeMissionBuying, TradeMissionReturning, TradeMissionDelivered, TradeMissionEnded:
	default:
		return errors.New("invalid trade mission phase")
	}
	return nil
}

func (m TradeMission) Record() (string, error) {
	if err := m.Validate(); err != nil {
		return "", err
	}
	data, err := json.Marshal(m)
	if err != nil {
		return "", err
	}
	if len(data) > MaxProjectRecord {
		return "", errors.New("trade mission record exceeds bound")
	}
	return string(data), nil
}

func DecodeTradeMission(record string) (TradeMission, error) {
	var m TradeMission
	if len(record) > MaxProjectRecord {
		return m, errors.New("trade mission record exceeds bound")
	}
	decoder := json.NewDecoder(bytes.NewBufferString(record))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&m); err != nil {
		return m, err
	}
	if err := decoder.Decode(new(any)); err != io.EOF {
		return m, errors.New("trailing trade mission record")
	}
	canonical, err := m.Record()
	if err != nil {
		return m, err
	}
	if record != canonical {
		return m, errors.New("noncanonical trade mission record")
	}
	return m, nil
}

func (m TradeMission) Departure() (CaravanDeparture, error) {
	if err := m.Validate(); err != nil {
		return CaravanDeparture{}, err
	}
	return NewCaravanDeparture(m.Crew, m.Pack, m.SettlementTile)
}

func NewTradeMissionProject(id ProjectID, priority int, snapshot GenerationSnapshot, m TradeMission) (Project, error) {
	if m.Phase != TradeMissionPlanned || m.HomeColony != snapshot.Colony || m.HomeMap != snapshot.Map {
		return Project{}, errors.New("trade mission scope mismatch")
	}
	record, err := m.Record()
	if err != nil {
		return Project{}, err
	}
	p := Project{ID: id, Kind: TradeMissionConcern, Priority: priority, Snapshot: snapshot, Status: ProjectOpen, Finding: FindingUnclear, Record: record}
	return p, p.Validate()
}
