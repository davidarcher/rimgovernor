package bridge

import (
	"context"
	"errors"
	"testing"
	"time"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/proto"
)

// The wear read resolves a loose apparel candidate's tokens; a weapon the
// census lists beside it (its eligibility is the equip family's) is absent
// for the wear path, the sentinel the executor cancels on (#339).
func TestReadGearReplacementSkipsWeaponCandidates(t *testing.T) {
	v := gearColonyFixture(t)
	pawn := v.GetPlanning().GetObserved().Gear.Pawns[0]
	pawn.Pawn.Snapshot = &o.SnapshotRef{Context: proto.Clone(v.Context).(*c.ObservationContext), EntityId: proto.String("pawn"), Token: proto.String("pawn-token")}
	pawn.Candidates[0].Item.Thing.Snapshot = &o.SnapshotRef{Context: proto.Clone(v.Context).(*c.ObservationContext), EntityId: proto.String("parka"), Token: proto.String("allow-parka")}
	log := &o.GearCandidate{Gain: proto.Float64(.9), Item: &o.GearItem{Thing: &o.EntityRef{Id: proto.String("log"), DefName: proto.String("WoodLog"), Position: &c.Cell{X: proto.Int32(2), Z: proto.Int32(1)},
		Snapshot: &o.SnapshotRef{Context: proto.Clone(v.Context).(*c.ObservationContext), EntityId: proto.String("log"), Token: proto.String("allow-log")}}, Apparel: proto.Bool(false), Weapon: proto.Bool(true), Melee: proto.Bool(true)}}
	pawn.Candidates = append(pawn.Candidates, log)
	pawn.Completeness.Matched = proto.Uint64(2)
	pawn.Completeness.Returned = proto.Uint64(2)
	reply := &o.ColonyFactsReply{Outcome: &o.ColonyFactsReply_Observed{Observed: v}}
	client := testClient(t, &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) { return pbResult(reply), nil }}, time.Second)
	id := proto.Clone(v.Context.Identity).(*c.Identity)
	read, _, err := client.ReadGearReplacement(context.Background(), id, "pawn", "parka")
	if err != nil || read.ThingToken != "allow-parka" || read.Definition != "Parka" || read.LoadoutToken != "loadout" || read.PawnToken != "pawn-token" {
		t.Fatal(read, err)
	}
	if _, _, err = client.ReadGearReplacement(context.Background(), id, "pawn", "log"); !errors.Is(err, ErrGearCandidateAbsent) || !errors.Is(err, ErrUnavailable) {
		t.Fatal("weapon candidate offered to the wear path", err)
	}
	if _, _, err = client.ReadGearReplacement(context.Background(), id, "other", "parka"); errors.Is(err, ErrGearCandidateAbsent) || !errors.Is(err, ErrUnavailable) {
		t.Fatal("absent pawn reported as an absent candidate", err)
	}
}
