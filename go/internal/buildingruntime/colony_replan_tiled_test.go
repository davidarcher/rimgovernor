package buildingruntime

import (
	"bytes"
	"context"
	"log/slog"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The tiled replan row is telemetry: skipped while the review made no advice or
// a fact is unread, never a change of the plan it is compared with.
func TestTiledReplanDecisionSkips(t *testing.T) {
	plan := policy.LayoutPlan{Rooms: []policy.PlannedRoom{{ID: 1, Role: policy.PlannedKitchen}}}
	d := tiledReplanDecision(policy.StationFacts{}, false, plan, policy.MapSurvey{}, nil)
	if d.Kind != "layout_replan_tiled" || d.Verdict != "skipped" || d.Reason != "station_advice_unavailable" {
		t.Fatalf("no advice: %+v", d)
	}
	facts := stationFactsOf(observation.ColonyProjection{}, plan, policy.MapSurvey{}, nil, policy.NewLedgerView(policy.LedgerViewNone, 0))
	d = tiledReplanDecision(facts, true, plan, policy.MapSurvey{}, nil)
	if d.Verdict != "skipped" || d.Reason != "facts_unknown" || d.Attrs["fact"] == "" {
		t.Fatalf("unread facts: %+v", d)
	}
	if len(plan.Rooms) != 1 || plan.Rooms[0].ID != 1 {
		t.Error("the plan changed")
	}
}

// One tile runs at a time: a replan that finds one running records the skip and
// starts nothing.
func TestTiledReplanSkipsWhileOneRuns(t *testing.T) {
	var out bytes.Buffer
	previous := slog.Default()
	slog.SetDefault(slog.New(slog.NewJSONHandler(&out, nil)))
	defer slog.SetDefault(previous)
	r := &Rounder{stationHeld: true}
	r.tiledRunning.Store(true)
	r.tiledReplan(context.Background(), policy.LayoutPlan{}, policy.MapSurvey{}, nil)
	if got := out.String(); !strings.Contains(got, "layout_replan_tiled") || !strings.Contains(got, "tile_in_flight") {
		t.Errorf("rows %q", got)
	}
	if !r.tiledRunning.Load() {
		t.Error("the running tile was released by the skip")
	}
}

func TestTiledReplanAttrsCountVerdictsAndListUnmatchedRooms(t *testing.T) {
	diff := policy.ReplanTileDiff{
		Rooms: []policy.TileRoomDiff{
			{Room: 1, Tiled: 1, Role: policy.PlannedKitchen, Verdict: policy.TileDiffSame, Reason: policy.ReasonDiffKept, Built: true},
			{Room: 2, Role: policy.PlannedGraveyard, Verdict: policy.TileDiffDropped, Reason: policy.ReasonDiffOutdoor, Built: true},
			{Tiled: 5, Role: policy.PlannedTomb, Verdict: policy.TileDiffNew, Reason: policy.ReasonDiffNoOldRoom},
		},
		Unmatched: []policy.UnmatchedBuilt{{Room: 2, Role: policy.PlannedGraveyard, Reason: policy.ReasonDiffOutdoor}},
	}
	attrs := tiledReplanAttrs(diff, 2, 3)
	if attrs["same"] != 1 || attrs["moved"] != 0 || attrs["new"] != 1 || attrs["dropped"] != 1 || attrs["built"] != 2 || attrs["tiled_rooms"] != 3 {
		t.Errorf("attrs = %+v", attrs)
	}
	if got := attrs["unmatched_built"].([]map[string]any); len(got) != 1 || got[0]["role"] != "graveyard" || got[0]["reason"] != policy.ReasonDiffOutdoor {
		t.Errorf("unmatched = %+v", got)
	}
	if got := attrs["rooms"].([]map[string]any); len(got) != 3 || got[2]["verdict"] != policy.TileDiffNew {
		t.Errorf("rooms = %+v", got)
	}
}
