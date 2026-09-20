package mood

import (
	"encoding/json"
	"os"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

func TestBerserkPawnFlightResponses(t *testing.T) {
	// Retained discovery response at sequence 103 of berserk574b (#579).
	data, err := os.ReadFile("testdata/berserk-pawn-detail.json")
	if err != nil {
		t.Fatal(err)
	}
	var detail na.FlightRow
	if err := json.Unmarshal(data, &detail); err != nil {
		t.Fatal(err)
	}
	for _, tc := range []struct {
		name, kind, tool, nativeTool, payload string
		wantRead, wantError                   bool
	}{
		{"invocation", "native_response", "games_call_tool", "rimgovernor/observations_list_pawns", `{"observed":{"pawns":[{"pawn":{"id":"broken","position":{"x":10,"z":12}},"mentalStateIsAggro":true,"downed":false,"dead":false}]}}`, true, false},
		{"empty invocation", "native_response", "games_call_tool", "rimgovernor/observations_list_pawns", "", false, true},
		{"malformed invocation", "native_response", "games_call_tool", "rimgovernor/observations_list_pawns", "{", false, true},
		{"request", "native_request", "games_call_tool", "rimgovernor/observations_list_pawns", "", false, false},
		{"other tool", "native_response", "games_call_tool", "rimgovernor/observations_list_zones", "", false, false},
		{"discovery", detail.Kind, "games_tool_detail", "rimgovernor/observations_list_pawns", "", false, false},
	} {
		t.Run(tc.name, func(t *testing.T) {
			a := berserkDispatch{target: "broken", positions: map[string]domain.Cell{}}
			event := na.FlightRow{Kind: tc.kind, Payload: map[string]any{"tool": tc.tool, "native_tool": tc.nativeTool, "result": map[string]any{"payload": tc.payload}}}
			if tc.name == "discovery" {
				event = detail
			}
			if err := a.observeEvent(event); (err != nil) != tc.wantError {
				t.Fatalf("error = %v, wantError = %v", err, tc.wantError)
			}
			if a.active != tc.wantRead || (a.reads == 1) != tc.wantRead {
				t.Fatalf("active=%v reads=%d", a.active, a.reads)
			}
			if tc.wantRead && a.positions["broken"] != (domain.Cell{X: 10, Z: 12}) {
				t.Fatalf("positions=%v", a.positions)
			}
		})
	}
}
