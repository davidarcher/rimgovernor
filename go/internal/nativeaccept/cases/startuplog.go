package cases

import (
	"fmt"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

// checkStartupRows is the closing check every run ends on: the
// mod's startup rows (ModLog, kind mod_log, source "startup") in
// <output>/otlp/logs.jsonl and its rotated segments carry no
// error, and a windowed run has no "headless mode active" row. The row is
// not required of a headless run: a long run rotates the ring and the
// startup row can be trimmed away. A run that never started a
// service has no ring and no startup guard.
func checkStartupRows(output string, headless bool) error {
	rows, err := na.ReadLogs(na.ServiceLogsPath(output))
	if err != nil {
		return fmt.Errorf("read log ring: %w", err)
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
	return nil
}
