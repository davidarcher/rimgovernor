package bridge

import (
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

func buildingPre() *a.WritePrecondition {
	return &a.WritePrecondition{Identity: pbIdentity(), ExpectedGeneration: proto.Uint64(1), Attempt: &c.AttemptKey{ControllerSessionId: proto.String("controller"), ActionId: proto.String("action"), AttemptId: proto.Uint64(1)}}
}
func buildingEffect() *r.EffectEvidence {
	return &r.EffectEvidence{Effect: &r.EffectEvidence_Construction{Construction: &r.ConstructionEffect{OriginThingId: proto.String("blueprint1"), CurrentThingId: proto.String("blueprint1"), DefName: proto.String("Wall"), Stuff: proto.String(""), Cell: &c.Cell{X: proto.Int32(0), Z: proto.Int32(0)}, Rotation: pbRequest().Placements[0].Rotation, Stage: r.ConstructionStage_CONSTRUCTION_STAGE_BLUEPRINT.Enum(), Present: proto.Bool(true), Started: proto.Bool(false), Failed: proto.Bool(false)}}}
}
func buildingAdmission() *r.Receipt {
	return &r.Receipt{Attempt: buildingPre().Attempt, AdmittedContext: &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(10), NativeGeneration: proto.Uint64(1)}, Outcome: &r.Receipt_Applied{Applied: &r.Applied{Observed: buildingEffect()}}}
}
func buildingDone() *r.Progress {
	e := buildingEffect()
	e.GetConstruction().Stage = r.ConstructionStage_CONSTRUCTION_STAGE_BUILDING.Enum()
	e.GetConstruction().CurrentThingId = proto.String("building1")
	e.GetConstruction().Started = proto.Bool(true)
	return &r.Progress{Attempt: buildingPre().Attempt, Context: &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(11), NativeGeneration: proto.Uint64(2)}, CompleteInspection: proto.Bool(true), Effect: &r.Progress_Completed{Completed: &r.CompletedEffect{Evidence: e}}}
}
