// The zone/delete case exercises the native zone intents (N01.04, issues
// #34 and #941) in one run, each one Action on rimgovernor/operations_apply,
// the same Actions/Apply call Go's plain intent path sends: the one
// ZoneIntent (#1353) in each shape -- create (NativeStockpilePlacement.cs),
// settings (NativeStockpilePatch.cs), cells (NativeZoneCellEdit.cs, both
// directions) and delete (NativeZoneDeletion.cs) -- read back through the native
// rimgovernor/observations_list_zones tool (NativeZoneObservationTools.cs).
//
// A real stockpile zone is created over a disposable fixture's roofed,
// walled, empty 2x2 interior with the allow-list body the Go covered-storage
// planners send (Nothing preset plus an explicit thing_def allow list) and a
// hit-point/quality range; its filter is read back through ListZones (not
// just inferred from the applied evidence). The priority and ranges are then
// changed through the settings shape, with an unresolvable selector refused.
// One corner cell is removed (leaving a contiguous 3-cell L-shape, and
// returning that cell to genuinely free ground), that same cell is added
// back (restoring the original 2x2), and the exact zone is then deleted.
// Native validates each intent against live state at apply. A stockpile
// placement resends its immutable key to recover every original created ID.
package zone

import (
	"context"
	"encoding/json"
	"fmt"
	"strings"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

const sessionOwner = "native-zone-acceptance"

func init() {
	cases.Register(cases.Case{
		Name: "zone/delete",
		Scope: "Native zone intents on operations_apply: a real allow-list stockpile zone with hit-point/quality ranges " +
			"is created by a ZoneIntent create over a disposable fixture site and its filter read back through " +
			"rimgovernor/observations_list_zones, its priority and ranges are changed by a ZoneIntent settings patch (an " +
			"unresolvable selector refused), one corner cell is removed and then re-added by ZoneIntent cell edits, and the " +
			"exact zone is deleted by a ZoneIntent delete, with a stale census token and a missing zone refused, real " +
			"applied evidence and ListZones readbacks, and resends of effects that already hold applying again. The " +
			"re-add runs under a playing clock window and the journal's observation_invalidated names the zone id and " +
			"its cell rectangle (#359).",
		Start:  cases.Fixture{On: cases.LabStart(), Op: "test/zone_delete_prepare"},
		Budget: 5 * time.Minute,
		Crew:   cases.Crew{Size: 3}, Run: run,
	})
}

func run(ctx context.Context, s cases.Session) error {
	// Fixture: one small roofed, walled, empty, unzoned 2x2 interior. Run
	// this BEFORE acquiring authority, mirroring bedassignaccept/
	// recoveryareaaccept's own ordering (NativeControlAuthority.RevokeExternal
	// revokes any held lease for such external activity regardless of owner).
	report := s.Report()
	h := s.Harness()
	identity := s.Identity()
	prepared := s.Prepared()
	for _, required := range []string{"rimgovernor/operations_apply", "rimgovernor/observations_list_zones"} {
		if !na.Contains(s.Names(), required) {
			return fmt.Errorf("missing %s in discovery", required)
		}
	}
	cellRows := na.AsSlice(prepared["cells"])
	if len(cellRows) == 0 {
		return fmt.Errorf("prepare: missing fixture cells: %#v", prepared)
	}
	var cells []map[string]any
	for _, row := range cellRows {
		cell, _ := na.AsMap(row)
		cells = append(cells, map[string]any{"x": cell["x"], "z": cell["z"]})
	}
	report["fixture_cell_count"] = len(cells)

	if _, err := na.GrantAuto(ctx, h.WireFunc(), "acquire", identity); err != nil {
		return err
	}

	// zoneRow reads one zone's row (with its filter) through the
	// rimgovernor/observations_list_zones tool. A nil row with no error
	// means the zone genuinely is not listed.
	zoneRow := func(label, zoneID string) (map[string]any, error) {
		reply, err := h.Wire(ctx, label, "observations_list_zones", map[string]any{
			"scope": map[string]any{"expectedIdentity": identity}, "ids": []string{zoneID}, "includeFilter": true,
		})
		if err != nil {
			return nil, err
		}
		_, observed, err := na.Outcome(reply, "observed")
		if err != nil {
			return nil, err
		}
		rows := na.AsSlice(observed["zones"])
		if len(rows) == 0 {
			return nil, nil
		}
		if len(rows) != 1 {
			return nil, fmt.Errorf("%s: expected at most one observed zone, got %#v", label, observed)
		}
		row, _ := na.AsMap(rows[0])
		if na.AsString(row["id"]) != zoneID {
			return nil, fmt.Errorf("%s: unexpected zone row: %#v", label, row)
		}
		return row, nil
	}
	// zoneState reads one zone's row and its grid cells as a state token
	// (their canonical JSON), used here only to detect
	// whether a refused or resent intent touched the zone. present=false
	// with no error means the zone is not listed.
	zoneState := func(label, zoneID string) (token string, present bool, err error) {
		row, err := zoneRow(label, zoneID)
		if err != nil || row == nil {
			return "", false, err
		}
		grid, err := h.MapCells(ctx, label+"-cells", identity)
		if err != nil {
			return "", false, err
		}
		encoded, err := json.Marshal(map[string]any{"row": row, "cells": na.ZoneCells(grid)[zoneID]})
		if err != nil {
			return "", false, fmt.Errorf("%s: zone row: %w", label, err)
		}
		return string(encoded), true, nil
	}
	// zoneFilter asserts the exact filter readback: the allowed def set,
	// the hit-point fractions and the quality names.
	zoneFilter := func(label, zoneID, priority string, allowed []string, hpMin, hpMax float64, qualityMin, qualityMax string) error {
		row, err := zoneRow(label, zoneID)
		if err != nil {
			return err
		}
		if row == nil {
			return fmt.Errorf("%s: expected the zone to be listed", label)
		}
		if na.AsString(row["priority"]) != priority {
			return fmt.Errorf("%s: expected priority %s, got %#v", label, priority, row["priority"])
		}
		filter, ok := na.AsMap(row["filter"])
		if !ok {
			return fmt.Errorf("%s: expected a filter readback, got %#v", label, row)
		}
		var names []string
		for _, name := range na.AsSlice(filter["allowedDefNames"]) {
			names = append(names, na.AsString(name))
		}
		if !na.DeepEqual(names, allowed) {
			return fmt.Errorf("%s: expected allowed defs %v, got %v", label, allowed, names)
		}
		if na.AsNumber(filter["hitPointsMin"]) != hpMin || na.AsNumber(filter["hitPointsMax"]) != hpMax {
			return fmt.Errorf("%s: expected hit-point range [%v, %v], got %#v", label, hpMin, hpMax, filter)
		}
		if na.AsString(filter["qualityMin"]) != qualityMin || na.AsString(filter["qualityMax"]) != qualityMax {
			return fmt.Errorf("%s: expected quality range [%s, %s], got %#v", label, qualityMin, qualityMax, filter)
		}
		return nil
	}

	// apply sends one intent (arm is its Action oneof field) through
	// operations_apply and returns its single result.
	apply := func(label, key, arm string, intent map[string]any) (map[string]any, error) {
		reply, err := h.Wire(ctx, label, "operations_apply", map[string]any{"identity": identity, "actions": []any{map[string]any{
			"key": key, arm: intent,
		}}})
		if err != nil {
			return nil, err
		}
		results := na.AsSlice(reply["results"])
		if len(results) != 1 {
			return nil, fmt.Errorf("%s: expected one result, got %#v", label, reply)
		}
		result, _ := na.AsMap(results[0])
		return result, nil
	}
	// applied returns the ZoneEffect of an applied result.
	applied := func(label, key, arm string, intent map[string]any) (map[string]any, error) {
		result, err := apply(label, key, arm, intent)
		if err != nil {
			return nil, err
		}
		receipt, _ := na.AsMap(result["applied"])
		outcome, _ := na.AsMap(receipt["applied"])
		observed, _ := na.AsMap(outcome["observed"])
		effect, ok := na.AsMap(observed["zone"])
		if !ok {
			return nil, fmt.Errorf("%s: expected applied zone evidence, got %#v", label, result)
		}
		return effect, nil
	}
	// refused asserts a refusal with one of codes whose reason names the
	// rule that failed.
	refused := func(label, key, arm string, intent map[string]any, reason string, codes ...string) error {
		result, err := apply(label, key, arm, intent)
		if err != nil {
			return err
		}
		refusal, ok := na.AsMap(result["refused"])
		got := na.AsString(refusal["reason"])
		if !ok || !na.Contains(codes, na.AsString(refusal["code"])) || !strings.Contains(got, reason) {
			return fmt.Errorf("%s: expected %v %q, got %#v", label, codes, reason, result)
		}
		report[strings.ReplaceAll(label, "-", "_")] = got
		return nil
	}

	// --- create ---

	// The allow-list body bridge.stockpileSettings sends for
	// an AllowOnlyFilter stockpile, plus both filter ranges.
	stockpileBody := map[string]any{"priority": "STORAGE_PRIORITY_IMPORTANT", "preset": "FILTER_PRESET_NOTHING",
		"filter": map[string]any{"allow": []map[string]any{{"thingDef": "Steel"}, {"thingDef": "WoodLog"}},
			"hitPointsMin": 0.5, "hitPointsMax": 1, "qualityMin": "Normal", "qualityMax": "Legendary"}}
	create := func() map[string]any {
		return map[string]any{
			"label":     "Steel, WoodLog",
			"kind":      "ZONE_TYPE_STOCKPILE",
			"addCells":  map[string]any{"rectangle": map[string]any{"origin": cells[0], "width": 2, "height": 2}},
			"stockpile": stockpileBody,
		}
	}
	createEffect, err := applied("apply-create", "zone-create", "zone", create())
	if err != nil {
		return err
	}
	created := na.AsSlice(createEffect["created"])
	if len(created) != 1 {
		return fmt.Errorf("expected one created component: %#v", createEffect)
	}
	createdZone, _ := na.AsMap(created[0])
	zoneID := na.AsString(createdZone["zoneId"])
	if zoneID == "" {
		return fmt.Errorf("apply-create: missing zoneId in effect evidence: %#v", createEffect)
	}
	if len(na.AsSlice(createdZone["cells"])) != len(cells) {
		return fmt.Errorf("apply-create: expected a present %d-cell zone, got %#v", len(cells), createEffect)
	}
	report["created_zone_id"] = zoneID
	tokenAfterCreate, present, err := zoneState("zone-after-create", zoneID)
	if err != nil {
		return err
	}
	if !present {
		return fmt.Errorf("zone-after-create: expected the created zone to be listed")
	}
	if err := zoneFilter("filter-after-create", zoneID, "Important", []string{"Steel", "WoodLog"}, 0.5, 1, "Normal", "Legendary"); err != nil {
		return err
	}
	// A lost reply resends the immutable placement key and recovers the receipt.
	if resent, err := applied("resend-create", "zone-create", "zone", create()); err != nil {
		return err
	} else if !na.DeepEqual(resent, createEffect) {
		return fmt.Errorf("resend-create: expected the standing zone %s, got %#v", zoneID, resent)
	}
	if token, _, err := zoneState("zone-after-create-resend", zoneID); err != nil {
		return err
	} else if token != tokenAfterCreate {
		return fmt.Errorf("resend-create: a standing create changed the zone")
	}

	// --- settings ---
	//
	// Priority and the hit-point range change; the absent quality range and
	// selectors preserve the live filter. A selector that does not resolve
	// refuses the whole body before anything is applied.

	if err := refused("patch-unresolved-selector", "stockpile-unresolved", "zone", map[string]any{
		"zone":      map[string]any{"id": zoneID},
		"stockpile": map[string]any{"filter": map[string]any{"allow": []map[string]any{{"thingDef": "NoSuchThingDefForZoneAccept"}}}},
	}, "Stockpile patch refused", "FAILURE_CODE_INVALID_REQUEST"); err != nil {
		return err
	}
	if token, _, err := zoneState("zone-after-patch-unresolved", zoneID); err != nil {
		return err
	} else if token != tokenAfterCreate {
		return fmt.Errorf("patch-unresolved-selector: a refused body unexpectedly changed the zone")
	}
	patch := map[string]any{
		"zone":      map[string]any{"id": zoneID},
		"stockpile": map[string]any{"priority": "STORAGE_PRIORITY_NORMAL", "filter": map[string]any{"hitPointsMin": 0.25, "hitPointsMax": 0.75}},
	}
	if effect, err := applied("apply-patch", "stockpile-patch", "zone", patch); err != nil {
		return err
	} else if na.AsString(effect["zoneId"]) != zoneID {
		return fmt.Errorf("apply-patch: unexpected zone effect identity: %#v", effect)
	}
	if err := zoneFilter("filter-after-patch", zoneID, "Normal", []string{"Steel", "WoodLog"}, 0.25, 0.75, "Normal", "Legendary"); err != nil {
		return err
	}
	tokenAfterPatch, _, err := zoneState("zone-after-patch", zoneID)
	if err != nil {
		return err
	}
	if tokenAfterPatch == tokenAfterCreate {
		return fmt.Errorf("apply-patch: the listed zone did not change")
	}
	if _, err := applied("resend-patch", "stockpile-patch-resend", "zone", patch); err != nil {
		return err
	}
	if token, _, err := zoneState("zone-after-patch-resend", zoneID); err != nil {
		return err
	} else if token != tokenAfterPatch {
		return fmt.Errorf("resend-patch: settings that already hold were written again")
	}

	// --- presets ---
	//
	// indoor_only and outdoor_safe partition the nonperishables (#1768): each
	// preset is patched on and its allowed defs read back from the live game.
	presetDefs := func(preset string) (map[string]bool, error) {
		label := "preset-" + preset
		if _, err := applied(label, label, "zone", map[string]any{
			"zone":      map[string]any{"id": zoneID},
			"stockpile": map[string]any{"preset": "FILTER_PRESET_" + strings.ToUpper(preset)},
		}); err != nil {
			return nil, err
		}
		row, err := zoneRow(label+"-readback", zoneID)
		if err != nil {
			return nil, err
		}
		filter, _ := na.AsMap(row["filter"])
		defs := map[string]bool{}
		for _, name := range na.AsSlice(filter["allowedDefNames"]) {
			defs[na.AsString(name)] = true
		}
		return defs, nil
	}
	nonperishable, err := presetDefs("nonperishables")
	if err != nil {
		return err
	}
	outdoor, err := presetDefs("outdoor_safe")
	if err != nil {
		return err
	}
	indoor, err := presetDefs("indoor_only")
	if err != nil {
		return err
	}
	if len(outdoor) == 0 || len(indoor) == 0 {
		return fmt.Errorf("presets: outdoor_safe has %d defs and indoor_only %d; both must be non-empty", len(outdoor), len(indoor))
	}
	for name := range indoor {
		if outdoor[name] || !nonperishable[name] {
			return fmt.Errorf("presets: %s must be nonperishable and in only one of indoor_only and outdoor_safe", name)
		}
	}
	for name := range outdoor {
		if !nonperishable[name] {
			return fmt.Errorf("presets: outdoor_safe def %s is not nonperishable", name)
		}
	}
	if len(indoor)+len(outdoor) != len(nonperishable) {
		return fmt.Errorf("presets: indoor_only (%d) and outdoor_safe (%d) do not partition nonperishables (%d)", len(indoor), len(outdoor), len(nonperishable))
	}
	report["preset_partition"] = fmt.Sprintf("indoor_only %d + outdoor_safe %d = nonperishables %d", len(indoor), len(outdoor), len(nonperishable))

	// --- cells ---
	//
	// cells[0] is one corner of the fixture's 2x2 interior. Removing it
	// leaves a contiguous 3-cell L-shape and returns that cell to genuinely
	// free ground (an add never steals a cell from another zone -- it only
	// ever accepts free ground, matching the create shape's own eligibility test --
	// so re-adding the very cell this harness just freed is the one add case
	// the fixture can exercise without a second site).

	editCell := cells[0]
	edit := func(zone, op string) map[string]any {
		return map[string]any{"zone": map[string]any{"id": zone}, op: map[string]any{"explicitCells": map[string]any{"cells": []map[string]any{editCell}}}}
	}
	if err := refused("edit-missing-zone", "zone-edit-missing", "zone", edit("Zone_999999", "removeCells"),
		"Zone cell edit refused: the exact zone no longer exists on this map", "FAILURE_CODE_NOT_FOUND"); err != nil {
		return err
	}
	removeEffect, err := applied("apply-edit-remove", "zone-edit-remove", "zone", edit(zoneID, "removeCells"))
	if err != nil {
		return err
	}
	if present, _ := na.AsBool(removeEffect["present"]); !present || int(na.AsNumber(removeEffect["listedCellCount"])) != len(cells)-1 {
		return fmt.Errorf("apply-edit-remove: expected %d listed cells after removing one, got %#v", len(cells)-1, removeEffect)
	}
	tokenAfterRemove, present, err := zoneState("zone-after-edit-remove", zoneID)
	if err != nil {
		return err
	}
	if !present || tokenAfterRemove == tokenAfterPatch {
		return fmt.Errorf("zone-after-edit-remove: expected the zone listed and changed by a real cell removal")
	}
	if _, err := applied("resend-edit-remove", "zone-edit-remove-resend", "zone", edit(zoneID, "removeCells")); err != nil {
		return err
	}
	if token, _, err := zoneState("zone-after-edit-remove-resend", zoneID); err != nil {
		return err
	} else if token != tokenAfterRemove {
		return fmt.Errorf("resend-edit-remove: a cell already out of the zone was edited again")
	}
	report["zone_cells_after_edit_remove"] = len(cells) - 1

	// The add runs under a playing clock window so the native probe sees
	// the zone change and journals an observation_invalidated narrowed to
	// this zone and its rectangle (#359); the window is paused again before
	// the readbacks below.
	var addEffect map[string]any
	if _, err := editUnderEpoch(ctx, h, identity, report, zoneID, cells, func() error {
		addEffect, err = applied("apply-edit-add", "zone-edit-add", "zone", edit(zoneID, "addCells"))
		return err
	}); err != nil {
		return err
	}
	if int(na.AsNumber(addEffect["listedCellCount"])) != len(cells) {
		return fmt.Errorf("apply-edit-add: expected %d listed cells after re-adding the corner, got %#v", len(cells), addEffect)
	}
	if _, present, err := zoneState("zone-after-edit-add", zoneID); err != nil {
		return err
	} else if !present {
		return fmt.Errorf("zone-after-edit-add: expected the zone to be listed with all %d cells restored", len(cells))
	}
	report["zone_cells_after_edit_add"] = len(cells)

	// --- delete ---

	deleteEffect, err := applied("apply-delete", "zone-delete", "zone", map[string]any{"zone": map[string]any{"id": zoneID}, "delete": true})
	if err != nil {
		return err
	}
	if na.AsString(deleteEffect["zoneId"]) != zoneID {
		return fmt.Errorf("apply-delete: unexpected zone effect identity: %#v", deleteEffect)
	}
	if present, _ := na.AsBool(deleteEffect["present"]); present {
		return fmt.Errorf("apply-delete: expected present=false in the effect evidence, got %#v", deleteEffect)
	}
	if _, afterDeletePresent, err := zoneState("zone-after-delete", zoneID); err != nil {
		return err
	} else if afterDeletePresent {
		return fmt.Errorf("zone-after-delete: expected the deleted zone to no longer be listed")
	}
	report["zone_deleted"] = true
	// A zone already gone is the deletion's effect: the resend applies.
	if _, err := applied("resend-delete", "zone-delete-resend", "zone", map[string]any{"zone": map[string]any{"id": zoneID}, "delete": true}); err != nil {
		return err
	}

	return nil
}
