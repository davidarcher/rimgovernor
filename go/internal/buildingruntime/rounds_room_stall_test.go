package buildingruntime

import (
	"bytes"
	"context"
	"log/slog"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func testSite(t *testing.T, def string, x, z int32, stage string, resources bool, finishers int) policy.ConstructionSite {
	t.Helper()
	b, err := domain.NewBuilding(def, domain.Cell{X: x, Z: z}, domain.North, "")
	if err != nil {
		t.Fatal(err)
	}
	return policy.ConstructionSite{Building: b, Stage: stage, ResourcesComplete: domain.Known(resources), EligibleFinishers: domain.Known(finishers)}
}

// A standing room site is named in the wait by stage count and reason.
func TestAwaitingBuildNamesStagesAndReason(t *testing.T) {
	ground := policy.Rectangle{X: 10, Z: 10, Width: 5, Height: 5}
	sites := roomSites([]policy.ConstructionSite{
		testSite(t, "Wall", 10, 10, "blueprint", false, 2),
		testSite(t, "Wall", 11, 10, "blueprint", false, 2),
		testSite(t, "Door", 12, 10, "frame", true, 0),
		testSite(t, "Wall", 30, 30, "frame", true, 0),
	}, ground)
	if got, want := awaitingKey(sites), "awaiting_build:2_blueprints,1_frames:awaiting_materials"; got != want {
		t.Fatalf("awaitingKey = %q, want %q", got, want)
	}
	if awaitingKey(nil) != "" {
		t.Fatal("no site must name nothing")
	}
	w := roomWork{rr: roomReconcile{name: "bedroom-shell-105-99"}, awaiting: awaitingKey(sites)}
	if got, want := waitingHeld(context.Background(), "bedroom-shell-105-99", w).Verdict.Refusal.Subject, "bedroom-shell-105-99_reconcile:awaiting_build:2_blueprints,1_frames:awaiting_materials"; got != want {
		t.Fatalf("subject = %q, want %q", got, want)
	}
}

// A room stalls only after the window, is reported once per window, and
// forgets its clock when its sites are gone.
func TestNoteStallReportsAfterWindow(t *testing.T) {
	var logs bytes.Buffer
	old := slog.Default()
	slog.SetDefault(slog.New(slog.NewTextHandler(&logs, nil)))
	defer slog.SetDefault(old)
	r := &Rounder{}
	ctx := context.Background()
	sites := []policy.ConstructionSite{testSite(t, "Wall", 1, 1, "blueprint", true, 0)}
	r.noteStall(ctx, "bedroom", 1000, sites, nil)
	r.noteStall(ctx, "bedroom", 1000+roomBuildStallTicks-1, sites, nil)
	if strings.Contains(logs.String(), "build_stalled") {
		t.Fatalf("reported inside the window: %s", logs.String())
	}
	r.noteStall(ctx, "bedroom", 1000+roomBuildStallTicks, sites, nil)
	out := logs.String()
	if !strings.Contains(out, "build_stalled") || !strings.Contains(out, "no_eligible_builder") || !strings.Contains(out, "bedroom") {
		t.Fatalf("stall row = %q, want the kind, room and reason", out)
	}
	logs.Reset()
	r.noteStall(ctx, "bedroom", 1000+roomBuildStallTicks+10, sites, nil)
	if logs.Len() != 0 {
		t.Fatalf("repeated inside the window: %s", logs.String())
	}
	r.noteStall(ctx, "bedroom", 1000+roomBuildStallTicks+20, nil, nil)
	if _, tracked := r.roomStalls["bedroom"]; tracked {
		t.Fatal("a finished room kept its stall clock")
	}
}

// A door cell under a stack names the stack in the wait and, past the window,
// in the stall row, though no blueprint stands.
func TestObstructedDoorCellNamesTheStack(t *testing.T) {
	ops := []policy.Operation{{Kind: policy.OpHaulOut, Targets: []policy.ClearanceTarget{{DefName: "Steel", Count: 40, Minimum: domain.Cell{X: 10, Z: 12}, Maximum: domain.Cell{X: 10, Z: 12}}}}}
	obstructed := roomObstructions(ops)
	w := roomWork{rr: roomReconcile{name: "bedroom"}, obstructed: obstructed}
	if got, want := waitingHeld(context.Background(), "bedroom", w).Verdict.Refusal.Subject, "bedroom_reconcile:obstructed:haul_out:Steel(40)@10,12"; got != want {
		t.Fatalf("subject = %q, want %q", got, want)
	}
	var logs bytes.Buffer
	old := slog.Default()
	slog.SetDefault(slog.New(slog.NewTextHandler(&logs, nil)))
	defer slog.SetDefault(old)
	r := &Rounder{}
	r.noteStall(context.Background(), "bedroom", 5, nil, obstructed)
	r.noteStall(context.Background(), "bedroom", 5+roomBuildStallTicks, nil, obstructed)
	if out := logs.String(); !strings.Contains(out, "obstructed") || !strings.Contains(out, "Steel(40)@10,12") {
		t.Fatalf("stall row = %q", out)
	}
}
