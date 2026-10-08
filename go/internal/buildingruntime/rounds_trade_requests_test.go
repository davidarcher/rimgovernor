package buildingruntime

import (
	"encoding/json"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"testing"
)

func TestCommsTradeRequestSavedIntentReconcilesNativeFacts(t *testing.T) {
	record := commsTradeRequestRecord{Request: domain.CommsTradeRequest{Kind: domain.TradeRequestOrbital, Faction: "ally", TraderKind: "bulk", Console: "console", Negotiator: "pawn", ExpectedLastRequestTick: -900000}}
	encoded, err := json.Marshal(record)
	if err != nil {
		t.Fatal(err)
	}
	saved, err := requestRecord(string(encoded))
	if err != nil || saved != record {
		t.Fatal(saved, err)
	}
	for _, tc := range []struct {
		name    string
		read    executor.CommsTradeInspection
		pending bool
	}{
		{"reload_walking", executor.CommsTradeInspection{RequestKnown: true, LastRequestTick: -900000, MatchingWork: true}, true},
		{"reload_paid_queue", executor.CommsTradeInspection{RequestKnown: true, LastRequestTick: 100, Tick: 200, MatchingArrival: true}, true},
		{"reload_paid_unknown", executor.CommsTradeInspection{RequestKnown: true, LastRequestTick: 100, Tick: 200}, true},
		{"reload_interrupted", executor.CommsTradeInspection{RequestKnown: true, LastRequestTick: -900000, Tick: 200}, false},
		{"arrival_handoff", executor.CommsTradeInspection{RequestKnown: true, LastRequestTick: 100, Tick: 3000, MatchingSeller: true}, false},
		{"no_show", executor.CommsTradeInspection{RequestKnown: true, LastRequestTick: 100, Tick: 6000}, false},
		{"invalid_target", executor.CommsTradeInspection{Tick: 6000}, false},
	} {
		t.Run(tc.name, func(t *testing.T) {
			if pending := requestRecordPending(saved, tc.read); pending != tc.pending {
				t.Fatal(pending, tc.pending)
			}
		})
	}
}
