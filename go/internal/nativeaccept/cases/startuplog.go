package cases

import (
	"fmt"
	"os"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

// checkStartupRows is the closing check every run ends on (#2121): the
// mod's startup rows in <output>/flight.jsonl (ModLog, kind mod_log,
// source "startup") carry no error, and "headless mode active" is present
// exactly when the run asked for headless. A run that never started a
// service has no flight.jsonl and no startup guard.
func checkStartupRows(output string, headless bool) error {
	path := na.FlightRecorderPath(output)
	if _, err := os.Stat(path); err != nil {
		return nil
	}
	rows, err := na.ReadFlight(path)
	if err != nil {
		return fmt.Errorf("read flight recorder: %w", err)
	}
	active := false
	for _, row := range rows {
		if row.Kind != "mod_log" || na.AsString(row.Payload["source"]) != "startup" {
			continue
		}
		msg := na.AsString(row.Payload["msg"])
		if na.AsString(row.Payload["level"]) == "error" {
			return fmt.Errorf("startup error logged by the mod: %s", msg)
		}
		if msg == "headless mode active" {
			active = true
		}
	}
	if active != headless {
		return fmt.Errorf("headless initialization disagrees with launch mode (headless=%t, \"headless mode active\" row=%t)", headless, active)
	}
	return nil
}
