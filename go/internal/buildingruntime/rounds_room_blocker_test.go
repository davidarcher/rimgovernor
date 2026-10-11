package buildingruntime

import (
	"bytes"
	"context"
	"log/slog"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

var wallBlueprint = policy.PlacementBlocker{Category: "Building", DefName: "Wall", Blueprint: true}

// refusingNative refuses every placement of one definition and names a blocker.
type refusingNative struct {
	*pricedNative
	def     string
	blocker policy.PlacementBlocker
}

func (n *refusingNative) PreviewBuilding(ctx context.Context, action domain.Action, s domain.GenerationSnapshot) (bridge.BuildingPreview, bridge.Result, error) {
	preview, raw, err := n.pricedNative.PreviewBuilding(ctx, action, s)
	if b, ok := action.Building(); ok && b.Definition() == n.def {
		preview.Preview.CanPlace = domain.Known(false)
		preview.Preview.Blockers = []policy.PlacementBlocker{n.blocker}
	}
	return preview, raw, err
}

// commitFurniture commits one furniture piece through a native that refuses
// refuse and returns the result and the service log.
func commitFurniture(t *testing.T, refuse string) (RoundsBuildingResult, string) {
	t.Helper()
	piece := policy.WantedPiece{DefName: "PenMarker", Minimum: domain.Cell{X: 1, Z: 1}, Maximum: domain.Cell{X: 1, Z: 1}, Size: domain.Cell{X: 1, Z: 1}, Rot: domain.North}
	return commitOps(t, refuse, wallBlueprint, policy.Operation{Kind: policy.OpBuild, Pieces: []policy.WantedPiece{piece}})
}

// commitOps commits one operation through a native that refuses refuse and
// returns the result and the service log.
func commitOps(t *testing.T, refuse string, blocker policy.PlacementBlocker, op policy.Operation) (RoundsBuildingResult, string) {
	t.Helper()
	p, db, base, _ := refrigerationFixture(t, false)
	base.buildable("PenMarker", 0, 1, 1)
	base.buildable("Wall", 0, 1, 1)
	base.buildable("Door", 0, 1, 1)
	p.native = &refusingNative{pricedNative: &pricedNative{refrigerationNative: base, available: 100}, def: refuse, blocker: blocker}
	ctx := context.Background()
	state := p.reviewer.player.session.State()
	review, err := db.LoadRounds(ctx)
	if err != nil {
		t.Fatal(err)
	}
	var concern domain.StandardID
	for _, binding := range review.Standards {
		if binding.Concern == policy.MaintainRefrigeration {
			concern = binding.Standard
		}
	}
	goal, err := db.LoadStandard(ctx, concern)
	if err != nil {
		t.Fatal(err)
	}
	identity, _, err := base.Identity(ctx)
	if err != nil {
		t.Fatal(err)
	}
	expected, err := observation.DecodeIdentity(identity)
	if err != nil {
		t.Fatal(err)
	}
	reading, err := p.reviewer.observeRooms(ctx, base, expected, domain.Unknown[[]policy.ConstructionClaim](), "PenMarker", "Wall", "Door")
	if err != nil {
		t.Fatal(err)
	}
	for i, d := range reading.Projection.Definitions {
		if d.Name == "Wall" || d.Name == "Door" {
			reading.Projection.Definitions[i].Available = domain.Known(true)
		}
	}
	player := p.reviewer.player
	player.mu.Lock()
	epoch := player.epoch
	player.mu.Unlock()
	room := policy.PlannedRoom{Role: policy.PlannedPen, Interior: policy.Rectangle{X: 1, Z: 1, Width: 2, Height: 2}, Outdoor: op.Kind == policy.OpBuild}
	work := roomWork{rr: roomReconcile{room: room, name: "kitchen", reason: "kitchen", stuff: func(_, _ observation.PlanningDefinition) (string, string, Verdict, bool) {
		return "", "", Verdict{}, true
	}}, ops: []policy.Operation{op}}
	var logs bytes.Buffer
	old := slog.Default()
	slog.SetDefault(slog.New(slog.NewTextHandler(&logs, nil)))
	defer slog.SetDefault(old)
	result, err := p.commitBuilds(ctx, epoch, state, review, goal, reading, policy.LayoutPlan{}, work)
	if err != nil {
		t.Fatal(err)
	}
	return result, logs.String()
}

// A furniture cell the preview refuses keeps the existing-work wait but names
// the def, the cell and the blocking thing.
func TestRefusedFurnitureCellNamesItsBlocker(t *testing.T) {
	result, logs := commitFurniture(t, "PenMarker")
	if result.Verdict.Refusal.Kind != policy.CauseExistingWork || result.Verdict.Refusal.Subject != "kitchen_reconcile:blocked:PenMarker@1,1" {
		t.Fatalf("verdict = %+v, want the wait naming PenMarker@1,1", result.Verdict)
	}
	if !strings.Contains(logs, "PenMarker@1,1") || !strings.Contains(logs, "Wall blueprint") {
		t.Fatalf("service log = %q, want the refused cell and its blocker", logs)
	}
}

// With nothing refused the wait key is the plain one and nothing is logged.
func TestAllClearRoomWaitUnchanged(t *testing.T) {
	if got := roomWaiting("kitchen").Verdict.Refusal.Subject; got != "kitchen_reconcile" {
		t.Fatalf("subject = %q", got)
	}
	result, logs := commitFurniture(t, "NoSuchDef")
	if result.Verdict.Refusal.Kind == policy.CauseExistingWork || strings.Contains(logs, "refused") {
		t.Fatalf("an all-clear room waited: %+v, log %q", result.Verdict, logs)
	}
}

// A wall cell the preview refuses is named in the wait like a furniture cell.
func TestRefusedRingCellNamesItsBlocker(t *testing.T) {
	result, logs := commitOps(t, "Wall", policy.PlacementBlocker{Category: "Building", DefName: "Door", Blueprint: true}, policy.Operation{Kind: policy.OpWallUp, Cells: []domain.Cell{{X: 1, Z: 1}}})
	if result.Verdict.Refusal.Kind != policy.CauseExistingWork || result.Verdict.Refusal.Subject != "kitchen_reconcile:blocked:Wall@1,1" {
		t.Fatalf("verdict = %+v, want the wait naming Wall@1,1", result.Verdict)
	}
	if !strings.Contains(logs, "ring cell refused: Wall@1,1") || !strings.Contains(logs, "Door blueprint") {
		t.Fatalf("service log = %q, want the refused ring cell and its blocker", logs)
	}
}

// A ring cell standing under our own matching blueprint is placed: no refusal
// and no wait naming it.
func TestRingCellUnderOwnBlueprintIsPlaced(t *testing.T) {
	for _, name := range []string{"Wall", "Blueprint_Wall"} {
		blocker := policy.PlacementBlocker{Category: "Building", DefName: name, Blueprint: true}
		result, logs := commitOps(t, "Wall", blocker, policy.Operation{Kind: policy.OpWallUp, Cells: []domain.Cell{{X: 1, Z: 1}}})
		if strings.Contains(result.Verdict.Refusal.Subject, "blocked") || strings.Contains(logs, "refused") {
			t.Fatalf("%s: own blueprint refused: %+v, log %q", name, result.Verdict, logs)
		}
	}
}
