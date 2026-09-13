package nativeaccept

import "testing"

func rowContext() map[string]any {
	return map[string]any{"identity": map[string]any{"colonyId": "c"}, "tick": "10"}
}

func pawnRowFixture(drafted bool, ownedBy map[string]any) map[string]any {
	snapshot := map[string]any{"entityId": "pawn-1", "token": "tok-1", "context": rowContext()}
	claim := map[string]any{"unowned": map[string]any{}}
	if ownedBy != nil {
		claim = map[string]any{"owned": map[string]any{"claimId": "claim-1", "owner": ownedBy, "pawnSnapshot": snapshot}}
	}
	return map[string]any{
		"pawn":       map[string]any{"id": "pawn-1", "snapshot": snapshot},
		"drafted":    drafted,
		"draftClaim": claim,
	}
}

func observedReply(row map[string]any) map[string]any {
	return map[string]any{"observed": map[string]any{
		"context": rowContext(),
		"completeness": map[string]any{"page": map[string]any{"complete": true},
			"matched": "1", "returned": "1", "unreadable": "0"},
		"pawns": []any{row},
	}}
}

func TestPawnRowAcceptsUnownedRow(t *testing.T) {
	identity := map[string]any{"colonyId": "c"}
	row := pawnRowFixture(false, nil)
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
	reply := observedReply(pawnRowFixture(false, nil))
	if _, err := PawnRow(reply, identity, "other-pawn"); err == nil {
		t.Fatal("expected an error for a pawn id mismatch")
	}
}

func TestPawnRowRejectsMultipleDraftClaimCases(t *testing.T) {
	identity := map[string]any{"colonyId": "c"}
	row := pawnRowFixture(false, nil)
	row["draftClaim"] = map[string]any{"unowned": map[string]any{}, "owned": map[string]any{}}
	if _, err := PawnRow(observedReply(row), identity, ""); err == nil {
		t.Fatal("expected an error for a draftClaim with more than one case")
	}
}

func TestSameControlDetectsDraftedChange(t *testing.T) {
	before := pawnRowFixture(false, nil)
	after := pawnRowFixture(true, Owner)
	if err := SameControl(before, after); err == nil {
		t.Fatal("expected an error for a changed drafted flag")
	}
}

func TestSameControlAcceptsIdenticalRows(t *testing.T) {
	row := pawnRowFixture(true, Owner)
	if err := SameControl(row, row); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
}

func TestReleaseRequestRequiresOwnedByOwner(t *testing.T) {
	identity := map[string]any{"colonyId": "c"}
	unowned := pawnRowFixture(false, nil)
	if _, err := ReleaseRequest(identity, unowned); err == nil {
		t.Fatal("expected an error releasing an unowned row")
	}
	ownedByOther := pawnRowFixture(true, map[string]any{"controllerSessionId": "someone-else"})
	if _, err := ReleaseRequest(identity, ownedByOther); err == nil {
		t.Fatal("expected an error releasing a row owned by a different controller")
	}
	ownedByUs := pawnRowFixture(true, Owner)
	request, err := ReleaseRequest(identity, ownedByUs)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if AsString(request["expectedClaimId"]) != "claim-1" {
		t.Fatalf("wrong expectedClaimId: %#v", request)
	}
}

func TestOwnedEffectRejectsMismatchedPawn(t *testing.T) {
	row := pawnRowFixture(true, Owner)
	receipt := map[string]any{"applied": map[string]any{"observed": map[string]any{"job": map[string]any{
		"pawnId": "someone-else", "drafted": true, "verified": true, "issued": true,
		"draftClaimId": "claim-1", "draftOwner": "native-draft-acceptance", "resultingSnapshotToken": "tok-1",
	}}}}
	if err := OwnedEffect(receipt, row, "applied", true); err == nil {
		t.Fatal("expected an error for a mismatched pawnId")
	}
}

func TestOwnedEffectAcceptsMatchingReceipt(t *testing.T) {
	row := pawnRowFixture(true, Owner)
	receipt := map[string]any{"applied": map[string]any{"observed": map[string]any{"job": map[string]any{
		"pawnId": "pawn-1", "drafted": true, "verified": true, "issued": true,
		"draftClaimId": "claim-1", "draftOwner": "native-draft-acceptance", "resultingSnapshotToken": "tok-1",
	}}}}
	if err := OwnedEffect(receipt, row, "applied", true); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
}

func TestActualOrderRejectsUnacceptedFixture(t *testing.T) {
	row := pawnRowFixture(false, nil)
	external := map[string]any{"success": true, "accepted": false}
	if err := ActualOrder(external, row); err == nil {
		t.Fatal("expected an error for an unaccepted external order")
	}
}

// draftReply builds a PawnRow input mirroring test_native_draft_acceptance.py's
// reply() fixture: one owned-drafted row under a single complete page.
func draftReply() map[string]any {
	context := map[string]any{"identity": map[string]any{"colonyId": "colony", "loadToken": "load", "mapId": 0.0}, "tick": "0", "nativeGeneration": "1"}
	snapshot := map[string]any{"context": deepCopyMap(context), "entityId": "Human1", "token": "token"}
	row := map[string]any{
		"pawn": map[string]any{"id": "Human1", "snapshot": snapshot}, "drafted": true,
		"draftClaim": map[string]any{"owned": map[string]any{"claimId": "claim", "owner": deepCopyMap(Owner), "pawnSnapshot": deepCopyMap(snapshot)}},
	}
	return map[string]any{"observed": map[string]any{"context": context, "pawns": []any{row}, "completeness": map[string]any{
		"page": map[string]any{"complete": true}, "matched": "1", "returned": "1", "unreadable": "0",
	}}}
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

func TestPawnRowExactSnapshotAndClaimValidation(t *testing.T) {
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
	for _, mutation := range []string{"token", "entity", "context", "claim_snapshot", "unavailable", "boolean", "count", "page"} {
		t.Run(mutation, func(t *testing.T) {
			value := draftReply()
			observed, _ := AsMap(value["observed"])
			context, _ := AsMap(observed["context"])
			identity, _ := AsMap(context["identity"])
			pawns := AsSlice(observed["pawns"])
			row, _ := AsMap(pawns[0])
			pawn, _ := AsMap(row["pawn"])
			snapshot, _ := AsMap(pawn["snapshot"])
			completeness, _ := AsMap(observed["completeness"])
			switch mutation {
			case "token":
				snapshot["token"] = ""
			case "entity":
				snapshot["entityId"] = "other"
			case "context":
				snapshotContext, _ := AsMap(snapshot["context"])
				snapshotContext["tick"] = "1"
			case "claim_snapshot":
				claim, _ := AsMap(row["draftClaim"])
				owned, _ := AsMap(claim["owned"])
				pawnSnapshot, _ := AsMap(owned["pawnSnapshot"])
				pawnSnapshot["token"] = "stale"
			case "unavailable":
				row["draftClaim"] = map[string]any{"unavailable": map[string]any{}}
			case "boolean":
				row["drafted"] = 1.0
			case "count":
				completeness["matched"] = "2"
			case "page":
				page, _ := AsMap(completeness["page"])
				page["complete"] = false
			}
			if _, err := PawnRow(value, identity, "Human1"); err == nil {
				t.Fatalf("expected an error for mutation %q", mutation)
			}
		})
	}
}

func TestExecuteRequestUsesExactNativeTokenAndOriginalOwner(t *testing.T) {
	row := draftRow(t)
	pawn, _ := AsMap(row["pawn"])
	snapshot, _ := AsMap(pawn["snapshot"])
	snapshotContext, _ := AsMap(snapshot["context"])
	identity, _ := AsMap(snapshotContext["identity"])
	grant := map[string]any{"context": map[string]any{"nativeGeneration": "9"}, "leaseId": "lease"}
	request := ExecuteRequest(identity, grant, row, 7)
	operation, _ := AsMap(request["operation"])
	setDrafted, _ := AsMap(operation["setDrafted"])
	if !DeepEqual(setDrafted["pawn"], Target(row)) || setDrafted["drafted"] != true || setDrafted["allowPersistentDraft"] != false {
		t.Fatalf("unexpected setDrafted operation: %#v", setDrafted)
	}
	precondition, _ := AsMap(request["precondition"])
	if AsString(precondition["expectedGeneration"]) != "9" {
		t.Fatalf("expected precondition to carry the grant's nativeGeneration: %#v", precondition)
	}
	cleanup, err := ReleaseRequest(identity, row)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if !DeepEqual(cleanup["originalOwner"], Owner) {
		t.Fatalf("expected release_request to capture the original Owner: %#v", cleanup["originalOwner"])
	}
	claim, _ := AsMap(row["draftClaim"])
	owned, _ := AsMap(claim["owned"])
	owner, _ := AsMap(owned["owner"])
	owner["playerDirection"] = "2"
	if _, err := ReleaseRequest(identity, row); err == nil {
		t.Fatal("expected an error releasing a row whose owner no longer matches Owner")
	}
}

func TestSameControlRejectsASecondTransition(t *testing.T) {
	for _, field := range []string{"token", "claim", "drafted"} {
		t.Run(field, func(t *testing.T) {
			before := draftRow(t)
			after := deepCopyMap(before)
			switch field {
			case "token":
				pawn, _ := AsMap(after["pawn"])
				snapshot, _ := AsMap(pawn["snapshot"])
				snapshot["token"] = "replacement"
			case "claim":
				claim, _ := AsMap(after["draftClaim"])
				owned, _ := AsMap(claim["owned"])
				owned["claimId"] = "replacement"
			default:
				after["drafted"] = false
			}
			if err := SameControl(before, after); err == nil {
				t.Fatalf("expected an error for a changed %s", field)
			}
		})
	}
}

func TestOwnedEffectNoChangeMustBeVerifiedWithoutIssuingSetter(t *testing.T) {
	row := draftRow(t)
	effect := map[string]any{
		"pawnId": "Human1", "drafted": true, "verified": true, "issued": false,
		"draftClaimId": "claim", "draftOwner": Owner["controllerSessionId"], "resultingSnapshotToken": "token",
	}
	receipt := map[string]any{"noChange": map[string]any{"observed": map[string]any{"job": effect}}}
	if err := OwnedEffect(receipt, row, "noChange", false); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	effect["issued"] = true
	if err := OwnedEffect(receipt, row, "noChange", false); err == nil {
		t.Fatal("expected an error when a no-change effect actually issued a setter")
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
