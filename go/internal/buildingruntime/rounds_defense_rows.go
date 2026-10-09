package buildingruntime

import (
	"context"
	"log/slog"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

// defenseAction emits one defense_action decision row (flight-rows contract):
// a defense, combat or custody action that was applied, refused, failed or is
// waiting. reason is a stable word; the data goes in attrs.
func defenseAction(ctx context.Context, component string, level slog.Level, verdict, reason, target string, attrs map[string]any) {
	telemetry.Decide(ctx, telemetry.Decision{Kind: "defense_action", Component: component, Level: level, Verdict: verdict, Reason: reason, Target: target, Attrs: attrs})
}

// defenseTierGated journals the gate that stopped a turret, mortar or IED
// tier short of the positions the layout found; no gate, no row.
func defenseTierGated(ctx context.Context, tier policy.DefenseTierName, gate string) {
	if gate == "" {
		return
	}
	defenseAction(ctx, "defense-layout", slog.LevelInfo, "refused", gate, string(tier), nil)
}

// defenseSnapshotSkip emits the snapshot_skip row for a snapshot that could not
// be recorded; target names the snapshot (combat, defense, layout).
func defenseSnapshotSkip(ctx context.Context, component, target string, err error) {
	telemetry.Decide(ctx, telemetry.Decision{Kind: "snapshot_skip", Component: component, Level: slog.LevelWarn, Verdict: "skipped", Reason: "not_recorded", Target: target, Attrs: map[string]any{"error": err}})
}
