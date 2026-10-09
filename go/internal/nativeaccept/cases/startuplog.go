package cases

import (
	"fmt"
	"os"
	"path/filepath"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

// checkStartupRows is the closing check every run ends on: the
// mod's startup rows (ModLog, kind mod_log, source "startup") in
// <output>/flight.jsonl and its rotated segments flight.jsonl.N carry no
// error, and a windowed run has no "headless mode active" row. The row is
// not required of a headless run: a long run rotates the recorder and the
// startup row can be trimmed away. A run that never started a
// service has no flight.jsonl and no startup guard.
func checkStartupRows(output string, headless bool) error {
	base := na.FlightRecorderPath(output)
	paths, _ := filepath.Glob(base + ".*")
	if _, err := os.Stat(base); err == nil {
		paths = append(paths, base)
	}
	for _, path := range paths {
		rows, err := na.ReadFlight(path)
		if err != nil {
			return fmt.Errorf("read flight recorder %s: %w", filepath.Base(path), err)
		}
		for _, row := range rows {
			if row.Kind != "mod_log" || na.AsString(row.Payload["source"]) != "startup" {
				continue
			}
			msg := na.AsString(row.Payload["msg"])
			if na.AsString(row.Payload["level"]) == "error" {
				return fmt.Errorf("startup error logged by the mod: %s", msg)
			}
			if msg == "headless mode active" && !headless {
				return fmt.Errorf("headless initialization disagrees with launch mode (headless=false, \"headless mode active\" row present)")
			}
		}
	}
	return nil
}
