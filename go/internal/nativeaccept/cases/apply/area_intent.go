package apply

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name: "apply/area-intent",
		Scope: "AreaIntent on Actions/Apply (#1321): a bot area is created with a cell, gains and loses cells, a resent " +
			"create applies again on the same area, it is deleted (twice), and the home area gains and loses one cell; every " +
			"receipt carries the area load id and cell count after the edit.",
		Start:  cases.LabStart(),
		Budget: 2 * time.Minute,
		Run:    areaIntent,
	})
}

func areaIntent(ctx context.Context, s cases.Session) error {
	h, identity, report := s.Harness(), s.Identity(), s.Report()
	if _, err := na.GrantAuto(ctx, h.WireFunc(), "acquire", identity); err != nil {
		return err
	}
	cell := func(x, z int) map[string]any { return map[string]any{"x": x, "z": z} }
	apply := func(key string, area map[string]any) (map[string]any, error) {
		reply, err := h.Wire(ctx, key, "operations_apply", map[string]any{"identity": identity, "actions": []any{map[string]any{"key": key, "area": area}}})
		if err != nil {
			return nil, err
		}
		results := na.AsSlice(reply["results"])
		if len(results) != 1 {
			return nil, fmt.Errorf("%s: expected one result, got %#v", key, reply)
		}
		result, _ := na.AsMap(results[0])
		applied, _ := na.AsMap(result["applied"])
		applied, _ = na.AsMap(applied["applied"])
		observed, _ := na.AsMap(applied["observed"])
		effect, ok := na.AsMap(observed["areaEdit"])
		if !ok {
			return nil, fmt.Errorf("%s: expected an applied area edit, got %#v", key, result)
		}
		return effect, nil
	}
	count := func(effect map[string]any) int { return int(na.AsNumber(effect["cellCount"])) }
	step := func(key, op string, cells []any, wantCount int, wantPresent bool) (map[string]any, error) {
		intent := map[string]any{"operation": op, "key": "accept"}
		if len(cells) > 0 {
			intent["cells"] = cells
		}
		effect, err := apply(key, intent)
		if err != nil {
			return nil, err
		}
		present, _ := effect["present"].(bool)
		if present != wantPresent || count(effect) != wantCount || (na.AsString(effect["areaId"]) != "") != wantPresent {
			return nil, fmt.Errorf("%s: expected present=%v with %d cells, got %#v", key, wantPresent, wantCount, effect)
		}
		report[key] = effect
		return effect, nil
	}
	created, err := step("area-create", "AREA_OPERATION_CREATE", []any{cell(10, 10)}, 1, true)
	if err != nil {
		return err
	}
	if _, err := step("area-set", "AREA_OPERATION_SET_CELLS", []any{cell(10, 11), cell(11, 10)}, 3, true); err != nil {
		return err
	}
	if _, err := step("area-clear", "AREA_OPERATION_CLEAR_CELLS", []any{cell(10, 10)}, 2, true); err != nil {
		return err
	}
	again, err := step("area-create-again", "AREA_OPERATION_CREATE", nil, 2, true)
	if err != nil {
		return err
	}
	if na.AsString(again["areaId"]) != na.AsString(created["areaId"]) {
		return fmt.Errorf("re-create made a second area %v beside %v", again["areaId"], created["areaId"])
	}
	for _, key := range []string{"area-delete", "area-delete-again"} {
		if _, err := step(key, "AREA_OPERATION_DELETE", nil, 0, false); err != nil {
			return err
		}
	}
	home := func(key, op string) (int, error) {
		effect, err := apply(key, map[string]any{"operation": op, "home": true, "cells": []any{cell(12, 12)}})
		if err != nil {
			return 0, err
		}
		if home, _ := effect["home"].(bool); !home || na.AsString(effect["areaId"]) == "" {
			return 0, fmt.Errorf("%s: expected the home area, got %#v", key, effect)
		}
		report[key] = effect
		return count(effect), nil
	}
	set, err := home("home-set", "AREA_OPERATION_SET_CELLS")
	if err != nil {
		return err
	}
	cleared, err := home("home-clear", "AREA_OPERATION_CLEAR_CELLS")
	if err != nil {
		return err
	}
	if cleared != set-1 {
		return fmt.Errorf("home clear left %d cells after set reached %d", cleared, set)
	}
	return nil
}
