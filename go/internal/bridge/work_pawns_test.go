package bridge

import (
	"context"
	"encoding/json"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
	"testing"
	"time"
)

func TestRoutinePawnsOwnWorkSelectionAndValidatePriorities(t *testing.T) {
	for _, change := range []string{"valid", "unknown-mode", "duplicate", "priority", "inapplicable", "extra-settings"} {
		t.Run(change, func(t *testing.T) {
			s := combatPawnsFixture()
			s.Pawns[0].Settings = &o.PawnSettings{WorkApplies: proto.Bool(true), ManualWorkPriorities: proto.Bool(false), Work: []*o.WorkSetting{{DefName: proto.String("Construction"), Priority: proto.Int32(3), Disabled: proto.Bool(false)}}}
			settings := s.Pawns[0].Settings
			switch change {
			case "unknown-mode":
				settings.ManualWorkPriorities = nil
			case "duplicate":
				settings.Work = append(settings.Work, settings.Work[0])
			case "priority":
				settings.Work[0].Priority = proto.Int32(5)
			case "inapplicable":
				settings.WorkApplies = proto.Bool(false)
			case "extra-settings":
				settings.SelfTend = proto.Bool(true)
			}
			client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
				var outer struct {
					Request string `json:"request"`
				}
				if err := json.Unmarshal(arg.Arguments, &outer); err != nil {
					t.Fatal(err)
				}
				q := &o.ListPawnsRequest{}
				if err := protojson.Unmarshal([]byte(outer.Request), q); err != nil {
					t.Fatal(err)
				}
				if !q.Details.GetWork() || !q.Details.GetNeeds() || q.Details.GetSettings() || !q.Details.GetBiography() || !q.Details.GetEquipment() {
					t.Fatal(q)
				}
				return pbResult(&o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: s}}), nil
			}}, time.Second)
			_, _, err := client.ReadRoutinePawns(context.Background(), pbIdentity(), []string{"pawn-1"})
			valid := change == "valid" || change == "unknown-mode"
			if (err == nil) != valid {
				t.Fatal(change, err)
			}
		})
	}
}
