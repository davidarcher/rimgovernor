package bridge

import (
	"context"

	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// HomeCoverageAttempt carries the exact already-selected target/shape pair
// MaintainHomeCoverage's routine review chose. The native contract is the
// typed ExtendHome operation (contracts/proto/operations.proto), dispatched
// by NativeHomeCoverageOperations.cs with the same scope/shape rules the
// legacy JSON home/upkeep_home tool (HomeCoverageTool.cs) applies. Unlike
// BedAssign, the target's own identity carries no dedicated CAS token:
// native recomputes the shape hash from the target's current bounded
// footprint and the map-wide Home revision and refuses if either differs,
// so the EntityPrecondition here never sets an expected snapshot token.
type HomeCoverageAttempt struct {
	Identity   *c.Identity
	Attempt    *c.AttemptKey
	Generation uint64
	Target     string
	Shape      string
	Revision   int64
}

func homeCoverageEntity(target string) *o.EntityPrecondition {
	return &o.EntityPrecondition{EntityId: proto.String(target)}
}

func homeCoverageOperation(target, shape string, revision int64) *o.Operation {
	return &o.Operation{Command: &o.Operation_ExtendHome{ExtendHome: &o.ExtendHome{
		Target: homeCoverageEntity(target), ShapeToken: proto.String(shape), Revision: proto.Int64(revision),
	}}}
}

func homeCoverageCommand(target, shape string, revision int64) error {
	if validID(target) != nil || validID(shape) != nil || revision < 0 {
		return contract("invalid home coverage command")
	}
	return nil
}

// PreviewHomeCoverage checks an exact already-selected target/shape/revision
// triple; acceptance is not authority.
func (client *Client) PreviewHomeCoverage(ctx context.Context, identity *c.Identity, target, shape string, revision int64) (*o.PreviewReply, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	if err := homeCoverageCommand(target, shape, revision); err != nil {
		return nil, Result{}, err
	}
	identity = proto.Clone(identity).(*c.Identity)
	reply := &o.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &o.PreviewRequest{Identity: identity, Operation: homeCoverageOperation(target, shape, revision)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.PreviewReply_Failure:
		err = failure(v.Failure, raw)
	case *o.PreviewReply_Evaluated:
		value := v.Evaluated
		if value == nil {
			return reply, raw, contract("home coverage preview missing")
		}
		if err = buildingContext(value.Context, identity, 0, false); err != nil {
			break
		}
		effect := value.Projected.GetHome()
		if value.Accepted == nil || !diagnostic(value.Reason) || value.Preparation != nil || effect == nil {
			err = contract("home coverage preview facts missing")
			break
		}
		if effect.Snapshot.GetEntityId() != target || effect.GetShapeToken() != shape || effect.GetRevision() != revision {
			err = contract("home coverage preview projection mismatch")
		}
	default:
		err = contract("home coverage preview outcome missing")
	}
	return reply, raw, err
}

func homeCoverageAttempt(v HomeCoverageAttempt) (HomeCoverageAttempt, error) {
	if err := ValidateIdentity(v.Identity); err != nil {
		return HomeCoverageAttempt{}, err
	}
	if err := buildingAttempt(v.Attempt); err != nil {
		return HomeCoverageAttempt{}, err
	}
	if v.Generation == 0 {
		return HomeCoverageAttempt{}, contract("home coverage admission owner or generation mismatch")
	}
	if err := homeCoverageCommand(v.Target, v.Shape, v.Revision); err != nil {
		return HomeCoverageAttempt{}, err
	}
	v.Identity = proto.Clone(v.Identity).(*c.Identity)
	v.Attempt = proto.Clone(v.Attempt).(*c.AttemptKey)
	return v, nil
}

func homeCoverageEvidence(evidence *r.EffectEvidence, expected HomeCoverageAttempt) (*r.HomeEffect, error) {
	home := evidence.GetHome()
	if home == nil || home.Snapshot.GetEntityId() != expected.Target || home.ShapeToken == nil || home.GetShapeToken() != expected.Shape {
		return nil, contract("home coverage target or shape mismatch")
	}
	allowed := &r.HomeEffect{Snapshot: home.Snapshot, ShapeToken: home.ShapeToken, Revision: home.Revision, ChangedCells: home.ChangedCells, Covered: home.Covered}
	if !proto.Equal(home, allowed) {
		return nil, contract("home coverage effect fields missing or unsupported")
	}
	if home.Revision != nil && home.GetRevision() < expected.Revision {
		return nil, contract("home coverage revision predates admission")
	}
	return home, nil
}

func homeCoverageReceipt(v *r.Receipt, expected HomeCoverageAttempt) error {
	if v == nil || buildingUnknown(v) != nil || !proto.Equal(v.Attempt, expected.Attempt) {
		return contract("home coverage admission mismatch")
	}
	if err := buildingContext(v.AdmittedContext, expected.Identity, expected.Generation, true); err != nil {
		return err
	}
	switch outcome := v.Outcome.(type) {
	case *r.Receipt_Applied:
		if outcome.Applied == nil {
			return contract("home coverage applied missing")
		}
		_, err := homeCoverageEvidence(outcome.Applied.GetObserved(), expected)
		return err
	case *r.Receipt_Uncertain:
		if outcome.Uncertain == nil {
			return contract("home coverage uncertainty missing")
		}
		if outcome.Uncertain.LastObserved != nil {
			_, err := homeCoverageEvidence(outcome.Uncertain.LastObserved, expected)
			return err
		}
		return nil
	default:
		return contract("unsupported home coverage receipt")
	}
}

type HomeCoverageWriter struct{ client *Client }

func NewHomeCoverageWriter(client *Client) (*HomeCoverageWriter, error) {
	if client == nil {
		return nil, contract("home coverage client missing")
	}
	return &HomeCoverageWriter{client}, nil
}

// ApplyHomeCoverage dispatches one already-admitted Home extension.
func (writer *HomeCoverageWriter) ApplyHomeCoverage(ctx context.Context, pre *a.WritePrecondition, target, shape string, revision int64) (*o.ExecuteReply, Result, error) {
	if writer == nil || writer.client == nil || pre == nil || buildingUnknown(pre) != nil || ValidateIdentity(pre.Identity) != nil || buildingAttempt(pre.Attempt) != nil || pre.GetExpectedGeneration() == 0 {
		return nil, Result{}, contract("invalid home coverage execution")
	}
	if err := homeCoverageCommand(target, shape, revision); err != nil {
		return nil, Result{}, err
	}
	expected, err := homeCoverageAttempt(HomeCoverageAttempt{Identity: pre.Identity, Attempt: pre.Attempt, Generation: pre.GetExpectedGeneration(), Target: target, Shape: shape, Revision: revision})
	if err != nil {
		return nil, Result{}, err
	}
	pre = proto.Clone(pre).(*a.WritePrecondition)
	reply := &o.ExecuteReply{}
	raw, err := writer.client.protoCall(ctx, "rimgovernor/operations_execute", &o.ExecuteRequest{Precondition: pre, Operation: homeCoverageOperation(target, shape, revision)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.ExecuteReply_Receipt:
		err = homeCoverageReceipt(v.Receipt, expected)
	case *o.ExecuteReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("home coverage execute outcome missing")
	}
	return reply, raw, err
}

func (client *Client) LookupHomeCoverage(ctx context.Context, w HomeCoverageAttempt) (*r.LookupReply, Result, error) {
	expected, err := homeCoverageAttempt(w)
	if err != nil {
		return nil, Result{}, err
	}
	reply := &r.LookupReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_lookup", &r.LookupRequest{Identity: expected.Identity, Attempt: expected.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *r.LookupReply_Receipt:
		err = homeCoverageReceipt(v.Receipt, expected)
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, expected.Attempt) {
			err = contract("home coverage in-flight attempt mismatch")
		} else {
			err = buildingContext(v.InFlight.AdmittedContext, expected.Identity, expected.Generation, true)
		}
	case *r.LookupReply_Unknown:
		if v.Unknown == nil {
			err = contract("home coverage unknown context missing")
		} else {
			err = buildingContext(v.Unknown.Context, expected.Identity, 0, false)
		}
	case *r.LookupReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("home coverage lookup outcome missing")
	}
	return reply, raw, err
}

func (client *Client) ObserveHomeCoverageProgress(ctx context.Context, w HomeCoverageAttempt, admitted *r.Receipt) (*r.ProgressReply, Result, error) {
	expected, err := homeCoverageAttempt(w)
	if err != nil {
		return nil, Result{}, err
	}
	if admitted != nil {
		if err = homeCoverageReceipt(admitted, expected); err != nil {
			return nil, Result{}, err
		}
		admitted = proto.Clone(admitted).(*r.Receipt)
	}
	reply := &r.ProgressReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_observe_progress", &r.ProgressRequest{Identity: expected.Identity, Attempt: expected.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *r.ProgressReply_Progress:
		err = homeCoverageProgress(v.Progress, expected, admitted)
	case *r.ProgressReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("home coverage progress outcome missing")
	}
	return reply, raw, err
}

func homeCoverageProgress(v *r.Progress, expected HomeCoverageAttempt, admitted *r.Receipt) error {
	if v == nil || v.CompleteInspection == nil || !proto.Equal(v.Attempt, expected.Attempt) {
		return contract("home coverage progress attempt mismatch")
	}
	if err := buildingContext(v.Context, expected.Identity, 0, false); err != nil {
		return err
	}
	if admitted != nil && v.Context.GetTick() < admitted.AdmittedContext.GetTick() {
		return contract("home coverage progress predates admission")
	}
	switch outcome := v.Effect.(type) {
	case *r.Progress_Unknown:
		if outcome.Unknown == nil || !diagnostic(outcome.Unknown.Reason) {
			return contract("home coverage unknown progress missing")
		}
		return nil
	case *r.Progress_Pending:
		if outcome.Pending == nil || !v.GetCompleteInspection() {
			return contract("home coverage pending missing")
		}
		_, err := homeCoverageEvidence(outcome.Pending.Evidence, expected)
		return err
	case *r.Progress_Completed:
		if outcome.Completed == nil || !v.GetCompleteInspection() {
			return contract("home coverage completed missing")
		}
		_, err := homeCoverageEvidence(outcome.Completed.Evidence, expected)
		return err
	case *r.Progress_Absent:
		if outcome.Absent == nil || validID(outcome.Absent.GetInspectionToken()) != nil || !v.GetCompleteInspection() {
			return contract("home coverage absence lacks inspection")
		}
		return nil
	case *r.Progress_Unsuccessful:
		if outcome.Unsuccessful == nil || outcome.Unsuccessful.Reason == nil || outcome.Unsuccessful.GetReason() == 0 || !v.GetCompleteInspection() || !diagnostic(outcome.Unsuccessful.Detail) {
			return contract("home coverage unsuccessful reason missing")
		}
		_, err := homeCoverageEvidence(outcome.Unsuccessful.Evidence, expected)
		return err
	default:
		return contract("home coverage progress state missing")
	}
}
