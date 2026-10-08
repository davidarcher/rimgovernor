package domain

import (
	"reflect"
	"testing"
)

func missionFixture() TradeMission {
	return TradeMission{HomeColony: "colony", HomeMap: 1, HomeTile: 2, Settlement: "settlement", SettlementTile: 3, Crew: []PawnID{"pawn"}, Negotiator: "pawn", SilverBudget: 100, Demand: []CargoItem{{Definition: "Steel", Count: 10}}, Pack: []CargoItem{{Definition: "MealSurvivalPack", Count: 10}, {Definition: "Silver", Count: 100}}, Phase: TradeMissionPlanned, ReturnHome: true}
}

func TestTradeMissionIntentRoundTripAndClosedRecord(t *testing.T) {
	m := missionFixture()
	record, err := m.Record()
	if err != nil {
		t.Fatal(err)
	}
	restored, err := DecodeTradeMission(record)
	if err != nil || !reflect.DeepEqual(restored, m) {
		t.Fatal(restored, err)
	}
	for _, invalid := range []string{record + "{}", " " + record, record[:len(record)-1] + ",\"Inventory\":{}}"} {
		if _, err := DecodeTradeMission(invalid); err == nil {
			t.Fatal("accepted invalid record", invalid)
		}
	}
	snapshot := GenerationSnapshot{Colony: "colony", Map: 1, Load: "load", Plan: "plan"}
	p, err := NewTradeMissionProject("project-trade", 2, snapshot, m)
	if err != nil {
		t.Fatal(err)
	}
	p.Snapshot.Load = "reloaded"
	if err := p.Validate(); err != nil {
		t.Fatal("saved mission cannot reconstruct", err)
	}
	p.Snapshot.Map = 4
	if p.Validate() == nil {
		t.Fatal("foreign home admitted")
	}
}

func TestTradeMissionRejectsMissingIntentAndBudgetDrift(t *testing.T) {
	for _, edit := range []func(*TradeMission){func(m *TradeMission) { m.ReturnHome = false }, func(m *TradeMission) { m.SilverBudget++ }, func(m *TradeMission) { m.Negotiator = "other" }, func(m *TradeMission) { m.Crew = append(m.Crew, "pawn") }, func(m *TradeMission) { m.Phase = "invented" }, func(m *TradeMission) { m.Demand = nil }} {
		m := missionFixture()
		edit(&m)
		if m.Validate() == nil {
			t.Fatal("invalid intent admitted", m)
		}
	}
}
