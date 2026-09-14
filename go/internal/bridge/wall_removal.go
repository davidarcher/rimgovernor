package bridge

import (
	"context"

	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// WallRemovalAttempt carries the exact already-admitted demolition target:
// the native occupant policy.EvaluateWallRemoval just proved still matches
// (either the original wall's proven identity or a same-plan backup wall's
// own proven construction identity). The native contract is the typed
// RemoveWall operation (contracts/proto/operations.proto), the legacy
// home/upkeep_wall tool's typed successor; like HomeCoverage, no native
// adapter wires Operation_RemoveWall into the typed Execute/Preview dispatch
// yet, an open native acceptance item matching the other upkeep verticals'
// own gaps. Unlike BedAssign, the target carries no dedicated CAS token here:
// native re-validates the exact occupant and site geometry itself and
// refuses if either changed, so the EntityPrecondition never sets an
// expected snapshot token.
type WallRemovalAttempt struct {
	Identity   *c.Identity
	Attempt    *c.AttemptKey
	Owner      *a.Owner
	Generation uint64
	Target     string
}

func wallRemovalEntity(target string) *o.EntityPrecondition {
	return &o.EntityPrecondition{EntityId: proto.String(target)}
}

func wallRemovalOperation(target string) *o.Operation {
	return &o.Operation{Command: &o.Operation_RemoveWall{RemoveWall: &o.RemoveWall{Wall: wallRemovalEntity(target)}}}
}

func wallRemovalAttempt(v WallRemovalAttempt) (WallRemovalAttempt, error) {
	if err := ValidateIdentity(v.Identity); err != nil {
		return WallRemovalAttempt{}, err
	}
	if err := buildingAttempt(v.Attempt); err != nil {
		return WallRemovalAttempt{}, err
	}
	if err := authorityOwner(v.Owner); err != nil {
		return WallRemovalAttempt{}, err
	}
	if err := buildingUnknown(v.Owner); err != nil {
		return WallRemovalAttempt{}, err
	}
	if v.Generation == 0 || v.Owner.GetControllerSessionId() != v.Attempt.GetControllerSessionId() {
		return WallRemovalAttempt{}, contract("wall removal admission owner or generation mismatch")
	}
	if validID(v.Target) != nil {
		return WallRemovalAttempt{}, contract("invalid wall removal target")
	}
	v.Identity = proto.Clone(v.Identity).(*c.Identity)
	v.Attempt = proto.Clone(v.Attempt).(*c.AttemptKey)
	v.Owner = proto.Clone(v.Owner).(*a.Owner)
	return v, nil
}

func wallRemovalEvidence(evidence *r.EffectEvidence, expected WallRemovalAttempt) (*r.WallEffect, error) {
	wall := evidence.GetWall()
	if wall == nil || wall.TargetId == nil || wall.GetTargetId() != expected.Target {
		return nil, contract("wall removal target mismatch")
	}
	allowed := &r.WallEffect{TargetId: wall.TargetId, RemovalId: wall.RemovalId, WorkerIds: wall.WorkerIds, ReleasedCount: wall.ReleasedCount, DemolitionObserved: wall.DemolitionObserved, Site: wall.Site}
	if !proto.Equal(wall, allowed) {
		return nil, contract("wall removal effect fields missing or unsupported")
	}
	return wall, nil
}

func wallRemovalReceipt(v *r.Receipt, expected WallRemovalAttempt) error {
	if v == nil || buildingUnknown(v) != nil || !proto.Equal(v.Attempt, expected.Attempt) || !proto.Equal(v.AuthorizingOwner, expected.Owner) {
		return contract("wall removal admission mismatch")
	}
	if err := buildingContext(v.AdmittedContext, expected.Identity, expected.Generation, true); err != nil {
		return err
	}
	switch outcome := v.Outcome.(type) {
	case *r.Receipt_Applied:
		if outcome.Applied == nil {
			return contract("wall removal applied missing")
		}
		_, err := wallRemovalEvidence(outcome.Applied.GetObserved(), expected)
		return err
	case *r.Receipt_Uncertain:
		if outcome.Uncertain == nil {
			return contract("wall removal uncertainty missing")
		}
		if outcome.Uncertain.LastObserved != nil {
			_, err := wallRemovalEvidence(outcome.Uncertain.LastObserved, expected)
			return err
		}
		return nil
	default:
		return contract("unsupported wall removal receipt")
	}
}

type WallRemovalWriter struct{ client *Client }

func NewWallRemovalWriter(client *Client) (*WallRemovalWriter, error) {
	if client == nil {
		return nil, contract("wall removal client missing")
	}
	return &WallRemovalWriter{client}, nil
}

// ApplyWallRemoval dispatches one already-admitted guarded demolition or
// backup removal.
func (writer *WallRemovalWriter) ApplyWallRemoval(ctx context.Context, pre *a.WritePrecondition, owner *a.Owner, target string) (*o.ExecuteReply, Result, error) {
	if writer == nil || writer.client == nil || pre == nil || buildingUnknown(pre) != nil || ValidateIdentity(pre.Identity) != nil || buildingAttempt(pre.Attempt) != nil || pre.GetExpectedGeneration() == 0 || validID(pre.GetLeaseId()) != nil {
		return nil, Result{}, contract("invalid wall removal execution")
	}
	if validID(target) != nil {
		return nil, Result{}, contract("invalid wall removal target")
	}
	expected, err := wallRemovalAttempt(WallRemovalAttempt{Identity: pre.Identity, Attempt: pre.Attempt, Owner: owner, Generation: pre.GetExpectedGeneration(), Target: target})
	if err != nil {
		return nil, Result{}, err
	}
	pre = proto.Clone(pre).(*a.WritePrecondition)
	reply := &o.ExecuteReply{}
	raw, err := writer.client.protoCall(ctx, "rimgovernor/operations_execute", &o.ExecuteRequest{Precondition: pre, Operation: wallRemovalOperation(target)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.ExecuteReply_Receipt:
		err = wallRemovalReceipt(v.Receipt, expected)
	case *o.ExecuteReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("wall removal execute outcome missing")
	}
	return reply, raw, err
}

func (client *Client) LookupWallRemoval(ctx context.Context, w WallRemovalAttempt) (*r.LookupReply, Result, error) {
	expected, err := wallRemovalAttempt(w)
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
		err = wallRemovalReceipt(v.Receipt, expected)
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, expected.Attempt) {
			err = contract("wall removal in-flight attempt mismatch")
		} else {
			err = buildingContext(v.InFlight.AdmittedContext, expected.Identity, expected.Generation, true)
		}
	case *r.LookupReply_Unknown:
		if v.Unknown == nil {
			err = contract("wall removal unknown context missing")
		} else {
			err = buildingContext(v.Unknown.Context, expected.Identity, 0, false)
		}
	case *r.LookupReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("wall removal lookup outcome missing")
	}
	return reply, raw, err
}

func (client *Client) ObserveWallRemovalProgress(ctx context.Context, w WallRemovalAttempt, admitted *r.Receipt) (*r.ProgressReply, Result, error) {
	expected, err := wallRemovalAttempt(w)
	if err != nil {
		return nil, Result{}, err
	}
	if admitted != nil {
		if err = wallRemovalReceipt(admitted, expected); err != nil {
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
		err = wallRemovalProgress(v.Progress, expected, admitted)
	case *r.ProgressReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("wall removal progress outcome missing")
	}
	return reply, raw, err
}

func wallRemovalProgress(v *r.Progress, expected WallRemovalAttempt, admitted *r.Receipt) error {
	if v == nil || v.CompleteInspection == nil || !proto.Equal(v.Attempt, expected.Attempt) {
		return contract("wall removal progress attempt mismatch")
	}
	if err := buildingContext(v.Context, expected.Identity, 0, false); err != nil {
		return err
	}
	if admitted != nil && v.Context.GetTick() < admitted.AdmittedContext.GetTick() {
		return contract("wall removal progress predates admission")
	}
	switch outcome := v.Effect.(type) {
	case *r.Progress_Unknown:
		if outcome.Unknown == nil || !diagnostic(outcome.Unknown.Reason) {
			return contract("wall removal unknown progress missing")
		}
		return nil
	case *r.Progress_Pending:
		if outcome.Pending == nil || !v.GetCompleteInspection() {
			return contract("wall removal pending missing")
		}
		_, err := wallRemovalEvidence(outcome.Pending.Evidence, expected)
		return err
	case *r.Progress_Completed:
		if outcome.Completed == nil || !v.GetCompleteInspection() {
			return contract("wall removal completed missing")
		}
		_, err := wallRemovalEvidence(outcome.Completed.Evidence, expected)
		return err
	case *r.Progress_Absent:
		if outcome.Absent == nil || validID(outcome.Absent.GetInspectionToken()) != nil || !v.GetCompleteInspection() {
			return contract("wall removal absence lacks inspection")
		}
		return nil
	case *r.Progress_Unsuccessful:
		if outcome.Unsuccessful == nil || outcome.Unsuccessful.Reason == nil || outcome.Unsuccessful.GetReason() == 0 || !v.GetCompleteInspection() || !diagnostic(outcome.Unsuccessful.Detail) {
			return contract("wall removal unsuccessful reason missing")
		}
		_, err := wallRemovalEvidence(outcome.Unsuccessful.Evidence, expected)
		return err
	default:
		return contract("wall removal progress state missing")
	}
}
