package buildingruntime

import (
	"log/slog"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

// snapshotSkipDecision is the snapshot_skip row of a snapshot the recorder
// could not write: target the snapshot (colony, combat, defense, layout,
// firebreak), reason not_recorded, WARN, the write error as an attr.
func snapshotSkipDecision(component, target string, err error, tick domain.Tick) telemetry.Decision {
	return telemetry.Decision{Kind: "snapshot_skip", Component: component, Level: slog.LevelWarn, Verdict: "skipped", Reason: "not_recorded", Target: target,
		Attrs: map[string]any{"error": err, "tick": int64(tick)}}
}
