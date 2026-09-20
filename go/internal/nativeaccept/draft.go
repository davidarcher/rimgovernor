package nativeaccept

import "fmt"

// Controller is the fixed attempt controllerSessionId used by the
// draft/combat/movement acceptance binaries. Since #52 there is no authority
// owner token on the wire (authority is SetMode(Auto|Manual) plus generation
// continuity); this only keys attempts and typed clock epochs.
const Controller = "native-draft-acceptance"

// Owner is the legacy map form of Controller kept for the acceptance binaries
// that still spell na.Owner["controllerSessionId"]; it carries no
// playerDirection because that concept was removed with the authority lease.
var Owner = map[string]any{"controllerSessionId": Controller}

// PawnRow asserts an observations_list_pawns reply is a single complete, exact-match
// page whose context matches identity, extracts the row for pawnID (the first
// row when pawnID is empty) and validates its snapshot/draftClaim invariants.
func PawnRow(reply map[string]any, identity map[string]any, pawnID string) (map[string]any, error) {
	_, observed, err := Outcome(reply, "observed")
	if err != nil {
		return nil, err
	}
	observedContext, _ := AsMap(observed["context"])
	if !DeepEqual(observedContext["identity"], identity) {
		return nil, fmt.Errorf("pawn read context identity mismatch")
	}
	completeness, _ := AsMap(observed["completeness"])
	page, _ := AsMap(completeness["page"])
	if complete, _ := AsBool(page["complete"]); !complete || len(page) != 1 {
		return nil, fmt.Errorf("expected a single complete page, found %#v", page)
	}
	rows := AsSlice(observed["pawns"])
	matched, returned, unreadable := AsNumber(completeness["matched"]), AsNumber(completeness["returned"]), AsNumber(completeness["unreadable"])
	if matched != returned || int(returned) != len(rows) || unreadable != 0 {
		return nil, fmt.Errorf("completeness does not exactly account for the returned rows: %#v", completeness)
	}
	if len(rows) == 0 {
		return nil, fmt.Errorf("expected at least one pawn row")
	}
	row, _ := AsMap(rows[0])
	pawn, _ := AsMap(row["pawn"])
	if pawnID != "" {
		// A multi-id filter returns one row per id; pick the requested one.
		row, pawn = nil, nil
		for _, candidate := range rows {
			r, _ := AsMap(candidate)
			p, _ := AsMap(r["pawn"])
			if AsString(p["id"]) == pawnID {
				if row != nil {
					return nil, fmt.Errorf("expected exactly one pawn for id %q, found %d", pawnID, len(rows))
				}
				row, pawn = r, p
			}
		}
		if row == nil {
			return nil, fmt.Errorf("expected pawn %q among %d rows", pawnID, len(rows))
		}
	}
	snapshot, _ := AsMap(pawn["snapshot"])
	if AsString(snapshot["entityId"]) != AsString(pawn["id"]) || AsString(snapshot["token"]) == "" {
		return nil, fmt.Errorf("pawn snapshot missing entityId/token: %#v", snapshot)
	}
	if !DeepEqual(snapshot["context"], observed["context"]) {
		return nil, fmt.Errorf("pawn snapshot context does not match the observed read context")
	}
	if _, ok := row["drafted"].(bool); !ok {
		return nil, fmt.Errorf("pawn row drafted must be an explicit boolean")
	}
	claim, _ := AsMap(row["draftClaim"])
	if len(claim) != 1 {
		return nil, fmt.Errorf("draftClaim must have exactly one case: %#v", claim)
	}
	if owned, ok := AsMap(claim["owned"]); ok {
		if !DeepEqual(owned["pawnSnapshot"], snapshot) {
			return nil, fmt.Errorf("owned draft claim's pawnSnapshot does not match the row's snapshot")
		}
		if AsString(owned["claimId"]) == "" {
			return nil, fmt.Errorf("owned draft claim missing claimId")
		}
	} else if _, ok := claim["unowned"]; !ok {
		return nil, fmt.Errorf("draftClaim is neither owned nor unowned: %#v", claim)
	}
	return row, nil
}

// Target builds the EntityTarget{entityId, expectedSnapshotToken} for row.
func Target(row map[string]any) map[string]any {
	pawn, _ := AsMap(row["pawn"])
	snapshot, _ := AsMap(pawn["snapshot"])
	return map[string]any{"entityId": pawn["id"], "expectedSnapshotToken": snapshot["token"]}
}

// ExecuteRequest builds an operations_execute setDrafted request at grant's
// native generation (grant is an authority_control "granted" body). Callers
// overwrite ["operation"] for non-draft operations (attack, movement), building
// on this shared precondition/attempt shape.
func ExecuteRequest(identity, grant, row map[string]any, number int) map[string]any {
	context, _ := AsMap(grant["context"])
	return map[string]any{
		"precondition": map[string]any{
			"identity":           identity,
			"expectedGeneration": context["nativeGeneration"],
			"attempt": map[string]any{
				"controllerSessionId": Controller,
				"actionId":            fmt.Sprintf("draft-%d", number),
				"attemptId":           "1",
			},
		},
		"operation": map[string]any{"setDrafted": map[string]any{
			"pawn": Target(row), "drafted": true, "allowPersistentDraft": false,
		}},
	}
}

// ReleaseRequest builds an operations_release_owned_draft request for row's current
// owned claim. A claim's existence is the ownership proof (there is only one bot
// process); no owner token is carried since #52.
func ReleaseRequest(identity, row map[string]any) (map[string]any, error) {
	draftClaim, _ := AsMap(row["draftClaim"])
	claim, ok := AsMap(draftClaim["owned"])
	if !ok {
		return nil, fmt.Errorf("release_request requires an owned draft claim, found %#v", draftClaim)
	}
	return map[string]any{
		"identity": identity, "pawn": Target(row), "expectedClaimId": claim["claimId"],
	}, nil
}

// SameControl asserts before and after describe the same draft control state
// (drafted flag, target/snapshot token, and full draftClaim).
func SameControl(before, after map[string]any) error {
	beforeDrafted, _ := before["drafted"].(bool)
	afterDrafted, _ := after["drafted"].(bool)
	if beforeDrafted != afterDrafted {
		return fmt.Errorf("drafted flag changed: %v -> %v", beforeDrafted, afterDrafted)
	}
	if !DeepEqual(Target(before), Target(after)) {
		return fmt.Errorf("pawn target/snapshot token changed unexpectedly")
	}
	if !DeepEqual(before["draftClaim"], after["draftClaim"]) {
		return fmt.Errorf("draftClaim changed unexpectedly")
	}
	return nil
}

// ActualOrder asserts a test/b04f_setup external-order fixture reply was accepted and
// agrees with row's observed job.
func ActualOrder(external, row map[string]any) error {
	success, _ := AsBool(external["success"])
	accepted, _ := AsBool(external["accepted"])
	if !success || !accepted {
		return fmt.Errorf("external order fixture was not accepted: %#v", external)
	}
	job, _ := AsMap(row["job"])
	if external["jobId"] == nil {
		if external["jobDef"] != nil {
			return fmt.Errorf("expected no jobDef alongside a nil jobId")
		}
		if _, present := job["loadId"]; present {
			return fmt.Errorf("expected no loadId for a jobless external order")
		}
		if _, present := job["defName"]; present {
			return fmt.Errorf("expected no defName for a jobless external order")
		}
		if playerForced, _ := AsBool(job["playerForced"]); playerForced {
			return fmt.Errorf("expected playerForced=false for a jobless external order")
		}
		return nil
	}
	if AsString(job["loadId"]) != fmt.Sprint(external["jobId"]) {
		return fmt.Errorf("job.loadId does not match the external order's jobId")
	}
	if AsString(job["defName"]) != AsString(external["jobDef"]) {
		return fmt.Errorf("job.defName does not match the external order's jobDef")
	}
	return nil
}

// OwnedEffect asserts an operations_execute receipt's named case (default "applied")
// describes row's own owned draft claim being set with the given issued value.
func OwnedEffect(receipt, row map[string]any, caseName string, issued bool) error {
	if caseName == "" {
		caseName = "applied"
	}
	caseValue, _ := AsMap(receipt[caseName])
	observed, _ := AsMap(caseValue["observed"])
	effect, _ := AsMap(observed["job"])
	draftClaim, _ := AsMap(row["draftClaim"])
	claim, _ := AsMap(draftClaim["owned"])
	drafted, _ := row["drafted"].(bool)
	if !drafted || claim == nil {
		return fmt.Errorf("row is not owned-drafted")
	}
	pawn, _ := AsMap(row["pawn"])
	if AsString(effect["pawnId"]) != AsString(pawn["id"]) {
		return fmt.Errorf("effect pawnId does not match row")
	}
	effectDrafted, _ := effect["drafted"].(bool)
	effectVerified, _ := effect["verified"].(bool)
	effectIssued, _ := effect["issued"].(bool)
	if !effectDrafted || !effectVerified || effectIssued != issued {
		return fmt.Errorf("effect drafted/verified/issued mismatch: %#v", effect)
	}
	if AsString(effect["draftClaimId"]) != AsString(claim["claimId"]) {
		return fmt.Errorf("effect draftClaimId does not match row's owned claim")
	}
	target := Target(row)
	if AsString(effect["resultingSnapshotToken"]) != AsString(target["expectedSnapshotToken"]) {
		return fmt.Errorf("effect resultingSnapshotToken does not match row's current snapshot")
	}
	return nil
}
