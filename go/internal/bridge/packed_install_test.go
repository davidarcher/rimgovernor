package bridge

import (
	"context"
	"encoding/json"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/placementpb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

// A packed install previews on the packed id and learns the inner
// building the move then carries (#830).
func TestResolvePackedInstall(t *testing.T) {
	projected := func(stage r.InstallationStage, x int32) *op.PreviewReply {
		return &op.PreviewReply{Outcome: &op.PreviewReply_Evaluated{Evaluated: &op.PreviewEvaluation{Context: pbContext(), Accepted: proto.Bool(true),
			Projected: &r.EffectEvidence{Effect: &r.EffectEvidence_Installation{Installation: &r.InstallationEffect{InnerThingId: proto.String("Thing_SculptureSmall3"),
				DefName: proto.String("SculptureSmall"), Cell: &c.Cell{X: proto.Int32(x), Z: proto.Int32(2)}, Rotation: p.Rotation_ROTATION_NORTH.Enum(), Stage: stage.Enum()}}}}}}
	}
	for _, test := range []struct {
		name  string
		reply *op.PreviewReply
		ok    bool
	}{
		{"placeable", projected(r.InstallationStage_INSTALLATION_STAGE_PLACEABLE, 4), true},
		{"wrong stage", projected(r.InstallationStage_INSTALLATION_STAGE_QUEUED, 4), false},
		{"other cell", projected(r.InstallationStage_INSTALLATION_STAGE_PLACEABLE, 5), false},
	} {
		t.Run(test.name, func(t *testing.T) {
			s := &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*callResult, error) {
				var outer struct {
					Request string `json:"request"`
				}
				if err := json.Unmarshal(arg.Arguments, &outer); err != nil {
					t.Fatal(err)
				}
				req := &op.PreviewRequest{}
				if err := protojson.Unmarshal([]byte(outer.Request), req); err != nil {
					t.Fatal(err)
				}
				if arg.Tool != "rimgovernor/operations_preview" || req.Operation.GetInstallBuilding().GetPackedOrInner().GetEntityId() != "Thing_MinifiedSculpture9" {
					t.Fatal("request correlation lost", req)
				}
				return pbResult(test.reply), nil
			}}
			inner, def, _, err := testClient(t, s, testBudget).ResolvePackedInstall(context.Background(), pbIdentity(), "Thing_MinifiedSculpture9", domain.Cell{X: 4, Z: 2}, domain.North)
			if test.ok != (err == nil) || test.ok && (inner != "Thing_SculptureSmall3" || def != "SculptureSmall") {
				t.Fatal(inner, def, err)
			}
			if !test.ok && !errors.Is(err, ErrContract) {
				t.Fatal("expected contract error", err)
			}
		})
	}
}
