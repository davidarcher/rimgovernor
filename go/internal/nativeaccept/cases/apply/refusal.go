// The apply/refusal case (#242) executes one routine write per kind with a
// snapshot token that was valid when the fixture read it, after the fixture
// has moved the world under that token, and asserts the refusal names the
// fact that moved (the reason strings in
// docs/developers/contracts/action-contracts.md) rather than the token.
// The token comparison is the closing rule of every kind; the zone-creation
// step reaches it by moving only the census, so its documented reason is
// asserted too. Every refusal is a terminal failure reply, never a receipt.
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
		Scope: "Apply-time precondition refusals (#242): for zone cell edit, stockpile patch, zone deletion, zone creation, " +
			"Allow, work settings, plant and mine acquisition, a write whose token was valid when read is executed after " +
			"the fixture moved the world and is refused with the documented reason naming the moved fact.",
		Start:  cases.Fixture{Op: "test/apply_refusal_prepare"},
		Budget: 5 * time.Minute,
		Run:    run,
	})
}

func run(ctx context.Context, s cases.Session) error {
	report := s.Report()
	h := s.Harness()
	identity := s.Identity()
	prepared := s.Prepared()
	for _, required := range []string{"rimgovernor/operations_execute", "test/apply_refusal_move"} {
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
	zoneCells, err := cellsOf("zoneCells")
	if err != nil {
		return err
	}
	freeCells, err := cellsOf("freeCells")
	if err != nil {
		return err
	}
	plantCell, _ := na.AsMap(prepared["plantCell"])
	rockCell, _ := na.AsMap(prepared["rockCell"])
	tokens := map[string]string{}
	for _, key := range []string{"zoneId", "zoneToken", "mapToken", "itemId", "itemToken", "pawnId", "pawnWorkToken",
		"plantId", "plantToken", "plantResource", "rockId", "rockToken", "rockResource"} {
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
	at := func(cell map[string]any) string { return fmt.Sprintf("(%v, %v)", cell["x"], cell["z"]) }
	zone := map[string]any{"entityId": tokens["zoneId"], "expectedSnapshotToken": tokens["zoneToken"]}

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
	}}, "FAILURE_CODE_INVALID_REQUEST", "Allow refused: the item is already allowed"); err != nil {
		return err
	}

	// Work settings: the colonist was drafted after the read.
	if err := move("draft-pawn", map[string]any{"action": "draft_pawn", "id": tokens["pawnId"]}); err != nil {
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

	// Plant and mine acquisition: each target was designated after the read.
	if err := move("designate-plant", map[string]any{"action": "designate_plant", "id": tokens["plantId"]}); err != nil {
		return err
	}
	if err := refused("plant", map[string]any{"acquireResource": map[string]any{
		"source": map[string]any{"entityId": tokens["plantId"], "expectedSnapshotToken": tokens["plantToken"]},
		"resourceDefName": tokens["plantResource"], "cell": map[string]any{"x": plantCell["x"], "z": plantCell["z"]},
	}}, "FAILURE_CODE_INVALID_REQUEST", "Plant acquisition refused: the plant is already designated"); err != nil {
		return err
	}
	if err := move("designate-rock", map[string]any{"action": "designate_rock", "id": tokens["rockId"]}); err != nil {
		return err
	}
	if err := refused("mine", map[string]any{"acquireResource": map[string]any{
		"source": map[string]any{"entityId": tokens["rockId"], "expectedSnapshotToken": tokens["rockToken"]},
		"resourceDefName": tokens["rockResource"], "cell": map[string]any{"x": rockCell["x"], "z": rockCell["z"]},
	}}, "FAILURE_CODE_INVALID_REQUEST", "Mine refused: the rock is already designated for mining"); err != nil {
		return err
	}
	return nil
}
