package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

type shrineTestNative struct {
	traps    []domain.Cell
	threats  []policy.EmergencyThreat
	regions  []bridge.CellRect
	pawnRead int
}

func (n *shrineTestNative) ReadCombatPawns(ctx context.Context, _ *c.Identity, ids []string) (*o.ListPawnsReply, bridge.Result, error) {
	n.pawnRead++
	missing := func(field string) *o.ReadIssue {
		return &o.ReadIssue{Field: proto.String(field), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}
	}
	var rows []*o.PawnState
	for i, id := range ids {
		ranged := i%2 == 0
		row := &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(id)}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Drafted: proto.Bool(false),
			Job: &o.JobEvidence{PlayerForced: proto.Bool(false), QueuedJobs: proto.Uint32(0)}, Health: &o.PawnHealth{NeedsTend: proto.Bool(false), SummaryFraction: proto.Float64(1)}, Biography: &o.PawnBiography{},
			Equipment: &o.PawnEquipment{Armed: proto.Bool(true), PrimaryId: proto.String("w" + id), Equipped: []*o.GearItem{{Thing: &o.EntityRef{Id: proto.String("w" + id)}, Ranged: proto.Bool(ranged), Range: proto.Float64(25.9)}}},
			Issues:    []*o.ReadIssue{missing("mental_state")}}
		rows = append(rows, row)
	}
	count := uint64(len(rows))
	return &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{Pawns: rows, Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(count), Returned: proto.Uint64(count), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}}}}, bridge.Result{}, ctx.Err()
}
func (n *shrineTestNative) ReadDefenseSite(ctx context.Context, _ *c.Identity, region bridge.CellRect) (bridge.DefenseSite, bridge.Result, error) {
	n.regions = append(n.regions, region)
	site := bridge.DefenseSite{Region: region}
	for _, trap := range n.traps {
		site.Cells = append(site.Cells, bridge.DefenseCell{Cell: trap, EdificeDefName: "TrapSpike", PlayerOwned: true})
	}
	site.Cells = append(site.Cells, bridge.DefenseCell{Cell: domain.Cell{X: 1, Z: 1}, EdificeDefName: "TrapSpike"}, bridge.DefenseCell{Cell: domain.Cell{X: 2, Z: 2}, Fogged: true})
	return site, bridge.Result{}, ctx.Err()
}
func (n *shrineTestNative) ReadEmergency(ctx context.Context, _ *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	return bridge.EmergencyObservation{Facts: policy.EmergencyFacts{Threats: n.threats}}, bridge.Result{}, ctx.Err()
}

func TestShrineReadinessReadsOnlyForBreachableShrines(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	identity := &c.Identity{ColonyId: proto.String("colony"), LoadToken: proto.String("load"), MapId: proto.Int32(0)}
	sealed := policy.AncientShrine{ID: "sealed", Sealed: true, BreachWalls: []policy.ShrineBreachWall{{EntityID: "wall", Cell: domain.Cell{X: 30, Z: 30}, Outside: domain.Cell{X: 29, Z: 30}}}}
	unexplored := policy.AncientShrine{ID: "unexplored", Sealed: true}
	opened := policy.AncientShrine{ID: "opened", GuardsKnown: true}
	native := &shrineTestNative{traps: []domain.Cell{{X: 27, Z: 30}, {X: 27, Z: 31}, {X: 26, Z: 30}}}
	got, err := shrineReadiness(ctx, native, identity, []policy.AncientShrine{unexplored, opened}, []string{"a", "b"}, domain.Known(100.0), domain.Cell{X: 5, Z: 5}, policy.Bounds{Width: 100, Height: 100})
	if err != nil || native.pawnRead != 0 || len(native.regions) != 0 || len(got) != 2 || got[0].Readiness.Reason != policy.ShrineHoldNoBreachWall || got[1].Readiness.Reason != policy.ShrineHoldNotSealed {
		t.Fatalf("%+v %v %+v", got, err, native)
	}
	got, err = shrineReadiness(ctx, native, identity, []policy.AncientShrine{sealed, opened}, []string{"a", "b"}, domain.Known(100.0), domain.Cell{X: 5, Z: 5}, policy.Bounds{Width: 100, Height: 100})
	if err != nil || native.pawnRead != 1 || len(native.regions) != 1 || len(got) != 2 {
		t.Fatalf("%+v %v %+v", got, err, native)
	}
	if region := native.regions[0]; region.Min != (domain.Cell{X: 16, Z: 17}) || region.Max != (domain.Cell{X: 42, Z: 43}) {
		t.Fatalf("trap window %+v", region)
	}
	if r := got[0].Readiness; !r.Ready || r.Reason != "" || r.Wall.EntityID != "wall" || r.Traps != 3 || len(r.Squad) != 2 || r.Squad[0] != "a" {
		t.Fatalf("%+v", r)
	}
	native.threats = []policy.EmergencyThreat{{ID: "raider", Dead: domain.Known(false), Downed: domain.Known(false)}}
	got, err = shrineReadiness(ctx, native, identity, []policy.AncientShrine{sealed}, []string{"a", "b"}, domain.Known(100.0), domain.Cell{X: 5, Z: 5}, policy.Bounds{Width: 100, Height: 100})
	if err != nil || got[0].Readiness.Ready || got[0].Readiness.Reason != policy.ShrineHoldEmergency {
		t.Fatalf("%+v %v", got, err)
	}
	native.threats = []policy.EmergencyThreat{{ID: "raider", Dead: domain.Known(true), Downed: domain.Known(false)}}
	got, err = shrineReadiness(ctx, native, identity, []policy.AncientShrine{sealed}, []string{"a", "b"}, domain.Known(100.0), domain.Cell{X: 30, Z: 95}, policy.Bounds{Width: 100, Height: 100})
	if err != nil || !got[0].Readiness.Ready || native.regions[len(native.regions)-1].Max.Z != 43 {
		t.Fatalf("%+v %v %+v", got, err, native.regions)
	}
	got, err = shrineReadiness(ctx, native, identity, []policy.AncientShrine{{ID: "edge", Sealed: true, BreachWalls: []policy.ShrineBreachWall{{EntityID: "w", Cell: domain.Cell{X: 1, Z: 98}, Outside: domain.Cell{X: 0, Z: 98}}}}}, []string{"a", "b"}, domain.Known(100.0), domain.Cell{}, policy.Bounds{Width: 100, Height: 100})
	if err != nil || native.regions[len(native.regions)-1] != (bridge.CellRect{Min: domain.Cell{X: 0, Z: 85}, Max: domain.Cell{X: 13, Z: 99}}) {
		t.Fatalf("%+v %v %+v", got, err, native.regions)
	}
}
