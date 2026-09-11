package bridge

import (
	"context"
	"errors"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/proto"
	"math"
	"testing"
	"time"
)

func combatPawnsFixture() *o.PawnSnapshot {
	s := pawnsTestSnapshot()
	p := s.Pawns[0]
	p.Health = &o.PawnHealth{SummaryFraction: proto.Float64(.9), Bleeding: proto.Bool(false), NeedsTend: proto.Bool(false), Capacities: []*o.Capacity{{DefName: proto.String("Moving"), Level: proto.Float64(1)}}, Hediffs: []*o.Hediff{{Definition: &o.DefinitionRef{DefName: proto.String("Bruise")}, Severity: proto.Float64(.1)}}, HediffCompleteness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(1), Returned: proto.Uint64(1), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}}
	p.Equipment = &o.PawnEquipment{Armed: proto.Bool(true), PrimaryId: proto.String("club"), Equipped: []*o.GearItem{{Thing: &o.EntityRef{Id: proto.String("club")}, Weapon: proto.Bool(true), Melee: proto.Bool(true), Ranged: proto.Bool(false), HitPoints: proto.Int32(100), MaxHitPoints: proto.Int32(100), ConditionFraction: proto.Float64(1)}}}
	p.Biography = &o.PawnBiography{BiologicalAgeYears: proto.Float64(25), Skills: []*o.Skill{{Definition: &o.DefinitionRef{DefName: proto.String("Melee")}, Level: proto.Int32(10), StoredLevel: proto.Float64(10), Disabled: proto.Bool(false)}}, Traits: []*o.Trait{{DefName: proto.String("Beauty"), Degree: proto.Int32(-1)}}}
	return s
}
func TestCombatPawnsFixedDetailsAndUnknown(t *testing.T) {
	for _, details := range []bool{false, true} {
		snapshot := pawnsTestSnapshot()
		if details {
			snapshot = combatPawnsFixture()
		} else {
			snapshot.Pawns[0].Issues = append(snapshot.Pawns[0].Issues, &o.ReadIssue{Field: proto.String("health"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum()}})
		}
		identity := pbIdentity()
		ids := []string{"pawn-1", "missing"}
		client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
			if arg.Tool != "rimgovernor/observations_list_pawns" {
				t.Fatal(arg.Tool)
			}
			draftTestRequest(t, arg, &o.ListPawnsRequest{Scope: &o.ReadScope{ExpectedIdentity: pbIdentity()}, Filter: &o.PawnFilter{Ids: []string{"pawn-1", "missing"}, IncludeDead: proto.Bool(true)}, Details: &o.PawnDetails{Needs: proto.Bool(false), Health: proto.Bool(true), Equipment: proto.Bool(true), Biography: proto.Bool(true), Settings: proto.Bool(false), Social: proto.Bool(false), Animals: proto.Bool(false)}, Page: &c.PageRequest{Limit: proto.Uint32(2)}})
			identity.LoadToken = proto.String("changed")
			ids[0] = "changed"
			return pbResult(&o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: snapshot}}), nil
		}}, time.Second)
		reply, _, err := client.ReadCombatPawns(context.Background(), identity, ids)
		if err != nil || !proto.Equal(reply.GetObserved(), snapshot) {
			t.Fatal(reply, err)
		}
		if !details && reply.GetObserved().Pawns[0].Health != nil {
			t.Fatal("unknown health invented")
		}
	}
}
func TestCombatPawnsMalformedDetails(t *testing.T) {
	edits := map[string]func(*o.PawnSnapshot){
		"health nan":      func(s *o.PawnSnapshot) { s.Pawns[0].Health.SummaryFraction = proto.Float64(math.NaN()) },
		"health fraction": func(s *o.PawnSnapshot) { s.Pawns[0].Health.SummaryFraction = proto.Float64(2) },
		"equipment infinity": func(s *o.PawnSnapshot) {
			s.Pawns[0].Equipment.Equipped[0].ConditionFraction = proto.Float64(math.Inf(1))
		},
		"biography age": func(s *o.PawnSnapshot) { s.Pawns[0].Biography.BiologicalAgeYears = proto.Float64(-1) },
		"known unavailable": func(s *o.PawnSnapshot) {
			s.Pawns[0].Health.Issues = []*o.ReadIssue{{Field: proto.String("summary_fraction"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum()}}}
		},
		"duplicate capacity": func(s *o.PawnSnapshot) { h := s.Pawns[0].Health; h.Capacities = append(h.Capacities, h.Capacities[0]) },
		"duplicate gear":     func(s *o.PawnSnapshot) { e := s.Pawns[0].Equipment; e.Equipped = append(e.Equipped, e.Equipped[0]) },
		"hediff count":       func(s *o.PawnSnapshot) { s.Pawns[0].Health.HediffCompleteness.Returned = proto.Uint64(2) },
		"unrequested needs":  func(s *o.PawnSnapshot) { s.Pawns[0].Needs = &o.PawnNeeds{} },
		"unrequested row":    func(s *o.PawnSnapshot) { s.Pawns[0].Pawn.Id = proto.String("other") },
	}
	for name, edit := range edits {
		t.Run(name, func(t *testing.T) {
			s := combatPawnsFixture()
			edit(s)
			if err := pawnsSnapshotDetails(s, pbIdentity(), map[string]bool{"pawn-1": true}, true); !errors.Is(err, ErrContract) {
				t.Fatal(err)
			}
		})
	}
}
func TestCombatPawnsUnavailableAndInvalidRequests(t *testing.T) {
	calls := 0
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, _ nativeArgument) (*mcp.CallToolResult, error) {
		calls++
		return pbResult(&o.ListPawnsReply{Outcome: &o.ListPawnsReply_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum()}}}), nil
	}}, time.Second)
	for _, ids := range [][]string{nil, {""}, {"same", "same"}, make([]string, 257)} {
		if _, _, err := client.ReadCombatPawns(context.Background(), pbIdentity(), ids); err == nil {
			t.Fatal(ids)
		}
	}
	if calls != 0 {
		t.Fatal(calls)
	}
	if _, _, err := client.ReadCombatPawns(context.Background(), pbIdentity(), []string{"pawn"}); err == nil {
		t.Fatal("unavailable treated as facts")
	}
}
