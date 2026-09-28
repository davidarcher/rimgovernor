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
// row when pawnID is empty) and validates its snapshot invariants.
func PawnRow(reply map[string]any, identity map[string]any, pawnID string) (map[string]any, error) {
	_, observed, err := Outcome(reply, "observed")
	if err != nil {
		return nil, err
	}
	observedContext, _ := AsMap(observed["context"])
	if !DeepEqual(observedContext["identity"], identity) {
		return nil, fmt.Errorf("pawn read context identity mismatch")
	}
	rows := AsSlice(observed["pawns"])
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
	return row, nil
}

// Target builds the EntityTarget{entityId, expectedSnapshotToken} for row.
func Target(row map[string]any) map[string]any {
	pawn, _ := AsMap(row["pawn"])
	snapshot, _ := AsMap(pawn["snapshot"])
	return map[string]any{"entityId": pawn["id"], "expectedSnapshotToken": snapshot["token"]}
}

// SameControl asserts before and after describe the same draft control state
// (drafted flag and target/snapshot token).
func SameControl(before, after map[string]any) error {
	beforeDrafted, _ := before["drafted"].(bool)
	afterDrafted, _ := after["drafted"].(bool)
	if beforeDrafted != afterDrafted {
		return fmt.Errorf("drafted flag changed: %v -> %v", beforeDrafted, afterDrafted)
	}
	if !DeepEqual(Target(before), Target(after)) {
		return fmt.Errorf("pawn target/snapshot token changed unexpectedly")
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
