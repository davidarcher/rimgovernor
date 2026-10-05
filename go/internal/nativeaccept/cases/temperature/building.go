// The temperature/building case exercises the SetBuildingTemperature vertical
// (G01.08) through BuildingPatchIntent's target_temperature arm on
// Actions/Apply (#940): an existing player-owned building with a native
// CompTempControl, an out-of-range refusal, the real write confirmed by an
// independent building read, and key replay. Uses a private disposable
// fixture (test/building_temperature_prepare) since a fresh baseline colony
// does not reliably start with a temperature-controlled building.
package temperature

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name: "temperature/building",
		Scope: "BuildingPatchIntent target temperature against an exact CompTempControl building: " +
			"out-of-range refusal, the real write read back, and key replay idempotency.",
		Start:  cases.Fixture{On: cases.LabStart(), Op: "test/building_temperature_prepare"},
		Budget: 5 * time.Minute,
		Run:    run,
	})
}

func run(ctx context.Context, s cases.Session) error {
	h := s.Harness()
	identity := s.Identity()
	prepared := s.Prepared()
	if !na.Contains(s.Names(), "rimgovernor/observations_list_buildings") {
		return fmt.Errorf("missing rimgovernor/observations_list_buildings in discovery")
	}
	thingID := na.AsString(prepared["thingId"])
	if thingID == "" {
		return fmt.Errorf("building_temperature_prepare: unexpected fixture identifier: %#v", prepared)
	}

	// target reads the building row through rimgovernor/observations_list_buildings.
	target := func(label string) (row map[string]any, err error) {
		reply, err := h.Wire(ctx, label, "observations_list_buildings", map[string]any{
			"scope": map[string]any{"expectedIdentity": identity}, "ids": []string{thingID},
		})
		if err != nil {
			return nil, err
		}
		_, observed, err := na.Outcome(reply, "observed")
		if err != nil {
			return nil, err
		}
		buildings := na.AsSlice(observed["buildings"])
		if len(buildings) != 1 {
			return nil, fmt.Errorf("%s: expected exactly one building, got %#v", label, observed)
		}
		row, _ = na.AsMap(buildings[0])
		building, _ := na.AsMap(row["building"])
		if na.AsString(building["id"]) != thingID {
			return nil, fmt.Errorf("%s: fixture building identity mismatch: %#v", label, row)
		}
		return row, nil
	}
	settingsOf := func(row map[string]any) (temperature float64, token string, err error) {
		settings, ok := na.AsMap(row["settings"])
		if !ok {
			return 0, "", fmt.Errorf("missing settings: %#v", row)
		}
		snapshot, ok := na.AsMap(settings["snapshot"])
		if !ok || na.AsString(snapshot["entityId"]) != thingID {
			return 0, "", fmt.Errorf("missing or mismatched settings snapshot: %#v", settings)
		}
		if _, present := settings["targetTemperatureC"]; !present {
			return 0, "", fmt.Errorf("missing targetTemperatureC: %#v", settings)
		}
		return na.AsNumber(settings["targetTemperatureC"]), na.AsString(snapshot["token"]), nil
	}

	beforeRow, err := target("target-before")
	if err != nil {
		return err
	}
	beforeTemperature, beforeToken, err := settingsOf(beforeRow)
	if err != nil {
		return err
	}
	if beforeToken == "" {
		return fmt.Errorf("target-before: missing settings snapshot token: %#v", beforeRow)
	}
	newTemperature := beforeTemperature + 5
	if newTemperature > 1000 {
		newTemperature = beforeTemperature - 5
	}

	if _, err := na.GrantAuto(ctx, h.WireFunc(), "acquire", identity); err != nil {
		return err
	}
	apply := func(key string, temperature float64) (map[string]any, error) {
		reply, err := h.Wire(ctx, key, "operations_apply", map[string]any{"identity": identity,
			"actions": []any{map[string]any{"key": key, "buildingPatch": map[string]any{"thingId": thingID, "targetTemperature": temperature}}}})
		if err != nil {
			return nil, err
		}
		results := na.AsSlice(reply["results"])
		if len(results) != 1 {
			return nil, fmt.Errorf("%s: expected one result: %#v", key, reply)
		}
		result, _ := na.AsMap(results[0])
		return result, nil
	}

	// Refusal: a target outside the game's interface range.
	outOfRange, err := apply("temperature-out-of-range", 5000)
	if err != nil {
		return err
	}
	if outOfRange["refused"] == nil {
		return fmt.Errorf("temperature-out-of-range: expected a refusal, got %#v", outOfRange)
	}

	set, err := apply("temperature-set", newTemperature)
	if err != nil {
		return err
	}
	applied, ok := na.AsMap(set["applied"])
	if !ok {
		return fmt.Errorf("temperature-set: expected an applied result, got %#v", set)
	}
	appliedObserved, _ := na.AsMap(applied["observed"])
	appliedSettings, _ := na.AsMap(appliedObserved["settings"])
	fields := na.AsSlice(appliedSettings["fields"])
	if len(fields) != 1 {
		return fmt.Errorf("temperature-set: expected exactly one field result, got %#v", appliedSettings)
	}
	field, _ := na.AsMap(fields[0])
	if na.AsString(field["field"]) != "SETTINGS_FIELD_TEMPERATURE" || na.AsString(field["outcome"]) != "FIELD_OUTCOME_APPLIED" {
		return fmt.Errorf("temperature-set: unexpected field result: %#v", field)
	}
	afterRow, err := target("target-after")
	if err != nil {
		return err
	}
	afterTemperature, afterToken, err := settingsOf(afterRow)
	if err != nil {
		return err
	}
	if afterToken == beforeToken {
		return fmt.Errorf("target-after: expected the settings token to change")
	}
	if diff := afterTemperature - newTemperature; diff > 0.01 || diff < -0.01 {
		return fmt.Errorf("target-after: expected the native target temperature to change to %v, got %v", newTemperature, afterTemperature)
	}
	replay, err := apply("temperature-set", newTemperature)
	if err != nil {
		return err
	}
	if !na.DeepEqual(replay, set) {
		return fmt.Errorf("temperature-set: resent key changed its result: %#v then %#v", set, replay)
	}

	return nil
}
