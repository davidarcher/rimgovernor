package power

import (
	"context"
	"fmt"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func checkRain(ctx context.Context, s cases.Session, h *na.Harness, before map[string]any, observe func(string) (map[string]any, error)) error {
	check := func(v map[string]any) error {
		if na.AsNumber(v["ordinaryConduits"]) != 0 || na.AsNumber(v["eligibleConduits"]) != 0 {
			return fmt.Errorf("short-circuitable conduit remains: %#v", v)
		}
		if na.AsNumber(v["fires"]) != 0 || len(na.AsSlice(v["shortCircuits"])) != 0 {
			return fmt.Errorf("electrical fire or short circuit: %#v", v)
		}
		for id, row := range indexRows(v) {
			if row["defName"] == "Battery" || row["defName"] == "ElectricStove" {
				roofed, known := na.AsBool(row["roofed"])
				if !known || !roofed {
					return fmt.Errorf("equipment %s is not roofed", id)
				}
			}
		}
		return nil
	}
	if err := check(before); err != nil {
		return err
	}
	reply, err := h.Call(ctx, "force-rain", "test/power_rain", nil)
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(reply["success"]); !success {
		return fmt.Errorf("rain fixture refused: %#v", reply)
	}
	// Transition completes before the measured full day begins.
	if _, err = s.Advance(ctx, 1000); err != nil {
		return err
	}
	start, err := observe("rain-start")
	if err != nil {
		return err
	}
	if na.AsNumber(start["rainRate"]) <= 0 {
		return fmt.Errorf("fixture did not produce rain: %#v", start)
	}
	s.Report()["rain_start"] = start
	for i := 0; i < 12; i++ {
		if _, err = s.Advance(ctx, 5000); err != nil {
			return err
		}
		after, err := observe(fmt.Sprintf("rain-%02d", i))
		if err != nil {
			return err
		}
		s.Report()["rain_after"] = after
		if na.AsNumber(after["rainRate"]) <= 0 {
			return fmt.Errorf("rain ended before the day elapsed")
		}
		if err = check(after); err != nil {
			return err
		}
		original := indexRows(before)
		for id, row := range indexRows(after) {
			if missing, _ := na.AsBool(row["missing"]); missing || na.AsNumber(row["hitPoints"]) < na.AsNumber(original[id]["hitPoints"]) {
				return fmt.Errorf("equipment %s damaged during rain: %#v", id, row)
			}
		}
		if i == 11 && na.AsNumber(after["tick"])-na.AsNumber(start["tick"]) < 60000 {
			return fmt.Errorf("less than one full game day observed")
		}
	}
	return checkStartupLog(s)
}
