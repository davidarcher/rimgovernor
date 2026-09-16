package bridge

import (
	"context"
	"errors"

	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/placementpb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

// BuildingControl permits only ordinary blueprint placement. The runtime must
// hold its process lease and revalidate authority before invoking this capability.
type BuildingControl struct{ client *Client }

func NewBuildingControl(client *Client) (*BuildingControl, error) {
	if client == nil {
		return nil, contract("missing building client")
	}
	return &BuildingControl{client}, nil
}
func (b *BuildingControl) PlaceBuilding(ctx context.Context, pre *a.WritePrecondition, candidate *p.PlacementCandidate) (*o.ExecuteReply, Result, error) {
	if b == nil || b.client == nil {
		return nil, Result{}, contract("missing building capability")
	}
	if err := buildingInputs(pre, candidate); err != nil {
		return nil, Result{}, err
	}
	pre = proto.Clone(pre).(*a.WritePrecondition)
	candidate = proto.Clone(candidate).(*p.PlacementCandidate)
	request := &o.ExecuteRequest{Precondition: pre, Operation: &o.Operation{Command: &o.Operation_PlaceBuilding{PlaceBuilding: &o.PlaceBuilding{Placement: candidate}}}}
	reply := &o.ExecuteReply{}
	raw, err := b.client.protoCall(ctx, "rimgovernor/operations_execute", request, reply)
	if err != nil {
		return nil, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.ExecuteReply_Receipt:
		err = buildingReceipt(v.Receipt, pre, candidate)
	case *o.ExecuteReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("building execute outcome missing")
	}
	if err != nil && !errors.Is(err, ErrRefused) {
		return nil, raw, err
	}
	return reply, raw, err
}

// LookupBuildingAttempt never retries placement. Unknown means the ledger has
// no answer, not that the admitted operation had no effect.
func (client *Client) LookupBuildingAttempt(ctx context.Context, identity *c.Identity, attempt *c.AttemptKey, expectedGeneration uint64, candidate *p.PlacementCandidate) (*r.LookupReply, Result, error) {
	if expectedGeneration == 0 {
		return nil, Result{}, contract("lookup admission generation missing")
	}
	if err := ValidateIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	if err := buildingAttempt(attempt); err != nil {
		return nil, Result{}, err
	}
	if err := buildingCandidate(identity, candidate); err != nil {
		return nil, Result{}, err
	}
	// This correlation value is not a write authorization and carries no lease.
	pre := &a.WritePrecondition{Identity: proto.Clone(identity).(*c.Identity), Attempt: proto.Clone(attempt).(*c.AttemptKey), ExpectedGeneration: proto.Uint64(expectedGeneration)}
	candidate = proto.Clone(candidate).(*p.PlacementCandidate)
	reply := &r.LookupReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_lookup", &r.LookupRequest{Identity: pre.Identity, Attempt: pre.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *r.LookupReply_Receipt:
		err = buildingReceipt(v.Receipt, pre, candidate)
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, pre.Attempt) {
			err = contract("in-flight attempt mismatch")
		} else {
			err = buildingContext(v.InFlight.AdmittedContext, pre.Identity, pre.GetExpectedGeneration(), true)
		}
	case *r.LookupReply_Unknown:
		if v.Unknown == nil {
			err = contract("missing unknown attempt context")
		} else {
			err = buildingContext(v.Unknown.Context, pre.Identity, 0, false)
		}
	case *r.LookupReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("lookup outcome missing")
	}
	if err != nil && !errors.Is(err, ErrRefused) {
		return nil, raw, err
	}
	return reply, raw, err
}

// ObserveBuildingProgress correlates current evidence with immutable admission.
// A receipt is not completion. Current generation is freshness, not write authority.
func (client *Client) ObserveBuildingProgress(ctx context.Context, admitted *r.Receipt, candidate *p.PlacementCandidate) (*r.ProgressReply, Result, error) {
	if err := buildingReceipt(admitted, nil, candidate); err != nil {
		return nil, Result{}, err
	}
	admitted = proto.Clone(admitted).(*r.Receipt)
	candidate = proto.Clone(candidate).(*p.PlacementCandidate)
	reply := &r.ProgressReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_observe_progress", &r.ProgressRequest{Identity: admitted.AdmittedContext.Identity, Attempt: admitted.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *r.ProgressReply_Progress:
		err = buildingProgress(v.Progress, admitted, candidate)
	case *r.ProgressReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("progress outcome missing")
	}
	if err != nil && !errors.Is(err, ErrRefused) {
		return nil, raw, err
	}
	return reply, raw, err
}
func buildingInputs(pre *a.WritePrecondition, candidate *p.PlacementCandidate) error {
	if pre == nil || pre.ExpectedGeneration == nil || pre.GetExpectedGeneration() == 0 {
		return contract("building precondition presence")
	}
	if err := buildingUnknown(pre); err != nil {
		return err
	}
	if err := ValidateIdentity(pre.Identity); err != nil {
		return err
	}
	if err := buildingAttempt(pre.Attempt); err != nil {
		return err
	}
	return buildingCandidate(pre.Identity, candidate)
}
func buildingCandidate(identity *c.Identity, candidate *p.PlacementCandidate) error {
	if candidate == nil || candidate.GetRotation() == p.Rotation_ROTATION_ALL {
		return contract("building requires exact cardinal placement")
	}
	return validatePlacementRequest(&p.PlacementRequest{Identity: identity, Placements: []*p.PlacementCandidate{candidate}})
}
func buildingAttempt(attempt *c.AttemptKey) error {
	if attempt == nil || attempt.ControllerSessionId == nil || attempt.ActionId == nil || attempt.AttemptId == nil || attempt.GetAttemptId() == 0 || validID(attempt.GetControllerSessionId()) != nil || validID(attempt.GetActionId()) != nil {
		return contract("building attempt presence")
	}
	return buildingUnknown(attempt)
}
func buildingContext(value *c.ObservationContext, identity *c.Identity, generation uint64, exact bool) error {
	if err := ValidateContext(value); err != nil {
		return err
	}
	if !sameIdentity(value.Identity, identity) || (exact && (value.NativeGeneration == nil || value.GetNativeGeneration() != generation)) {
		return contract("building context mismatch")
	}
	return nil
}
func buildingReceipt(receipt *r.Receipt, pre *a.WritePrecondition, candidate *p.PlacementCandidate) error {
	if receipt == nil {
		return contract("building receipt missing")
	}
	if err := buildingUnknown(receipt); err != nil {
		return err
	}
	if err := buildingAttempt(receipt.Attempt); err != nil {
		return err
	}
	if err := ValidateContext(receipt.AdmittedContext); err != nil {
		return err
	}
	if receipt.AdmittedContext.NativeGeneration == nil {
		return contract("admitted generation missing")
	}
	if err := buildingCandidate(receipt.AdmittedContext.Identity, candidate); err != nil {
		return err
	}
	if pre != nil {
		if !proto.Equal(receipt.Attempt, pre.Attempt) {
			return contract("receipt attempt mismatch")
		}
		if err := buildingContext(receipt.AdmittedContext, pre.Identity, pre.GetExpectedGeneration(), true); err != nil {
			return err
		}
	}
	switch v := receipt.Outcome.(type) {
	case *r.Receipt_Applied:
		if v.Applied == nil {
			return contract("applied missing")
		}
		return buildingEvidence(v.Applied.Observed, candidate, true)
	case *r.Receipt_NoChange:
		if v.NoChange == nil || !diagnostic(v.NoChange.Detail) {
			return contract("no-change missing")
		}
		return buildingEvidence(v.NoChange.Observed, candidate, true)
	case *r.Receipt_Uncertain:
		if v.Uncertain == nil || !diagnostic(v.Uncertain.Detail) {
			return contract("uncertain missing")
		}
		if v.Uncertain.LastObserved != nil {
			return buildingEvidence(v.Uncertain.LastObserved, candidate, false)
		}
		return nil
	default:
		return contract("receipt outcome missing")
	}
}
func buildingEvidence(evidence *r.EffectEvidence, candidate *p.PlacementCandidate, complete bool) error {
	if evidence == nil || evidence.GetConstruction() == nil {
		return contract("building construction evidence required")
	}
	v := evidence.GetConstruction()
	if complete && (v.DefName == nil || v.Stuff == nil || v.Cell == nil || v.Rotation == nil || v.Stage == nil || v.Present == nil || v.Started == nil || v.Failed == nil) {
		return contract("construction facts missing")
	}
	if v.DefName != nil && (validID(v.GetDefName()) != nil || v.GetDefName() != candidate.GetDefName()) {
		return contract("construction definition mismatch")
	}
	if v.Stuff != nil && ((v.GetStuff() != "" && validID(v.GetStuff()) != nil) || (candidate.GetStuff() != "" && v.GetStuff() != candidate.GetStuff())) {
		return contract("construction stuff mismatch")
	}
	if v.Cell != nil {
		if err := validCell(v.Cell); err != nil {
			return err
		}
		if v.Cell.GetX() != candidate.GetX() || v.Cell.GetZ() != candidate.GetZ() {
			return contract("construction cell mismatch")
		}
	}
	if v.Rotation != nil && (v.GetRotation() < 1 || v.GetRotation() > 4 || v.GetRotation() != candidate.GetRotation()) {
		return contract("construction rotation mismatch")
	}
	if v.Stage != nil && (v.GetStage() < 1 || v.GetStage() > 4) {
		return contract("construction stage invalid")
	}
	for _, id := range []*string{v.OriginThingId, v.CurrentThingId} {
		if id != nil && validID(*id) != nil {
			return contract("construction lineage invalid")
		}
	}
	if v.GetPresent() && (v.OriginThingId == nil || v.CurrentThingId == nil || v.Stage == nil || v.GetStage() == r.ConstructionStage_CONSTRUCTION_STAGE_CANCELLED) {
		return contract("present construction lineage missing")
	}
	// A blueprint may carry an identity other than its origin: native follows
	// a failed construction (Frame.FailConstruction) into the fresh blueprint
	// the game spawns in the frame's place.
	if v.GetStage() == r.ConstructionStage_CONSTRUCTION_STAGE_BUILDING && v.Started != nil && !v.GetStarted() {
		return contract("building cannot be unstarted")
	}
	if !diagnostic(v.Blocker) {
		return contract("construction blocker invalid")
	}
	for _, ids := range [][]string{v.CancelledFrameIds, v.WipedThingIds} {
		if len(ids) > 4096 {
			return contract("construction effects limit")
		}
		seen := map[string]bool{}
		for _, id := range ids {
			if validID(id) != nil || seen[id] {
				return contract("construction effect identifiers invalid")
			}
			seen[id] = true
		}
	}
	return nil
}
func buildingObserved(receipt *r.Receipt) *r.ConstructionEffect {
	switch v := receipt.Outcome.(type) {
	case *r.Receipt_Applied:
		return v.Applied.GetObserved().GetConstruction()
	case *r.Receipt_NoChange:
		return v.NoChange.GetObserved().GetConstruction()
	case *r.Receipt_Uncertain:
		return v.Uncertain.GetLastObserved().GetConstruction()
	}
	return nil
}
func buildingProgress(value *r.Progress, admitted *r.Receipt, candidate *p.PlacementCandidate) error {
	if value == nil || !proto.Equal(value.Attempt, admitted.Attempt) {
		return contract("progress attempt mismatch")
	}
	if err := buildingContext(value.Context, admitted.AdmittedContext.Identity, 0, false); err != nil {
		return err
	}
	if value.Context.GetTick() < admitted.AdmittedContext.GetTick() {
		return contract("progress predates admission")
	}
	var evidence *r.EffectEvidence
	terminal := false
	switch v := value.Effect.(type) {
	case *r.Progress_Unknown:
		if v.Unknown == nil || !diagnostic(v.Unknown.Reason) {
			return contract("unknown progress invalid")
		}
		return nil
	case *r.Progress_Pending:
		if v.Pending != nil {
			evidence = v.Pending.Evidence
		}
	case *r.Progress_Completed:
		terminal = true
		if v.Completed != nil {
			evidence = v.Completed.Evidence
		}
		if evidence.GetConstruction().GetStage() != r.ConstructionStage_CONSTRUCTION_STAGE_BUILDING || !evidence.GetConstruction().GetPresent() || evidence.GetConstruction().GetFailed() {
			return contract("completion lacks completed building")
		}
	case *r.Progress_Absent:
		if value.CompleteInspection == nil || !value.GetCompleteInspection() || v.Absent == nil || v.Absent.InspectionToken == nil || validID(v.Absent.GetInspectionToken()) != nil {
			return contract("absence lacks complete inspection")
		}
		return nil
	case *r.Progress_Unsuccessful:
		terminal = true
		if v.Unsuccessful == nil || v.Unsuccessful.Reason == nil || v.Unsuccessful.GetReason() < 1 || v.Unsuccessful.GetReason() > 6 || !diagnostic(v.Unsuccessful.Detail) {
			return contract("unsuccessful progress invalid")
		}
		evidence = v.Unsuccessful.Evidence
	default:
		return contract("progress effect missing")
	}
	if terminal && (value.CompleteInspection == nil || !value.GetCompleteInspection()) {
		return contract("terminal progress lacks complete inspection")
	}
	if err := buildingEvidence(evidence, candidate, true); err != nil {
		return err
	}
	if err := buildingUnknown(value); err != nil {
		return err
	}
	original := buildingObserved(admitted)
	if original != nil && original.OriginThingId != nil && evidence.GetConstruction().GetOriginThingId() != original.GetOriginThingId() {
		return contract("construction origin changed")
	}
	return nil
}
func buildingUnknown(message proto.Message) error {
	if message == nil {
		return contract("missing building message")
	}
	var visit func(protoreflect.Message, int) error
	visit = func(m protoreflect.Message, depth int) error {
		if depth > 64 || len(m.GetUnknown()) != 0 {
			return contract("unknown building fields or excessive depth")
		}
		var err error
		m.Range(func(f protoreflect.FieldDescriptor, v protoreflect.Value) bool {
			if f.Message() == nil {
				return true
			}
			if f.IsList() {
				list := v.List()
				for i := 0; i < list.Len(); i++ {
					if err = visit(list.Get(i).Message(), depth+1); err != nil {
						return false
					}
				}
			} else if f.IsMap() {
				v.Map().Range(func(_ protoreflect.MapKey, v protoreflect.Value) bool {
					err = visit(v.Message(), depth+1)
					return err == nil
				})
			} else {
				err = visit(v.Message(), depth+1)
			}
			return err == nil
		})
		return err
	}
	return visit(message.ProtoReflect(), 0)
}
