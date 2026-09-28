package nativeaccept

import "testing"

func rowContext() map[string]any {
	return map[string]any{"identity": map[string]any{"colonyId": "c"}, "tick": "10"}
}

// pawnRowFixture builds a pawn row.
func pawnRowFixture(drafted bool) map[string]any {
	snapshot := map[string]any{"entityId": "pawn-1", "token": "tok-1", "context": rowContext()}
	return map[string]any{
		"pawn":    map[string]any{"id": "pawn-1", "snapshot": snapshot},
		"drafted": drafted,
	}
}

func observedReply(row map[string]any) map[string]any {
	return map[string]any{"observed": map[string]any{
		"context":      rowContext(),
		"completeness": map[string]any{},
		"pawns":        []any{row},
	}}
}

func TestPawnRowAcceptsRow(t *testing.T) {
	identity := map[string]any{"colonyId": "c"}
	row := pawnRowFixture(false)
	reply := observedReply(row)
	got, err := PawnRow(reply, identity, "pawn-1")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if AsString(got["pawn"].(map[string]any)["id"]) != "pawn-1" {
		t.Fatalf("wrong pawn returned: %#v", got)
	}
}

func TestPawnRowRejectsWrongPawnID(t *testing.T) {
	identity := map[string]any{"colonyId": "c"}
	reply := observedReply(pawnRowFixture(false))
	if _, err := PawnRow(reply, identity, "other-pawn"); err == nil {
		t.Fatal("expected an error for a pawn id mismatch")
	}
}

func TestSameControlDetectsDraftedChange(t *testing.T) {
	before := pawnRowFixture(false)
	after := pawnRowFixture(true)
	if err := SameControl(before, after); err == nil {
		t.Fatal("expected an error for a changed drafted flag")
	}
}

func TestSameControlAcceptsIdenticalRows(t *testing.T) {
	row := pawnRowFixture(true)
	if err := SameControl(row, row); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
}

func TestActualOrderRejectsUnacceptedFixture(t *testing.T) {
	row := pawnRowFixture(false)
	external := map[string]any{"success": true, "accepted": false}
	if err := ActualOrder(external, row); err == nil {
		t.Fatal("expected an error for an unaccepted external order")
	}
}

// draftReply builds a PawnRow input: one drafted row under a single complete page.
func draftReply() map[string]any {
	context := map[string]any{"identity": map[string]any{"colonyId": "colony", "loadToken": "load", "mapId": 0.0}, "tick": "0", "nativeGeneration": "1"}
	snapshot := map[string]any{"context": deepCopyMap(context), "entityId": "Human1", "token": "token"}
	row := map[string]any{
		"pawn": map[string]any{"id": "Human1", "snapshot": snapshot}, "drafted": true,
	}
	return map[string]any{"observed": map[string]any{"context": context, "pawns": []any{row}, "completeness": map[string]any{}}}
}

func draftRow(t *testing.T) map[string]any {
	t.Helper()
	value := draftReply()
	observed, _ := AsMap(value["observed"])
	context, _ := AsMap(observed["context"])
	identity, _ := AsMap(context["identity"])
	row, err := PawnRow(value, identity, "Human1")
	if err != nil {
		t.Fatalf("unexpected error building fixture row: %v", err)
	}
	return row
}

func TestPawnRowExactSnapshotValidation(t *testing.T) {
	value := draftReply()
	observed, _ := AsMap(value["observed"])
	context, _ := AsMap(observed["context"])
	identity, _ := AsMap(context["identity"])
	row, err := PawnRow(value, identity, "Human1")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if drafted, _ := row["drafted"].(bool); !drafted {
		t.Fatalf("expected drafted=true: %#v", row)
	}
	if _, err := PawnRow(draftReply(), identity, "Human2"); err == nil {
		t.Fatal("expected an error for a pawn id that does not match the single row")
	}
}

func TestPawnRowRejectsPartialOrFabricatedCorrelation(t *testing.T) {
	for _, mutation := range []string{"token", "entity", "context", "boolean"} {
		t.Run(mutation, func(t *testing.T) {
			value := draftReply()
			observed, _ := AsMap(value["observed"])
			context, _ := AsMap(observed["context"])
			identity, _ := AsMap(context["identity"])
			pawns := AsSlice(observed["pawns"])
			row, _ := AsMap(pawns[0])
			pawn, _ := AsMap(row["pawn"])
			snapshot, _ := AsMap(pawn["snapshot"])
			switch mutation {
			case "token":
				snapshot["token"] = ""
			case "entity":
				snapshot["entityId"] = "other"
			case "context":
				snapshotContext, _ := AsMap(snapshot["context"])
				snapshotContext["tick"] = "1"
			case "boolean":
				row["drafted"] = 1.0
			}
			if _, err := PawnRow(value, identity, "Human1"); err == nil {
				t.Fatalf("expected an error for mutation %q", mutation)
			}
		})
	}
}

func TestSameControlRejectsASecondTransition(t *testing.T) {
	for _, field := range []string{"token", "drafted"} {
		t.Run(field, func(t *testing.T) {
			before := draftRow(t)
			after := deepCopyMap(before)
			switch field {
			case "token":
				pawn, _ := AsMap(after["pawn"])
				snapshot, _ := AsMap(pawn["snapshot"])
				snapshot["token"] = "replacement"
			default:
				after["drafted"] = false
			}
			if err := SameControl(before, after); err == nil {
				t.Fatalf("expected an error for a changed %s", field)
			}
		})
	}
}

func TestActualOrderCompletedMoveComparesActualCurrentJobWithoutInventingGoto(t *testing.T) {
	external := map[string]any{"success": true, "accepted": true, "jobId": 58.0, "jobDef": "Wait_Combat"}
	pawn := map[string]any{"job": map[string]any{"loadId": "58", "defName": "Wait_Combat"}}
	if err := ActualOrder(external, pawn); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	for _, wrong := range []map[string]any{
		{"loadId": "59", "defName": "Wait_Combat"},
		{"loadId": "58", "defName": "Goto"},
	} {
		if err := ActualOrder(external, map[string]any{"job": wrong}); err == nil {
			t.Fatalf("expected an error for a fabricated job %#v", wrong)
		}
	}
	unaccepted := map[string]any{"success": true, "accepted": false, "jobId": 58.0, "jobDef": "Wait_Combat"}
	if err := ActualOrder(unaccepted, pawn); err == nil {
		t.Fatal("expected an error for an unaccepted external order")
	}
}

func TestActualOrderNoCurrentJobRequiresAbsence(t *testing.T) {
	external := map[string]any{"success": true, "accepted": true, "jobId": nil, "jobDef": nil}
	if err := ActualOrder(external, map[string]any{"job": map[string]any{"playerForced": false, "queuedJobs": 0.0}}); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if err := ActualOrder(external, map[string]any{"job": map[string]any{"playerForced": true}}); err == nil {
		t.Fatal("expected an error when playerForced is true with no reported job")
	}
	if err := ActualOrder(external, map[string]any{"job": map[string]any{"defName": "Goto"}}); err == nil {
		t.Fatal("expected an error when a defName is present with no reported jobId")
	}
}
