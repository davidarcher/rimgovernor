package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/telemetry/telemetrytest"
)

// One planner_step row per Rounder step: ok when the review ran,
// failed with the error as an attr, reason control_lost for ErrControl.
func TestRoundsStepDecisionShape(t *testing.T) {
	ok := roundsStepDecision(nil, 2*time.Millisecond, true)
	p := ok.Payload()
	if ok.Kind != "planner_step" || p["target"] != "rounds" || p["verdict"] != "ok" || p["reason"] != "reviewed" || ok.Level != slog.LevelInfo || p["attrs"].(map[string]any)["partial"] != true {
		t.Fatalf("%+v", p)
	}
	lost := roundsStepDecision(fmt.Errorf("%w: step", ErrControl), 0, false)
	if p := lost.Payload(); p["verdict"] != "failed" || p["reason"] != "control_lost" || lost.Level != slog.LevelWarn || p["attrs"].(map[string]any)["error"] == nil {
		t.Fatalf("%+v", p)
	}
	failed := roundsStepDecision(errors.New("boom"), 0, false)
	if p := failed.Payload(); p["reason"] != "error" || p["attrs"].(map[string]any)["error"] != "boom" {
		t.Fatalf("%+v", p)
	}
}

func TestLayoutPlanDecisionVerdicts(t *testing.T) {
	d := layoutPlanDecision("replanned", "outgrown", 7, "plan", map[string]any{"unplaced": "x"})
	p := d.Payload()
	attrs := p["attrs"].(map[string]any)
	if d.Kind != "layout_plan" || p["verdict"] != "replanned" || p["reason"] != "outgrown" || attrs["colonists"] != 7 || attrs["unplaced"] != "x" {
		t.Fatalf("%+v", p)
	}
}

// A replan that leaves rooms unplaced writes one layout_plan refused/no_room
// row per distinct unplaced set; placing everything clears the memory.
func TestLogNoRoomOncePerUnplacedSet(t *testing.T) {
	rows := telemetrytest.Install(t)
	r := &Rounder{}
	ctx := context.Background()
	r.logNoRoom(ctx, 4, nil)
	r.logNoRoom(ctx, 4, errors.New("no site for the vet room"))
	r.logNoRoom(ctx, 4, errors.New("no site for the vet room"))
	if got := rows.Of("layout_plan"); len(got) != 1 {
		t.Fatalf("rows: %+v", got)
	}
	row := rows.Of("layout_plan")[0].Payload
	if row["verdict"] != "refused" || row["reason"] != "no_room" || row["attrs"].(map[string]any)["unplaced"] != "no site for the vet room" {
		t.Fatalf("%+v", row)
	}
	r.logNoRoom(ctx, 4, nil)
	r.logNoRoom(ctx, 4, errors.New("no site for the vet room"))
	if got := rows.Of("layout_plan"); len(got) != 2 {
		t.Fatalf("rows after clear: %+v", got)
	}
}
