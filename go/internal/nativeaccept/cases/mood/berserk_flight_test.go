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

// The first thing a fresh session does with operations_execute is describe
// it, and a games_tool_detail row names that very tool while carrying no
// inner request. Matching on the inner tool alone unmarshalled an empty
// string and failed the case with "unexpected end of JSON input" (#663).
func TestDispatchedOperationSkipsTheSchemaDetailRow(t *testing.T) {
	execute := `{"operation":{"setDrafted":{"pawn":{"entityId":"Thing_Human726"},"drafted":true}}}`
	for _, tc := range []struct {
		name            string
		row             na.FlightRow
		want, wantError bool
	}{
		{"schema detail", na.FlightRow{Kind: "native_request", Sequence: 351, Payload: map[string]any{
			"tool": "games_tool_detail", "arguments": map[string]any{"gameId": "rimgovernor-trial", "tool": "rimgovernor/operations_execute"}}}, false, false},
		{"dispatch", na.FlightRow{Kind: "native_request", Payload: map[string]any{
			"tool": "games_call_tool", "arguments": map[string]any{"tool": "rimgovernor/operations_execute", "arguments": map[string]any{"request": execute}}}}, true, false},
		{"other native tool", na.FlightRow{Kind: "native_request", Payload: map[string]any{
			"tool": "games_call_tool", "arguments": map[string]any{"tool": "rimgovernor/observations_list_pawns", "arguments": map[string]any{"request": "{}"}}}}, false, false},
		{"response", na.FlightRow{Kind: "native_response", Payload: map[string]any{
			"tool": "games_call_tool", "arguments": map[string]any{"tool": "rimgovernor/operations_execute"}}}, false, false},
		{"malformed request", na.FlightRow{Kind: "native_request", Sequence: 7, Payload: map[string]any{
			"tool": "games_call_tool", "arguments": map[string]any{"tool": "rimgovernor/operations_execute", "arguments": map[string]any{"request": "{"}}}}, false, true},
	} {
		t.Run(tc.name, func(t *testing.T) {
			operation, dispatched, err := dispatchedOperation(tc.row)
			if (err != nil) != tc.wantError || dispatched != tc.want {
				t.Fatalf("dispatchedOperation = %v, %v, %v", operation, dispatched, err)
			}
			if tc.want {
				if _, ok := operation["setDrafted"]; !ok {
					t.Fatalf("operation = %v", operation)
				}
			}
		})
	}
}
