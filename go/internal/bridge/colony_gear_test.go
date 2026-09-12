package bridge

import (
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"math"
	"testing"
)

func gearColonyFixture(t *testing.T) *o.ColonyFactsSnapshot {
	v := colonyFixture(t).GetObserved()
	ctx := v.Context
	complete := func(count uint64) *o.Completeness {
		return &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(count), Returned: proto.Uint64(count), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}
	}
	p := &o.GearLoadout{Pawn: &o.EntityRef{Id: proto.String("pawn")}, Snapshot: &o.SnapshotRef{Context: proto.Clone(ctx).(*c.ObservationContext), EntityId: proto.String("pawn"), Token: proto.String("loadout")}, Deficit: proto.Bool(true), Completeness: complete(1), Candidates: []*o.GearCandidate{{Gain: proto.Float64(.3), Item: &o.GearItem{Thing: &o.EntityRef{Id: proto.String("parka"), DefName: proto.String("Parka"), Position: &c.Cell{X: proto.Int32(1), Z: proto.Int32(1)}}, Apparel: proto.Bool(true), Weapon: proto.Bool(false)}}}, ReplacementNeeds: []*o.GearReplacementNeed{{DefName: proto.String("Parka"), Stuff: proto.String("Cloth"), Reason: proto.String("wear")}}}
	v.GetPlanning().GetObserved().Gear = &o.GearSnapshot{Context: proto.Clone(ctx).(*c.ObservationContext), Pawns: []*o.GearLoadout{p}, Completeness: complete(1)}
	return v
}

func TestColonyGearRequiresExactCompleteLoadoutEvidence(t *testing.T) {
	v := gearColonyFixture(t)
	if err := ValidateColonyFacts(v, v.Context.Identity); err != nil {
		t.Fatal(err)
	}
	for name, change := range map[string]func(*o.GearSnapshot){
		"stale census":       func(g *o.GearSnapshot) { g.Context.Tick = proto.Int64(g.Context.GetTick() - 1) },
		"stale loadout":      func(g *o.GearSnapshot) { g.Pawns[0].Snapshot.Context.Tick = proto.Int64(g.Context.GetTick() - 1) },
		"changed generation": func(g *o.GearSnapshot) { g.Pawns[0].Snapshot.Context.NativeGeneration = proto.Uint64(99) },
		"other pawn token":   func(g *o.GearSnapshot) { g.Pawns[0].Snapshot.EntityId = proto.String("other") },
		"partial":            func(g *o.GearSnapshot) { g.Completeness.Filtered = proto.Uint64(1) },
		"partial candidates": func(g *o.GearSnapshot) { g.Pawns[0].Completeness.Returned = proto.Uint64(0) },
		"blocked eligible":   func(g *o.GearSnapshot) { g.Pawns[0].Blocker = proto.String("player job") },
		"nan gain":           func(g *o.GearSnapshot) { g.Pawns[0].Candidates[0].Gain = proto.Float64(math.NaN()) },
		"outside map":        func(g *o.GearSnapshot) { g.Pawns[0].Candidates[0].Item.Thing.Position.X = proto.Int32(4096) },
		"unknown kind":       func(g *o.GearSnapshot) { g.Pawns[0].Candidates[0].Item.Apparel = nil },
		"duplicate need": func(g *o.GearSnapshot) {
			g.Pawns[0].ReplacementNeeds = append(g.Pawns[0].ReplacementNeeds, g.Pawns[0].ReplacementNeeds[0])
		},
	} {
		t.Run(name, func(t *testing.T) {
			v := gearColonyFixture(t)
			change(v.GetPlanning().GetObserved().Gear)
			if err := ValidateColonyFacts(v, v.Context.Identity); err == nil {
				t.Fatal("invalid gear evidence accepted")
			}
		})
	}
	v = gearColonyFixture(t)
	planning := v.GetPlanning().GetObserved()
	planning.Issues = []*o.ReadIssue{{Field: proto.String("gear"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum()}}}
	if err := ValidateColonyFacts(v, v.Context.Identity); err == nil {
		t.Fatal("gear both known and unavailable")
	}
	planning.Gear = nil
	if err := ValidateColonyFacts(v, v.Context.Identity); err != nil {
		t.Fatal(err)
	}
}
