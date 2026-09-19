// The apply/refusal case (#242, #252) executes one routine write per kind
// with a snapshot token that was valid when the fixture read it, after the
// fixture has moved the world under that token, and asserts the refusal
// names the fact that moved (the reason strings in
// docs/developers/contracts/action-contracts.md) rather than the token.
// The token comparison is the closing rule of every kind; the zone-creation
// and excavation steps reach it by moving only what the token hashes, so
// their documented closing reasons are asserted too. Every refusal is a
// terminal failure reply, never a receipt.
package apply

import (
	"context"
	"fmt"
	"strings"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

const sessionOwner = "native-apply-refusal-acceptance"

func init() {
	cases.Register(cases.Case{
		Name: "apply/refusal",
		Scope: "Apply-time precondition refusals (#242, #252): for zone cell edit, stockpile patch, zone deletion, zone creation, " +
			"Allow, haul, work settings, bills, build, hunt, tame, grower crop, plant and mine acquisition, excavation and wall " +
			"removal, a write whose token was valid when read is executed after the fixture moved the world and is refused " +
			"with the documented reason naming the moved fact; the acquisition writes are refused the same way without a " +
			"token, as live dispatch sends them (#243).",
		// clutter plants every bare cell around the colonist before the
		// fixture stages its interior, so the initial map cannot supply
		// one untouched and the fixture's own clearing is exercised
		// (#441); the case asserts the planting happened.
		Start:  cases.Fixture{Op: "test/apply_refusal_prepare", Args: map[string]any{"clutter": true}},
		Budget: 5 * time.Minute,
		Run:    run,
	})
}

func run(ctx context.Context, s cases.Session) error {
	report := s.Report()
	h := s.Harness()
	identity := s.Identity()
	prepared := s.Prepared()
	for _, required := range []string{"rimgovernor/operations_execute", "rimgovernor/operations_preview", "rimgovernor/observations_list_pawns", "test/apply_refusal_move"} {
		if !na.Contains(s.Names(), required) {
			return fmt.Errorf("missing %s in discovery", required)
		}
	}
	str := func(key string) (string, error) {
		value := na.AsString(prepared[key])
		if value == "" {
			return "", fmt.Errorf("prepare: missing %s: %#v", key, prepared)
		}
		return value, nil
	}
	cellsOf := func(key string) ([]map[string]any, error) {
		rows := na.AsSlice(prepared[key])
		if len(rows) == 0 {
			return nil, fmt.Errorf("prepare: missing %s: %#v", key, prepared)
		}
		var cells []map[string]any
		for _, row := range rows {
			cell, _ := na.AsMap(row)
			cells = append(cells, map[string]any{"x": cell["x"], "z": cell["z"]})
		}
		return cells, nil
	}
	if planted := na.AsNumber(prepared["clutterPlanted"]); planted <= 0 {
		return fmt.Errorf("prepare: clutter planted nothing, the initial map supplied the interior untouched: %#v", prepared["clutterPlanted"])
	}
	report["clutter_planted"] = prepared["clutterPlanted"]
	zoneCells, err := cellsOf("zoneCells")
	if err != nil {
		return err
	}
	freeCells, err := cellsOf("freeCells")
	if err != nil {
		return err
	}
	cellOf := func(key string) map[string]any {
		cell, _ := na.AsMap(prepared[key])
		return map[string]any{"x": cell["x"], "z": cell["z"]}
	}
	plantCell, rockCell, preyCell, buildCell := cellOf("plantCell"), cellOf("rockCell"), cellOf("preyCell"), cellOf("buildCell")
	tokens := map[string]string{}
	for _, key := range []string{"zoneId", "zoneToken", "mapToken", "wallId", "wallToken", "itemId", "itemToken", "haulItemId", "haulItemToken",
		"pawnId", "pawnWorkToken", "plantId", "plantToken", "plantResource", "rockId", "rockToken", "rockResource", "rockDef", "excavateToken",
		"benchId", "benchToken", "preyId", "preyResource", "tameId", "tameToken", "tameCensusToken", "growerId", "growerToken", "growerCrop"} {
		if tokens[key], err = str(key); err != nil {
			return err
		}
	}

	if _, err := na.GrantAuto(ctx, h.WireFunc(), "acquire", identity); err != nil {
		return err
	}
	generation := func(label string) (any, error) {
		reply, err := h.Wire(ctx, label, "authority_read_status", map[string]any{"identity": identity})
		if err != nil {
			return nil, err
		}
		_, status, err := na.Outcome(reply, "status")
		if err != nil {
			return nil, err
		}
		statusContext, _ := na.AsMap(status["context"])
		return statusContext["nativeGeneration"], nil
	}
	move := func(label string, args map[string]any) error {
		result, err := h.Call(ctx, label, "test/apply_refusal_move", args)
		if err != nil {
			return err
		}
		if success, _ := na.AsBool(result["success"]); !success {
			return fmt.Errorf("%s: fixture move refused: %#v", label, result)
		}
		return nil
	}
	// refused executes operation under a fresh generation and asserts the
	// reply is a failure whose code and detail are the documented ones.
	refused := func(label string, operation map[string]any, code, detail string) error {
		current, err := generation("generation-" + label)
		if err != nil {
			return err
		}
		reply, err := h.Wire(ctx, "execute-"+label, "operations_execute", map[string]any{
			"precondition": map[string]any{
				"identity": identity, "expectedGeneration": current,
				"attempt": map[string]any{"controllerSessionId": sessionOwner, "actionId": label, "attemptId": "1"},
			},
			"operation": operation,
		})
		if err != nil {
			return err
		}
		_, failure, err := na.Outcome(reply, "failure")
		if err != nil {
			return fmt.Errorf("%s: expected a failure reply, got %#v", label, reply)
		}
		got := na.AsString(failure["detail"])
		if na.AsString(failure["code"]) != code || !strings.Contains(got, detail) {
			return fmt.Errorf("%s: expected %s %q, got %s %q", label, code, detail, na.AsString(failure["code"]), got)
		}
		report[strings.ReplaceAll(label, "-", "_")] = got
		return nil
	}
	// preview evaluates operation and returns whether it was accepted and,
	// when not, the reason the evaluation carries.
	preview := func(label string, operation map[string]any) (bool, string, error) {
		reply, err := h.Wire(ctx, "preview-"+label, "operations_preview", map[string]any{"identity": identity, "operation": operation})
		if err != nil {
			return false, "", err
		}
		evaluated, ok := na.AsMap(reply["evaluated"])
		if !ok {
			return false, "", fmt.Errorf("%s: expected an evaluated preview, got %#v", label, reply)
		}
		accepted, _ := na.AsBool(evaluated["accepted"])
		return accepted, na.AsString(evaluated["reason"]), nil
	}
	at := func(cell map[string]any) string { return fmt.Sprintf("(%v, %v)", cell["x"], cell["z"]) }
	zone := map[string]any{"entityId": tokens["zoneId"], "expectedSnapshotToken": tokens["zoneToken"]}

	// The haul order carries the pawn's control snapshot, which only the
	// pawn listing reports; it is read before the fixture drafts the pawn.
	pawnToken, err := controlToken(ctx, h, identity, tokens["pawnId"])
	if err != nil {
		return err
	}

	// Build: the open build cell is walled over after the preview accepted
	// it; the execute refusal is the re-planned preview's own reason.
	placement := map[string]any{"placeBuilding": map[string]any{"placement": map[string]any{
		"defName": "Wall", "stuff": "WoodLog", "x": buildCell["x"], "z": buildCell["z"], "rotation": "ROTATION_NORTH",
	}}}
	if accepted, reason, err := preview("build-open", placement); err != nil {
		return err
	} else if !accepted {
		return fmt.Errorf("build-open: expected the open build cell to be accepted, got %q", reason)
	}
	if err := move("fill-build-cell", map[string]any{"action": "fill_cell", "x": buildCell["x"], "z": buildCell["z"]}); err != nil {
		return err
	}
	accepted, reason, err := preview("build-blocked", placement)
	if err != nil {
		return err
	}
	if accepted || reason == "" {
		return fmt.Errorf("build-blocked: expected a refused preview with a reason, got accepted=%v %q", accepted, reason)
	}
	if err := refused("build", placement, "FAILURE_CODE_INVALID_REQUEST", reason); err != nil {
		return err
	}

	// Zone cell edit: the free roofed cell is walled over after the read.
	if err := move("fill-free-cell", map[string]any{"action": "fill_cell", "x": freeCells[0]["x"], "z": freeCells[0]["z"]}); err != nil {
		return err
	}
	if err := refused("zone-edit-add", map[string]any{"editZoneCells": map[string]any{
		"zone": zone, "edit": "CELL_EDIT_ADD", "cells": map[string]any{"explicitCells": map[string]any{"cells": []map[string]any{freeCells[0]}}},
	}}, "FAILURE_CODE_INVALID_REQUEST", "Zone cell edit refused: cell "+at(freeCells[0])+" is not free zoneable ground"); err != nil {
		return err
	}

	// Stockpile patch, zone deletion and a cell removal: the zone is gone.
	if err := move("delete-zone", map[string]any{"action": "delete_zone", "id": tokens["zoneId"]}); err != nil {
		return err
	}
	if err := refused("stockpile-patch", map[string]any{"patchStockpile": map[string]any{
		"zone": zone, "settings": map[string]any{"priority": "STORAGE_PRIORITY_IMPORTANT"},
	}}, "FAILURE_CODE_NOT_FOUND", "Stockpile patch refused: the exact stockpile zone no longer exists on this map"); err != nil {
		return err
	}
	if err := refused("zone-delete", map[string]any{"deleteZone": map[string]any{"zone": zone}},
		"FAILURE_CODE_NOT_FOUND", "Zone deletion refused: the exact zone no longer exists on this map"); err != nil {
		return err
	}
	if err := refused("zone-edit-remove", map[string]any{"editZoneCells": map[string]any{
		"zone": zone, "edit": "CELL_EDIT_REMOVE", "cells": map[string]any{"explicitCells": map[string]any{"cells": []map[string]any{zoneCells[0]}}},
	}}, "FAILURE_CODE_NOT_FOUND", "Zone cell edit refused: the exact zone no longer exists on this map"); err != nil {
		return err
	}

	// Zone creation: the freed cells are fresh ground again, so every cell
	// rule holds and the deletion is caught by the closing census rule.
	if err := refused("zone-create", map[string]any{"createZone": map[string]any{
		"expectedMapSnapshotToken": tokens["mapToken"], "label": "RimGovernor apply refusal", "type": "ZONE_TYPE_STOCKPILE",
		"cells":     map[string]any{"explicitCells": map[string]any{"cells": zoneCells}},
		"stockpile": map[string]any{"priority": "STORAGE_PRIORITY_NORMAL", "preset": "FILTER_PRESET_NOTHING"},
	}}, "FAILURE_CODE_INVALID_REQUEST", "Zone creation refused: the map's zone census changed since it was read"); err != nil {
		return err
	}

	// Allow: the item was unforbidden after the read.
	if err := move("allow-item", map[string]any{"action": "allow_item", "id": tokens["itemId"]}); err != nil {
		return err
	}
	if err := refused("allow", map[string]any{"designateThing": map[string]any{
		"target": map[string]any{"entityId": tokens["itemId"], "expectedSnapshotToken": tokens["itemToken"]}, "designation": "THING_DESIGNATION_ALLOW",
	}}, "FAILURE_CODE_INVALID_REQUEST", "Allow refused: the item already has the desired forbid state"); err != nil {
		return err
	}

	// Haul and work settings: the colonist was drafted after the read.
	if err := move("draft-pawn", map[string]any{"action": "draft_pawn", "id": tokens["pawnId"]}); err != nil {
		return err
	}
	if err := refused("haul", map[string]any{"pawnTargetOrder": map[string]any{
		"pawn":   map[string]any{"entityId": tokens["pawnId"], "expectedSnapshotToken": pawnToken},
		"target": map[string]any{"entityId": tokens["haulItemId"], "expectedSnapshotToken": tokens["haulItemToken"]},
		"kind":   "PAWN_ORDER_KIND_HAUL", "requireSafeStorage": true,
	}}, "FAILURE_CODE_INVALID_REQUEST", "Haul refused: the pawn is drafted"); err != nil {
		return err
	}
	if err := refused("work-settings", map[string]any{"patchPawn": map[string]any{
		"pawn": map[string]any{"entityId": tokens["pawnId"], "expectedSnapshotToken": tokens["pawnWorkToken"]},
		"work": []map[string]any{{"workTypeDef": "Hauling", "priority": 3}},
	}}, "FAILURE_CODE_INVALID_REQUEST", "Work settings refused: the pawn is drafted"); err != nil {
		return err
	}
	if err := move("undraft-pawn", map[string]any{"action": "undraft_pawn", "id": tokens["pawnId"]}); err != nil {
		return err
	}

	// Bills: the fixture added the butcher bill after the bench was read.
	if err := refused("bill", map[string]any{"addBill": map[string]any{
		"bench":     map[string]any{"entityId": tokens["benchId"], "expectedSnapshotToken": tokens["benchToken"]},
		"recipeDef": "ButcherCorpseFlesh",
		"settings": map[string]any{"repeatMode": "REPEAT_MODE_FOREVER", "suspended": false, "ingredientSearchRadius": 40,
			"store": map[string]any{"mode": "STORE_MODE_DROP_ON_FLOOR"}},
	}}, "FAILURE_CODE_INVALID_REQUEST", "Production bill requires unchanged native bench, available recipe and assigned skilled worker: "+
		"bench already carries a ButcherCorpseFlesh bill"); err != nil {
		return err
	}

	// Hunt: the butcher bill was suspended, then the hare was designated,
	// after the read. The order is sent as live dispatch sends it (#243),
	// without the animal token the pawn listing does not report.
	hunt := map[string]any{"acquireResource": map[string]any{
		"source":          map[string]any{"entityId": tokens["preyId"]},
		"resourceDefName": tokens["preyResource"], "cell": preyCell,
	}}
	if err := move("suspend-bills", map[string]any{"action": "suspend_bills", "id": tokens["benchId"]}); err != nil {
		return err
	}
	if err := refused("hunt-unbutchered", hunt, "FAILURE_CODE_INVALID_REQUEST", "Hunt refused: no usable butcher bill with an assigned cook accepts the corpse"); err != nil {
		return err
	}
	if err := move("designate-hunt", map[string]any{"action": "designate_hunt", "id": tokens["preyId"]}); err != nil {
		return err
	}
	if err := refused("hunt", hunt, "FAILURE_CODE_INVALID_REQUEST", "Hunt refused: the animal is already designated for hunting"); err != nil {
		return err
	}

	// Tame: the hare was designated after the read.
	if err := move("designate-tame", map[string]any{"action": "designate_tame", "id": tokens["tameId"]}); err != nil {
		return err
	}
	if err := refused("tame", map[string]any{"tameAnimal": map[string]any{
		"animal": map[string]any{"entityId": tokens["tameId"], "expectedSnapshotToken": tokens["tameToken"]}, "expectedCensusToken": tokens["tameCensusToken"],
	}}, "FAILURE_CODE_INVALID_REQUEST", "Native tame eligibility refused the animal or it is already designated."); err != nil {
		return err
	}

	// Grower crop: the plant pot is gone.
	if err := move("destroy-grower", map[string]any{"action": "destroy_thing", "id": tokens["growerId"]}); err != nil {
		return err
	}
	if err := refused("grower-crop", map[string]any{"patchBuilding": map[string]any{
		"building": map[string]any{"entityId": tokens["growerId"], "expectedSnapshotToken": tokens["growerToken"]}, "plantDef": tokens["growerCrop"],
	}}, "FAILURE_CODE_NOT_FOUND", "Exact plant grower is unavailable."); err != nil {
		return err
	}

	// Plant and mine acquisition: each target was designated after the read.
	if err := move("designate-plant", map[string]any{"action": "designate_plant", "id": tokens["plantId"]}); err != nil {
		return err
	}
	if err := refused("plant", map[string]any{"acquireResource": map[string]any{
		"source":          map[string]any{"entityId": tokens["plantId"], "expectedSnapshotToken": tokens["plantToken"]},
		"resourceDefName": tokens["plantResource"], "cell": plantCell,
	}}, "FAILURE_CODE_INVALID_REQUEST", "Plant acquisition refused: the plant is already designated"); err != nil {
		return err
	}
	if err := move("designate-rock", map[string]any{"action": "designate_rock", "id": tokens["rockId"]}); err != nil {
		return err
	}
	if err := refused("mine", map[string]any{"acquireResource": map[string]any{
		"source":          map[string]any{"entityId": tokens["rockId"], "expectedSnapshotToken": tokens["rockToken"]},
		"resourceDefName": tokens["rockResource"], "cell": rockCell,
	}}, "FAILURE_CODE_INVALID_REQUEST", "Mine refused: the rock is already designated for mining"); err != nil {
		return err
	}
	// Live dispatch (#243) omits the acquisition token: the rules alone
	// refuse the moved world, with the same reason.
	if err := refused("plant-untokened", map[string]any{"acquireResource": map[string]any{
		"source":          map[string]any{"entityId": tokens["plantId"]},
		"resourceDefName": tokens["plantResource"], "cell": plantCell,
	}}, "FAILURE_CODE_INVALID_REQUEST", "Plant acquisition refused: the plant is already designated"); err != nil {
		return err
	}
	if err := refused("mine-untokened", map[string]any{"acquireResource": map[string]any{
		"source":          map[string]any{"entityId": tokens["rockId"]},
		"resourceDefName": tokens["rockResource"], "cell": rockCell,
	}}, "FAILURE_CODE_INVALID_REQUEST", "Mine refused: the rock is already designated for mining"); err != nil {
		return err
	}

	// Excavation: the mining designation moved the rock's cell token, and
	// no earlier rule names a designation, so the closing token rule fires.
	if err := refused("excavate", map[string]any{"excavateCell": map[string]any{
		"cell": rockCell, "expectedMineableDefName": tokens["rockDef"], "expectedSnapshotToken": tokens["excavateToken"],
	}}, "FAILURE_CODE_STALE_IDENTITY", "Rock snapshot changed; observe before new admission."); err != nil {
		return err
	}

	// Wall removal: the interior wall is gone.
	if err := move("destroy-wall", map[string]any{"action": "destroy_thing", "id": tokens["wallId"]}); err != nil {
		return err
	}
	if err := refused("remove-wall", map[string]any{"removeWall": map[string]any{
		"wall": map[string]any{"entityId": tokens["wallId"], "expectedSnapshotToken": tokens["wallToken"]},
	}}, "FAILURE_CODE_NOT_FOUND", "No spawned colonist wall with that id is on the current map."); err != nil {
		return err
	}
	return nil
}

// controlToken reads pawnID's control snapshot token from the pawn listing,
// the token a haul order carries for its pawn.
func controlToken(ctx context.Context, h *na.Harness, identity map[string]any, pawnID string) (string, error) {
	reply, err := h.Wire(ctx, "list-pawn", "observations_list_pawns", map[string]any{
		"scope":   map[string]any{"expectedIdentity": identity},
		"filter":  map[string]any{"ids": []string{pawnID}},
		"details": map[string]any{},
		"page":    map[string]any{"limit": 1},
	})
	if err != nil {
		return "", err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return "", err
	}
	rows := na.AsSlice(observed["pawns"])
	if len(rows) != 1 {
		return "", fmt.Errorf("list-pawn: expected exactly one observed pawn, got %#v", observed)
	}
	row, _ := na.AsMap(rows[0])
	pawn, _ := na.AsMap(row["pawn"])
	snapshot, _ := na.AsMap(pawn["snapshot"])
	token := na.AsString(snapshot["token"])
	if na.AsString(pawn["id"]) != pawnID || token == "" {
		return "", fmt.Errorf("list-pawn: missing control snapshot for %s: %#v", pawnID, row)
	}
	return token, nil
}
