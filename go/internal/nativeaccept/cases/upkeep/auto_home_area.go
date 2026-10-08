package upkeep

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name: "upkeep/auto-home-area",
		Scope: "AutoHomeAreaIntent on Actions/Apply (#1322): flipping Find.PlaySettings.autoHomeArea applies, the same value " +
			"again is UNCHANGED, and UpkeepFacts.auto_home_area reads each value back.",
		Start:  cases.LabStart(),
		Budget: 2 * time.Minute,
		Crew:   cases.Crew{Size: 3}, Run: runAutoHomeArea,
	})
}

func runAutoHomeArea(ctx context.Context, s cases.Session) error {
	h, identity, report := s.Harness(), s.Identity(), s.Report()
	if _, err := na.GrantAuto(ctx, h.WireFunc(), "acquire", identity); err != nil {
		return err
	}
	read := func(label string) (bool, error) {
		reply, err := h.Wire(ctx, label, "observations_read_colony_facts", map[string]any{
			"scope": map[string]any{"expectedIdentity": identity}, "planning": false,
		})
		if err != nil {
			return false, err
		}
		_, observed, err := na.Outcome(reply, "observed")
		if err != nil {
			return false, err
		}
		section, _ := na.AsMap(observed["upkeep"])
		_, upkeep, err := na.Outcome(section, "observed")
		if err != nil {
			return false, fmt.Errorf("%s: upkeep facts unavailable: %w", label, err)
		}
		value, ok := na.AsBool(upkeep["autoHomeArea"])
		if !ok {
			// A set optional is emitted even when false.
			return false, fmt.Errorf("%s: auto_home_area absent: %#v", label, upkeep["issues"])
		}
		return value, nil
	}
	apply := func(key string, enabled bool, outcome string) error {
		reply, err := h.Wire(ctx, key, "operations_apply", map[string]any{"identity": identity,
			"actions": []any{map[string]any{"key": key, "autoHomeArea": map[string]any{"enabled": enabled}}}})
		if err != nil {
			return err
		}
		results := na.AsSlice(reply["results"])
		if len(results) != 1 {
			return fmt.Errorf("%s: expected one result, got %#v", key, reply)
		}
		result, _ := na.AsMap(results[0])
		applied, _ := na.AsMap(result["applied"])
		applied, _ = na.AsMap(applied["applied"])
		observed, _ := na.AsMap(applied["observed"])
		settings, _ := na.AsMap(observed["settings"])
		fields := na.AsSlice(settings["fields"])
		if len(fields) != 1 {
			return fmt.Errorf("%s: expected one settings field, got %#v", key, result)
		}
		field, _ := na.AsMap(fields[0])
		if na.AsString(field["field"]) != "SETTINGS_FIELD_AUTO_HOME_AREA" || na.AsString(field["outcome"]) != outcome {
			return fmt.Errorf("%s: expected %s, got %#v", key, outcome, field)
		}
		got, err := read(key + "-readback")
		if err != nil {
			return err
		}
		if got != enabled {
			return fmt.Errorf("%s: fact reads %v, want %v", key, got, enabled)
		}
		return nil
	}
	initial, err := read("initial")
	if err != nil {
		return err
	}
	report["initial"] = initial
	if err := apply("auto-home-flip", !initial, "FIELD_OUTCOME_APPLIED"); err != nil {
		return err
	}
	if err := apply("auto-home-again", !initial, "FIELD_OUTCOME_UNCHANGED"); err != nil {
		return err
	}
	return apply("auto-home-restore", initial, "FIELD_OUTCOME_APPLIED")
}
