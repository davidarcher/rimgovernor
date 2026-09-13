package nativeaccept

import "testing"

func gearFixture() (map[string]any, map[string]any) {
	context := map[string]any{"identity": map[string]any{"mapId": 0}, "tick": "10"}
	row := map[string]any{
		"pawn":         map[string]any{"id": "pawn"},
		"snapshot":     map[string]any{"context": context, "entityId": "pawn", "token": "loadout"},
		"deficit":      false,
		"completeness": map[string]any{"page": map[string]any{"complete": true}, "returned": "1"},
		"candidates": []any{map[string]any{
			"item": map[string]any{"thing": map[string]any{"id": "item", "defName": "Shirt"}, "apparel": true, "weapon": false},
			"gain": 1.2,
		}},
	}
	colony := map[string]any{
		"context": context, "colonistCount": 1,
		"planning": map[string]any{"observed": map[string]any{"gear": map[string]any{
			"context": context, "pawns": []any{row}, "completeness": map[string]any{"page": map[string]any{"complete": true}, "returned": "1"},
		}}},
	}
	legacy := map[string]any{
		"success": true, "tick": 10, "mapId": 0,
		"pawns": []any{map[string]any{
			"pawn": "pawn", "loadout": "loadout", "deficit": false,
			"candidates": []any{map[string]any{"target": "item", "gear": map[string]any{"defName": "Shirt"}, "kind": "apparel", "gain": 1.2}},
		}},
	}
	return colony, legacy
}

func deepCopyGearAny(v any) any {
	switch t := v.(type) {
	case map[string]any:
		out := map[string]any{}
		for k, val := range t {
			out[k] = deepCopyGearAny(val)
		}
		return out
	case []any:
		out := make([]any, len(t))
		for i, val := range t {
			out[i] = deepCopyGearAny(val)
		}
		return out
	default:
		return v
	}
}

func TestNativeGearParityPreservesFalseAndExactCandidateCensus(t *testing.T) {
	colony, legacy := gearFixture()
	result, err := AuditGear(colony, legacy)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	want := map[string]any{"pawns": 1, "deficits": 0, "blocked": 0, "candidates": 1, "same_tick_native_parity": true}
	if !DeepEqual(result, want) {
		t.Fatalf("unexpected result: %#v", result)
	}

	for _, mutation := range []string{"missing", "false", "candidate", "gain", "token", "tick", "needs"} {
		t.Run(mutation, func(t *testing.T) {
			changed := deepCopyGearAny(colony).(map[string]any)
			observed, _ := AsMap(changed["planning"])
			observedMap, _ := AsMap(observed["observed"])
			gear, _ := AsMap(observedMap["gear"])
			pawns := AsSlice(gear["pawns"])
			row, _ := AsMap(pawns[0])
			switch mutation {
			case "missing":
				delete(row, "deficit")
			case "false":
				row["deficit"] = true
			case "candidate":
				row["candidates"] = []any{}
			case "gain":
				candidates := AsSlice(row["candidates"])
				candidate, _ := AsMap(candidates[0])
				candidate["gain"] = 2
			case "token":
				snapshot, _ := AsMap(row["snapshot"])
				snapshot["token"] = "changed"
			case "tick":
				changedContext, _ := AsMap(changed["context"])
				changedContext["tick"] = "11"
			case "needs":
				row["replacementNeeds"] = []any{map[string]any{"defName": "Shirt", "reason": "worn"}}
			}
			if _, err := AuditGear(changed, legacy); err == nil {
				t.Fatalf("expected an error for mutation %q", mutation)
			}
		})
	}
}
