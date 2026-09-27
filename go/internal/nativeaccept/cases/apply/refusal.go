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
		Scope: "Apply-time precondition refusals (#242, #252): for zone cell edit, stockpile patch, zone creation, " +
			"Allow, haul, work settings, bills, build, hunt, tame, grower crop, plant and mine acquisition, " +
			"a write whose token was valid when read is executed after the fixture moved the world and is refused " +
			"with the documented reason naming the moved fact; the acquisition writes are refused the same way without a " +
			"token, as live dispatch sends them (#243).",
		// clutter plants every bare cell around the colonist before the
		// fixture stages its interior, so the initial map cannot supply
		// one untouched and the fixture's own clearing is exercised
		// (#441); the case asserts the planting happened.
		Start:  cases.Fixture{Op: "test/apply_refusal_prepare", Args: map[string]any{"clutter": true}, On: cases.LabStart()},
		Budget: 5 * time.Minute,
		Run:    run,
	})
}

func run(ctx context.Context, s cases.Session) error {
	report := s.Report()
	h := s.Harness()
	identity := s.Identity()
	prepared := s.Prepared()
	for _, required := range []string{"rimgovernor/operations_execute", "rimgovernor/operations_apply", "rimgovernor/placement_preview", "rimgovernor/observations_list_pawns", "test/apply_refusal_move"} {
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
	for _, key := range []string{"zoneId", "wallId", "wallToken", "itemId", "itemToken", "haulItemId", "haulItemToken",
		"pawnId", "pawnWorkToken", "plantId", "plantToken", "plantResource", "rockId", "rockToken", "rockResource", "rockDef",
		"benchId", "benchToken", "preyId", "preyResource", "tameId", "growerId", "growerCrop"} {
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
	// intentRefused applies one Actions/Apply intent and asserts its result
	// is a refusal whose code and reason are the documented ones.
	intentRefused := func(label string, action map[string]any, code, reason string) error {
		action["key"] = "refusal-" + label
		reply, err := h.Wire(ctx, "apply-"+label, "operations_apply", map[string]any{"identity": identity, "actions": []any{action}})
		if err != nil {
			return err
		}
		results := na.AsSlice(reply["results"])
		if len(results) != 1 {
			return fmt.Errorf("%s: expected one result, got %#v", label, reply)
		}
		result, _ := na.AsMap(results[0])
		refusal, ok := na.AsMap(result["refused"])
		got := na.AsString(refusal["reason"])
		if !ok || na.AsString(refusal["code"]) != code || !strings.Contains(got, reason) {
			return fmt.Errorf("%s: expected %s %q, got %#v", label, code, reason, result)
		}
		report[strings.ReplaceAll(label, "-", "_")] = got
		return nil
	}
	at := func(cell map[string]any) string { return fmt.Sprintf("(%v, %v)", cell["x"], cell["z"]) }

	// Build: the open build cell is walled over after the placement preview
	// accepted it; the Actions/Apply building intent (#856) is refused with
	// the re-planned placement's own reason. A Campfire, not a wall: the
	// fixture's player wall would otherwise be applied as it stands.
	placement := map[string]any{"defName": "Campfire", "x": buildCell["x"], "z": buildCell["z"], "rotation": "ROTATION_NORTH"}
	canPlace := func(label string) (bool, error) {
		reply, err := h.Wire(ctx, "placement-"+label, "placement_preview", map[string]any{"identity": identity, "placements": []any{placement}})
		if err != nil {
			return false, err
		}
		batch, _ := na.AsMap(reply["batch"])
		results := na.AsSlice(batch["results"])
		if len(results) != 1 {
			return false, fmt.Errorf("%s: expected one placement result, got %#v", label, reply)
		}
		result, _ := na.AsMap(results[0])
		evaluated, ok := na.AsMap(result["evaluated"])
		if !ok {
			return false, fmt.Errorf("%s: expected an evaluated placement, got %#v", label, result)
		}
		ok, _ = na.AsBool(evaluated["canPlace"])
		return ok, nil
	}
	if open, err := canPlace("build-open"); err != nil {
		return err
	} else if !open {
		return fmt.Errorf("build-open: expected the open build cell to be placeable")
	}
	if err := move("fill-build-cell", map[string]any{"action": "fill_cell", "x": buildCell["x"], "z": buildCell["z"]}); err != nil {
		return err
	}
	if blocked, err := canPlace("build-blocked"); err != nil {
		return err
	} else if blocked {
		return fmt.Errorf("build-blocked: expected the walled build cell to be unplaceable")
	}
	applied, err := h.Wire(ctx, "apply-build", "operations_apply", map[string]any{"identity": identity, "actions": []any{
		map[string]any{"key": "apply-refusal-build", "building": map[string]any{"placement": placement}},
	}})
	if err != nil {
		return err
	}
	results := na.AsSlice(applied["results"])
	var buildRefusal map[string]any
	if len(results) == 1 {
		result, _ := na.AsMap(results[0])
		buildRefusal, _ = na.AsMap(result["refused"])
	}
	if buildRefusal == nil || na.AsString(buildRefusal["code"]) != "FAILURE_CODE_INVALID_REQUEST" || na.AsString(buildRefusal["reason"]) == "" {
		return fmt.Errorf("build: expected one INVALID_REQUEST refusal with a reason, got %#v", applied)
	}
	report["build"] = na.AsString(buildRefusal["reason"])

	// Zone intents are Actions/Apply intents (#941). Zone cell edit and zone
	// creation: the free roofed cell is walled over after the read.
	if err := move("fill-free-cell", map[string]any{"action": "fill_cell", "x": freeCells[0]["x"], "z": freeCells[0]["z"]}); err != nil {
		return err
	}
	if err := intentRefused("zone-edit-add", map[string]any{"zoneCells": map[string]any{
		"zoneId": tokens["zoneId"], "edit": "CELL_EDIT_ADD", "cells": map[string]any{"explicitCells": map[string]any{"cells": []map[string]any{freeCells[0]}}},
	}}, "FAILURE_CODE_INVALID_REQUEST", "Zone cell edit refused: cell "+at(freeCells[0])+" is not free zoneable ground"); err != nil {
		return err
	}
	if err := intentRefused("zone-create", map[string]any{"createZone": map[string]any{
		"label": "RimGovernor apply refusal", "type": "ZONE_TYPE_STOCKPILE",
		"cells":     map[string]any{"explicitCells": map[string]any{"cells": []map[string]any{freeCells[0]}}},
		"stockpile": map[string]any{"priority": "STORAGE_PRIORITY_NORMAL", "preset": "FILTER_PRESET_NOTHING"},
	}}, "FAILURE_CODE_INVALID_REQUEST", "Zone creation refused: fresh free ground required: cell "+at(freeCells[0])+" is not roofed, walkable, unzoned, empty storage ground"); err != nil {
		return err
	}

	// Stockpile patch and a cell removal: the zone is gone. (A deletion of
	// the gone zone is its effect already holding, so it applies.)
	if err := move("delete-zone", map[string]any{"action": "delete_zone", "id": tokens["zoneId"]}); err != nil {
		return err
	}
	if err := intentRefused("stockpile-patch", map[string]any{"stockpile": map[string]any{
		"targetId": tokens["zoneId"], "settings": map[string]any{"priority": "STORAGE_PRIORITY_IMPORTANT"},
	}}, "FAILURE_CODE_NOT_FOUND", "Stockpile patch refused: the exact stockpile zone or storage building no longer exists on this map"); err != nil {
		return err
	}
	if err := intentRefused("zone-edit-remove", map[string]any{"zoneCells": map[string]any{
		"zoneId": tokens["zoneId"], "edit": "CELL_EDIT_REMOVE", "cells": map[string]any{"explicitCells": map[string]any{"cells": []map[string]any{zoneCells[0]}}},
	}}, "FAILURE_CODE_NOT_FOUND", "Zone cell edit refused: the exact zone no longer exists on this map"); err != nil {
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
	// Haul and work settings are intents on Actions/Apply (#856, #941): the
	// refusal is the action's result, not a failure reply.
	if err := intentRefused("haul", map[string]any{"haul": map[string]any{"pawnId": tokens["pawnId"], "thingId": tokens["haulItemId"]}},
		"FAILURE_CODE_INVALID_REQUEST", "Haul refused: the pawn is drafted"); err != nil {
		return err
	}
	if err := intentRefused("work-settings", map[string]any{"workSettings": map[string]any{
		"pawnId": tokens["pawnId"], "work": []map[string]any{{"workTypeDef": "Hauling", "priority": 1}},
	}}, "FAILURE_CODE_INVALID_REQUEST", "Work settings refused: the pawn is drafted"); err != nil {
		return err
	}
	if err := move("undraft-pawn", map[string]any{"action": "undraft_pawn", "id": tokens["pawnId"]}); err != nil {
		return err
	}

	// Bills are intents too (#941): the same bill on the fixture's bench
	// would find its matching bill standing and apply again, so the refusal
	// names a bench that is not on the map.
	if err := intentRefused("bill", map[string]any{"productionBill": map[string]any{
		"benchId":   "Building_missing_bench",
		"recipeDef": "ButcherCorpseFlesh",
		"settings": map[string]any{"repeatMode": "REPEAT_MODE_FOREVER", "suspended": false, "ingredientSearchRadius": 40,
			"store": map[string]any{"mode": "STORE_MODE_DROP_ON_FLOOR"}, "corpseClass": "CORPSE_CLASS_ANIMAL"},
	}}, "FAILURE_CODE_NOT_FOUND", "Production bill refused: bench Building_missing_bench is not a loaded bill giver"); err != nil {
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

	// Tame: the hare was designated for hunting after the read; the
	// Actions/Apply husbandry intent (#941) is refused by native tame
	// eligibility. (A tame designation would apply as it stands.)
	if err := move("designate-tame-prey", map[string]any{"action": "designate_hunt", "id": tokens["tameId"]}); err != nil {
		return err
	}
	if err := intentRefused("tame", map[string]any{"husbandry": map[string]any{"animalId": tokens["tameId"], "order": "HUSBANDRY_ORDER_TAME"}},
		"FAILURE_CODE_INVALID_REQUEST", "Husbandry refused: native tame eligibility refused the animal or it is designated for hunting"); err != nil {
		return err
	}

	// Grower crop: the plant pot is gone.
	if err := move("destroy-grower", map[string]any{"action": "destroy_thing", "id": tokens["growerId"]}); err != nil {
		return err
	}
	if err := intentRefused("grower-crop", map[string]any{"buildingPatch": map[string]any{"thingId": tokens["growerId"], "plantDef": tokens["growerCrop"]}},
		"FAILURE_CODE_NOT_FOUND", "Exact plant grower is unavailable."); err != nil {
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

	return nil
}
