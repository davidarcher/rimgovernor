package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// penRefusingNative refuses the placement of the cells it names, as a native
// preview does for a ring cell the planned site cannot hold.
type penRefusingNative struct {
	*refrigerationNative
	refused map[domain.Cell]bool
	seen    []domain.Cell
}

func (n *penRefusingNative) PreviewBuilding(ctx context.Context, action domain.Action, s domain.GenerationSnapshot) (bridge.BuildingPreview, bridge.Result, error) {
	preview, raw, err := n.refrigerationNative.PreviewBuilding(ctx, action, s)
	if b, ok := action.Building(); ok {
		n.seen = append(n.seen, b.Cell())
		if n.refused[b.Cell()] {
			preview.Preview.CanPlace = domain.Known(false)
		}
	}
	return preview, raw, err
}

// A ring cell of the planned pen site the native refuses is reported as no
// space and the pen is never moved to another site (#2120); with every cell
// accepted the ring is admitted.
func TestPenRefusedRingCellReportsNoSpace(t *testing.T) {
	p, db, base, _ := refrigerationFixture(t, false)
	base.putCatalog(bridge.FixtureDef{Name: "Fence", ConstructionSkill: 0, Width: 1, Height: 1}, bridge.FixtureDef{Name: "FenceGate", ConstructionSkill: 0, Width: 1, Height: 1}, bridge.FixtureDef{Name: "PenMarker", Width: 1, Height: 1})
	room := policy.PlannedRoom{Role: policy.PlannedPen, Interior: policy.Rectangle{X: 1, Z: 1, Width: 2, Height: 2}, Door: domain.Cell{X: 2, Z: 0}, DoorRot: domain.North, Outdoor: true}
	refused := domain.Cell{X: 0, Z: 1}
	n := &penRefusingNative{refrigerationNative: base, refused: map[domain.Cell]bool{refused: true}}
	p.native = n
	ctx := context.Background()
	state := p.reviewer.player.session.State()
	review, err := db.LoadRounds(ctx)
	if err != nil {
		t.Fatal(err)
	}
	var concern domain.ConcernID
	for _, binding := range review.Standards {
		if binding.Concern == policy.MaintainRefrigeration {
			concern = binding.Standard
		}
	}
	goal, err := db.LoadStandard(ctx, concern)
	if err != nil {
		t.Fatal(err)
	}
	identity, _, err := n.Identity(ctx)
	if err != nil {
		t.Fatal(err)
	}
	expected, err := observation.DecodeIdentity(identity)
	if err != nil {
		t.Fatal(err)
	}
	reading, err := p.reviewer.observeRooms(ctx, n, expected, domain.Unknown[[]policy.ConstructionClaim](), "Fence", "FenceGate", "PenMarker")
	if err != nil {
		t.Fatal(err)
	}
	player := p.reviewer.player
	player.mu.Lock()
	epoch := player.epoch
	player.mu.Unlock()
	ops := []policy.Operation{
		{Kind: policy.OpWallIn, Cells: []domain.Cell{{X: 0, Z: 0}, refused, {X: 0, Z: 2}}},
		{Kind: policy.OpDoorIn, Cells: []domain.Cell{room.Door}},
	}
	// Control: with every cell accepted the ring is admitted whole.
	n.refused = nil
	ok, err := p.commitBuilds(ctx, epoch, state, review, goal, reading, policy.LayoutPlan{}, []roomWork{{rr: roomReconcile{room: room, name: "pen-1-1", reason: "pen"}, ops: ops}})
	if err != nil || ok.Verdict == noSpace("pen_enclosure") {
		t.Fatalf("an accepted ring reads %v %v", ok.Verdict, err)
	}
	n.refused = map[domain.Cell]bool{refused: true}
	result, err := p.commitBuilds(ctx, epoch, state, review, goal, reading, policy.LayoutPlan{}, []roomWork{{rr: roomReconcile{room: room, name: "pen-1-1", reason: "pen"}, ops: ops}})
	if err != nil {
		t.Fatal(err)
	}
	if result.Verdict != noSpace("pen_enclosure") {
		t.Fatalf("a refused ring cell reads %v", result.Verdict)
	}
}
