package nativeaccept

import "testing"

func TestListingPreservesKnownEmptyAndRefusesMissingCounts(t *testing.T) {
	if err := Listing(map[string]any{"totalCount": 0, "returnedCount": 0, "complete": true, "truncated": false}, 0); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if err := Listing(map[string]any{"complete": true}, 0); err == nil {
		t.Fatal("expected an error for a listing missing declared fields")
	}
}

func TestListingRefusesPartial(t *testing.T) {
	base := map[string]any{"totalCount": 1, "returnedCount": 1, "complete": true, "truncated": false}
	changes := []map[string]any{
		{"totalCount": 2}, {"returnedCount": 0}, {"complete": false}, {"truncated": true},
	}
	for _, change := range changes {
		value := copyCompatAny(base)
		for k, v := range change {
			value[k] = v
		}
		if err := Listing(value, 1); err == nil {
			t.Fatalf("expected an error for partial change %#v", change)
		}
	}
}

func cameraFixture() (map[string]any, map[string]any) {
	native := map[string]any{
		"success": true, "rootSize": 20, "zoomRootSize": 20,
		"sizeRange":   map[string]any{"min": 10, "max": 30},
		"mapPosition": map[string]any{"x": 0, "z": 5}, "zoomRange": "Close",
		"cameraZoomExtensionEnabled": false,
		"viewRect":                   map[string]any{"minX": -3, "minZ": -2, "maxX": 10, "maxZ": 20},
	}
	typed := map[string]any{
		"rootSize": 20, "zoomRootSize": 20, "minimumRootSize": 10, "maximumRootSize": 30,
		"mapPosition": map[string]any{"z": 5}, "nativeZoomRange": "Close", "zoomExtensionEnabled": false,
		"viewRect": copyCompatAny(native["viewRect"].(map[string]any)),
	}
	return typed, native
}

func TestCameraSignedViewportAndFalseExtensionAreFacts(t *testing.T) {
	typed, native := cameraFixture()
	if err := CameraMatches(typed, native); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	typed["viewRect"].(map[string]any)["minX"] = 0
	if err := CameraMatches(typed, native); err == nil {
		t.Fatal("expected an error after altering a signed viewport bound")
	}
}

func TestRosterRequiresActualExactIdsAndSpawnedPositions(t *testing.T) {
	native := map[string]any{
		"success": true, "count": 1,
		"colonists": []any{map[string]any{"pawnId": "Thing_17", "name": "Pawn", "spawned": true, "position": map[string]any{"x": 0, "z": 2}}},
	}
	typed := map[string]any{
		"listing": map[string]any{"totalCount": 1, "returnedCount": 1, "complete": true, "truncated": false},
		"colonists": []any{map[string]any{
			"pawnId": "Thing_17", "name": "Pawn", "spawned": true, "mapId": 7, "position": map[string]any{"z": 2},
		}},
	}
	identity := map[string]any{"mapId": 7}
	if err := RosterMatches(typed, native, identity); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	typed["colonists"].([]any)[0].(map[string]any)["pawnId"] = "17"
	if err := RosterMatches(typed, native, identity); err == nil {
		t.Fatal("expected an error for a roster row with a mismatched pawnId")
	}
}

func TestSelectedPawnLabelAndExactNativeFacts(t *testing.T) {
	native := map[string]any{
		"success": true, "selectedCount": 1,
		"selectedObjects": []any{map[string]any{
			"id": "Thing_Human17", "kind": "pawn", "type": "Verse.Pawn", "label": "Short",
			"details": map[string]any{"defName": "Human", "position": map[string]any{"x": 2, "z": 3}},
		}},
	}
	typed := map[string]any{
		"listing": map[string]any{"totalCount": 1, "returnedCount": 1, "complete": true, "truncated": false},
		"selectedObjects": []any{map[string]any{
			"id": "Thing_Human17", "nativeKind": "pawn", "nativeType": "Verse.Pawn", "label": "Short",
			"defName": "Human", "mapId": 0, "position": map[string]any{"x": 2, "z": 3},
		}},
	}
	identity := map[string]any{"mapId": 0}
	if err := SelectionMatches(typed, native, "Thing_Human17", identity); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	typed["selectedObjects"].([]any)[0].(map[string]any)["label"] = "Short, Full title"
	if err := SelectionMatches(typed, native, "Thing_Human17", identity); err == nil {
		t.Fatal("expected an error for a mismatched label")
	}
}
