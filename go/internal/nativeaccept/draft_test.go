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
		"context":      rowContext(),
		"completeness": map[string]any{"page": map[string]any{"complete": true}},
		"pawns":        []any{row},
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
