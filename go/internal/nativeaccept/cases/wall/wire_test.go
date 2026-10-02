package wall

import (
	"encoding/json"
	"testing"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/encoding/protojson"
)

// Every action JSON the wall cases send must parse as an operations Action
// with unknown fields refused, as native's ProtoJSON parser does.
func TestCaseActionsParseAsProtoJSON(t *testing.T) {
	ground := []any{map[string]any{"origin": map[string]any{"x": 1, "z": 2}, "width": 5, "height": 5}}
	for name, action := range map[string]map[string]any{
		"deconstruct":  deconstructIntent("k", "Thing_1", nil),
		"cleared":      deconstructIntent("k", "Thing_1", map[string]any{"clearedGround": ground}),
		"swap":         deconstructIntent("k", "Thing_1", map[string]any{"replaceWithWall": true}),
		"wall-upgrade": {"key": "k", "designate": map[string]any{"designation": "THING_DESIGNATION_DECONSTRUCT", "cell": map[string]any{"x": 1, "z": 2}, "guard": "DESIGNATION_GUARD_WALL_UPGRADE", "target": map[string]any{"id": "Thing_1"}}},
		"remove-roof":  {"key": "k", "removeRoof": map[string]any{"cells": []any{map[string]any{"x": 1, "z": 2}}}},
		"remove-floor": {"key": "k", "removeFloor": map[string]any{"cell": map[string]any{"x": 1, "z": 2}, "defName": "WoodPlankFloor"}},
	} {
		raw, err := json.Marshal(action)
		if err != nil {
			t.Fatal(err)
		}
		var parsed o.Action
		if err := (protojson.UnmarshalOptions{DiscardUnknown: false}).Unmarshal(raw, &parsed); err != nil {
			t.Errorf("%s: %v", name, err)
		}
	}
}
