package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// pricedNative prices every placement at one WoodLog and reports the stock the
// colony holds, as the native preview does.
type pricedNative struct {
	*refrigerationNative
	available int64
}

func (n *pricedNative) PreviewBuilding(ctx context.Context, action domain.Action, s domain.GenerationSnapshot) (bridge.BuildingPreview, bridge.Result, error) {
	preview, raw, err := n.refrigerationNative.PreviewBuilding(ctx, action, s)
	preview.Preview.Costs = domain.Known([]policy.Amount{{Resource: "WoodLog", Count: 1}})
	preview.Stock.Values = []policy.Stock{{Resource: "WoodLog", Available: domain.Known(n.available)}}
	return preview, raw, err
}

// batchRooms are three rooms of one wing, each owing a door and three walls.
func batchRooms() []roomWork {
	var works []roomWork
	for i := int32(0); i < 3; i++ {
		x := 1 + 4*i
		room := policy.PlannedRoom{Role: policy.PlannedPen, Interior: policy.Rectangle{X: x, Z: 1, Width: 2, Height: 2}, Door: domain.Cell{X: x + 1, Z: 0}, DoorRot: domain.North, Outdoor: true}
		works = append(works, roomWork{
			rr: roomReconcile{room: room, name: "wing-" + string(rune('a'+i)), reason: "wing"},
			ops: []policy.Operation{
				{Kind: policy.OpWallIn, Cells: []domain.Cell{{X: x - 1, Z: 1}, {X: x - 1, Z: 2}, {X: x + 2, Z: 1}}},
				{Kind: policy.OpDoorIn, Cells: []domain.Cell{room.Door}},
			},
		})
	}
	return works
}

// admittedBuilds commits works with available wood in stock and returns the
// buildings of the one method admitted.
func admittedBuilds(t *testing.T, available int64) []domain.Building {
	t.Helper()
	p, db, base, _ := refrigerationFixture(t, false)
	base.putCatalog(bridge.FixtureDef{Name: "Fence", ConstructionSkill: 0, Width: 1, Height: 1}, bridge.FixtureDef{Name: "FenceGate", ConstructionSkill: 0, Width: 1, Height: 1}, bridge.FixtureDef{Name: "PenMarker", Width: 1, Height: 1})
	p.native = &pricedNative{refrigerationNative: base, available: available}
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
	identity, _, err := base.Identity(ctx)
	if err != nil {
		t.Fatal(err)
	}
	expected, err := observation.DecodeIdentity(identity)
	if err != nil {
		t.Fatal(err)
	}
	reading, err := p.reviewer.observeRooms(ctx, base, expected, domain.Unknown[[]policy.ConstructionClaim](), "Fence", "FenceGate", "PenMarker")
	if err != nil {
		t.Fatal(err)
	}
	for i, d := range reading.Projection.Definitions {
		if d.Name == "Fence" || d.Name == "FenceGate" {
			reading.Projection.Definitions[i].Available = domain.Known(true)
		}
	}
	player := p.reviewer.player
	player.mu.Lock()
	epoch := player.epoch
	player.mu.Unlock()
	result, err := p.commitBuilds(ctx, epoch, state, review, goal, reading, policy.LayoutPlan{}, batchRooms())
	if err != nil || !result.Decision.Admitted {
		t.Fatalf("a wing's wave = %v %+v, %v", result.Verdict, result.Decision, err)
	}
	after, err := db.LoadStandard(ctx, concern)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := db.LoadPlan(ctx, after.Methods[len(after.Methods)-1].Plan)
	if err != nil {
		t.Fatal(err)
	}
	var out []domain.Building
	for _, action := range plan.Spec.Actions() {
		if b, ok := action.Building(); ok {
			out = append(out, b)
		}
	}
	return out
}

// A stocked wing's rooms are one batch: every room's ring is admitted
// together, the first room leading, rooms in plan order (#2133).
func TestCommitBuildsAdmitsAStockedWingTogether(t *testing.T) {
	got := admittedBuilds(t, 100)
	if len(got) != 12 {
		t.Fatalf("admitted %d buildings, want the 12 of three rooms", len(got))
	}
	// The first room leads, door first; then the later rooms' doors, then walls.
	for i, x := range map[int]int32{0: 2, 4: 6, 5: 10} {
		if got[i].Definition() != "FenceGate" || got[i].Cell().X != x {
			t.Fatalf("building %d = %s at %v, want the gate at x=%d", i, got[i].Definition(), got[i].Cell(), x)
		}
	}
}

// An under-stocked wing admits the first room whole and then only what the
// stock still funds, in plan order; the rest follows in the next wave.
func TestCommitBuildsAdmitsOnlyWhatTheStockFunds(t *testing.T) {
	got := admittedBuilds(t, 6)
	if len(got) != 6 {
		t.Fatalf("admitted %d buildings, want the 6 the stock funds", len(got))
	}
	head := 0
	for _, b := range got {
		if b.Cell().X < 4 {
			head++
		}
	}
	if head != 4 {
		t.Fatalf("the first room has %d of its 4 buildings, want it whole", head)
	}
}
