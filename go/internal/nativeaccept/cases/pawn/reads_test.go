package pawn

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

func copyAny(m map[string]any) map[string]any {
	out := map[string]any{}
	for k, v := range m {
		if inner, ok := v.(map[string]any); ok {
			out[k] = copyAny(inner)
			continue
		}
		out[k] = v
	}
	return out
}

func pawnContext() map[string]any {
	return map[string]any{"identity": map[string]any{"colonyId": "c", "loadToken": "l", "mapId": 0.0}, "tick": "1", "nativeGeneration": "2"}
}

// controlledPawnRow is one colonist with an exact CAS snapshot.
func controlledPawnRow(context map[string]any, drafted bool) map[string]any {
	ref := map[string]any{"context": copyAny(context), "entityId": "Thing_Human42", "token": "opaque-native-token"}
	return map[string]any{
		"pawn":     map[string]any{"id": "Thing_Human42", "mapId": 0.0, "snapshot": ref},
		"colonist": true, "dead": false, "animal": false, "drafted": drafted,
		"issues": []any{},
	}
}

func TestDraftControlAcceptsLiveColonistExactSnapshot(t *testing.T) {
	context := pawnContext()
	for _, drafted := range []bool{false, true} {
		if err := draftControl(controlledPawnRow(context, drafted), context); err != nil {
			t.Fatalf("drafted=%v: unexpected error: %v", drafted, err)
		}
	}
}

func TestDraftControlRejectsSnapshotDisappearingOrScopeChange(t *testing.T) {
	for _, fault := range []string{"missing-snapshot", "wrong-pawn", "wrong-context", "empty-token"} {
		t.Run(fault, func(t *testing.T) {
			context := pawnContext()
			row := controlledPawnRow(context, false)
			pawn, _ := nativeaccept.AsMap(row["pawn"])
			switch fault {
			case "missing-snapshot":
				delete(pawn, "snapshot")
			case "wrong-pawn":
				snapshot, _ := nativeaccept.AsMap(pawn["snapshot"])
				snapshot["entityId"] = "other"
			case "wrong-context":
				snapshot, _ := nativeaccept.AsMap(pawn["snapshot"])
				snapshotContext, _ := nativeaccept.AsMap(snapshot["context"])
				snapshotContext["tick"] = "2"
			default:
				snapshot, _ := nativeaccept.AsMap(pawn["snapshot"])
				snapshot["token"] = " "
			}
			if err := draftControl(row, context); err == nil {
				t.Fatalf("expected an error for fault %q", fault)
			}
		})
	}
}

// animalPawnRow is a dead, unspawned animal with no snapshot and an
// explicit pawn.snapshot issue.
func animalPawnRow(context map[string]any, reason string) map[string]any {
	row := controlledPawnRow(context, false)
	pawn, _ := nativeaccept.AsMap(row["pawn"])
	delete(pawn, "snapshot")
	row["colonist"] = false
	row["animal"] = true
	row["dead"] = true
	unavailable := map[string]any{"reason": reason, "detail": "Pawn is not spawned on the current map."}
	row["issues"] = []any{map[string]any{"field": "pawn.snapshot", "unavailable": unavailable}}
	return row
}

func TestDraftControlDeadUnspawnedAnimalHasExplicitUnavailability(t *testing.T) {
	context := pawnContext()
	if err := draftControl(animalPawnRow(context, "UNAVAILABLE_REASON_NOT_APPLICABLE"), context); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
}

func TestDraftControlUnavailabilityCannotMaskLiveColonistOrInventReason(t *testing.T) {
	for _, fault := range []string{"snapshot-present", "legacy-unsupported", "missing-issue"} {
		t.Run(fault, func(t *testing.T) {
			context := pawnContext()
			row := animalPawnRow(context, "UNAVAILABLE_REASON_NOT_APPLICABLE")
			switch fault {
			case "snapshot-present":
				pawn, _ := nativeaccept.AsMap(row["pawn"])
				pawn["snapshot"] = map[string]any{}
			case "legacy-unsupported":
				row = animalPawnRow(context, "UNAVAILABLE_REASON_UNSUPPORTED")
			default:
				row["issues"] = []any{}
			}
			if err := draftControl(row, context); err == nil {
				t.Fatalf("expected an error for fault %q", fault)
			}
		})
	}
}

func TestDraftControlReadableAnimalHasSameSnapshot(t *testing.T) {
	context := pawnContext()
	row := controlledPawnRow(context, false)
	row["colonist"] = false
	row["animal"] = true
	if err := draftControl(row, context); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
}

func TestDraftControlAliveAnimalMissingTrackerIsExplicitlyUnavailable(t *testing.T) {
	context := pawnContext()
	row := animalPawnRow(context, "UNAVAILABLE_REASON_NATIVE_COMPONENT_MISSING")
	row["dead"] = false
	if err := draftControl(row, context); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
}

func TestDraftControlLiveCurrentMapAnimalIsNotBlanketNotApplicable(t *testing.T) {
	context := pawnContext()
	row := animalPawnRow(context, "UNAVAILABLE_REASON_NOT_APPLICABLE")
	row["dead"] = false
	if err := draftControl(row, context); err == nil {
		t.Fatal("expected an error: a live current-map animal cannot use NOT_APPLICABLE without a component-missing tracker reason")
	}
}

func pawnCompareRows() ([]any, []any) {
	old := map[string]any{"thingId": "Human1", "defName": "Human", "isColonist": true, "isFreeColonist": true, "isPrisoner": false}
	newRow := map[string]any{"pawn": map[string]any{"id": "Human1", "defName": "Human"}, "colonist": true, "freeColonist": true, "prisoner": false}
	for _, key := range []string{"animal", "humanlike", "mechanoid", "tame", "wild", "hostile", "dead", "downed", "drafted"} {
		old[key] = false
		newRow[key] = false
	}
	return []any{newRow}, []any{old}
}

func TestCompareCoreRejectsMissingPawnsAndFalseCoercion(t *testing.T) {
	typed, legacy := pawnCompareRows()
	if err := compareCore(typed, legacy); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if err := compareCore(nil, legacy); err == nil {
		t.Fatal("expected an error when the typed pawn is missing entirely")
	}
	bad, _ := pawnCompareRows()
	badRow, _ := nativeaccept.AsMap(bad[0])
	badRow["drafted"] = 0.0
	if err := compareCore(bad, legacy); err == nil {
		t.Fatal("expected an error for a falsy-but-not-boolean drafted mismatch")
	}
}
