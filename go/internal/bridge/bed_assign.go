package bridge

import (
	"context"

	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// BedAssignPreviousBed mirrors domain.PreviousBed's wire Assignment oneof:
// the pawn's exact currently-owned bed expected at admission, or explicitly
// none.
type BedAssignPreviousBed struct {
	ID    string
	Clear bool
}

func (p BedAssignPreviousBed) valid() bool { return p.Clear != (p.ID != "") }

func (p BedAssignPreviousBed) wire() *o.Assignment {
	if p.Clear {
		return &o.Assignment{Value: &o.Assignment_Clear{Clear: &o.Clear{}}}
	}
	return &o.Assignment{Value: &o.Assignment_EntityId{EntityId: p.ID}}
}

// BedAssignAttempt carries the exact already-selected pawn/bed pair
// MaintainSleeping's routine planner chose. The native contract is the typed
// AssignBed operation (contracts/proto/operations.proto), the same one the
// legacy JSON home/upkeep_bed tool (UpkeepBedTool.cs) drives; no native
// adapter wires Operation_AssignBed into the typed Execute/Preview dispatch
// yet (NativeOperationTools.cs), an open native acceptance item like
// Repair/Clean/Equip's PawnOrderKind gap.
type BedAssignAttempt struct {
	Identity   *c.Identity
	Attempt    *c.AttemptKey
	Generation uint64
	Pawn, Bed  string
	PawnToken  string
	BedToken   string
	Previous   BedAssignPreviousBed
}

func bedAssignOperation(pawn, pawnToken, bed, bedToken string, previous BedAssignPreviousBed) *o.Operation {
	return &o.Operation{Command: &o.Operation_AssignBed{AssignBed: &o.AssignBed{
		Pawn: gearEntity(pawn, pawnToken), Bed: gearEntity(bed, bedToken), ExpectedPreviousBed: previous.wire(),
	}}}
}

func bedAssignCommand(pawn, pawnToken, bed, bedToken string, previous BedAssignPreviousBed) error {
	if validID(pawn) != nil || validID(pawnToken) != nil || validID(bed) != nil || validID(bedToken) != nil || pawn == bed {
		return contract("invalid bed assign command")
	}
	if !previous.valid() || (!previous.Clear && validID(previous.ID) != nil) || (!previous.Clear && previous.ID == bed) {
		return contract("invalid bed assign previous bed")
	}
	return nil
}

// PreviewBedAssign checks an exact already-selected pawn/bed pair; acceptance
// is not authority.
func (client *Client) PreviewBedAssign(ctx context.Context, identity *c.Identity, pawn, pawnToken, bed, bedToken string, previous BedAssignPreviousBed) (*o.PreviewReply, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	if err := bedAssignCommand(pawn, pawnToken, bed, bedToken, previous); err != nil {
		return nil, Result{}, err
	}
	identity = proto.Clone(identity).(*c.Identity)
	reply := &o.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &o.PreviewRequest{Identity: identity, Operation: bedAssignOperation(pawn, pawnToken, bed, bedToken, previous)}, reply)
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
			return reply, raw, contract("bed assign preview missing")
		}
		if err = buildingContext(value.Context, identity, 0, false); err != nil {
			break
		}
		effect := value.Projected.GetBed()
		if value.Accepted == nil || !diagnostic(value.Reason) || value.Preparation != nil || effect == nil {
			err = contract("bed assign preview facts missing")
			break
		}
		expected := &r.BedEffect{PawnId: proto.String(pawn), BedId: proto.String(bed), PreviousBedId: bedAssignPreviousBedID(previous), Assigned: proto.Bool(value.GetAccepted())}
		if !proto.Equal(effect, expected) {
			err = contract("bed assign preview projection mismatch")
		}
	default:
		err = contract("bed assign preview outcome missing")
	}
	return reply, raw, err
}

func bedAssignPreviousBedID(previous BedAssignPreviousBed) *string {
	if previous.Clear {
		return nil
	}
	return proto.String(previous.ID)
}

func bedAssignAttempt(v BedAssignAttempt) (BedAssignAttempt, error) {
	if err := ValidateIdentity(v.Identity); err != nil {
		return BedAssignAttempt{}, err
	}
	if err := buildingAttempt(v.Attempt); err != nil {
		return BedAssignAttempt{}, err
	}
	if v.Generation == 0 {
		return BedAssignAttempt{}, contract("bed assign admission owner or generation mismatch")
	}
	if err := bedAssignCommand(v.Pawn, v.PawnToken, v.Bed, v.BedToken, v.Previous); err != nil {
		return BedAssignAttempt{}, err
	}
	v.Identity = proto.Clone(v.Identity).(*c.Identity)
	v.Attempt = proto.Clone(v.Attempt).(*c.AttemptKey)
	return v, nil
}

func bedAssignEvidence(evidence *r.EffectEvidence, expected BedAssignAttempt) (*r.BedEffect, error) {
	bed := evidence.GetBed()
	if bed == nil || bed.PawnId == nil || bed.GetPawnId() != expected.Pawn || bed.BedId == nil || bed.GetBedId() != expected.Bed {
		return nil, contract("bed assign pawn or bed mismatch")
	}
	allowed := &r.BedEffect{PawnId: bed.PawnId, BedId: bed.BedId, PreviousBedId: bed.PreviousBedId, Assigned: bed.Assigned, Sleeping: bed.Sleeping}
	if !proto.Equal(bed, allowed) {
		return nil, contract("bed assign effect fields missing or unsupported")
	}
	if expected.Previous.Clear {
		if bed.PreviousBedId != nil {
			return nil, contract("bed assign previous bed unexpectedly present")
		}
	} else if bed.PreviousBedId == nil || bed.GetPreviousBedId() != expected.Previous.ID {
		return nil, contract("bed assign previous bed mismatch")
	}
	return bed, nil
}

func bedAssignReceipt(v *r.Receipt, expected BedAssignAttempt) error {
	if v == nil || buildingUnknown(v) != nil || !proto.Equal(v.Attempt, expected.Attempt) {
		return contract("bed assign admission mismatch")
	}
	if err := buildingContext(v.AdmittedContext, expected.Identity, expected.Generation, true); err != nil {
		return err
	}
	switch outcome := v.Outcome.(type) {
	case *r.Receipt_Applied:
		if outcome.Applied == nil {
			return contract("bed assign applied missing")
		}
		_, err := bedAssignEvidence(outcome.Applied.GetObserved(), expected)
		return err
	case *r.Receipt_Uncertain:
		if outcome.Uncertain == nil {
			return contract("bed assign uncertainty missing")
		}
		if outcome.Uncertain.LastObserved != nil {
			_, err := bedAssignEvidence(outcome.Uncertain.LastObserved, expected)
			return err
		}
		return nil
	default:
		return contract("unsupported bed assign receipt")
	}
}

type BedAssignWriter struct{ client *Client }

func NewBedAssignWriter(client *Client) (*BedAssignWriter, error) {
	if client == nil {
		return nil, contract("bed assign client missing")
	}
	return &BedAssignWriter{client}, nil
}

// ApplyBedAssign dispatches one already-admitted bed ownership assignment.
func (writer *BedAssignWriter) ApplyBedAssign(ctx context.Context, pre *a.WritePrecondition, pawn, pawnToken, bed, bedToken string, previous BedAssignPreviousBed) (*o.ExecuteReply, Result, error) {
	if writer == nil || writer.client == nil || pre == nil || buildingUnknown(pre) != nil || ValidateIdentity(pre.Identity) != nil || buildingAttempt(pre.Attempt) != nil || pre.GetExpectedGeneration() == 0 {
		return nil, Result{}, contract("invalid bed assign execution")
	}
	if err := bedAssignCommand(pawn, pawnToken, bed, bedToken, previous); err != nil {
		return nil, Result{}, err
	}
	expected, err := bedAssignAttempt(BedAssignAttempt{Identity: pre.Identity, Attempt: pre.Attempt, Generation: pre.GetExpectedGeneration(), Pawn: pawn, Bed: bed, PawnToken: pawnToken, BedToken: bedToken, Previous: previous})
	if err != nil {
		return nil, Result{}, err
	}
	pre = proto.Clone(pre).(*a.WritePrecondition)
	reply := &o.ExecuteReply{}
	raw, err := writer.client.protoCall(ctx, "rimgovernor/operations_execute", &o.ExecuteRequest{Precondition: pre, Operation: bedAssignOperation(pawn, pawnToken, bed, bedToken, previous)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.ExecuteReply_Receipt:
		err = bedAssignReceipt(v.Receipt, expected)
	case *o.ExecuteReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("bed assign execute outcome missing")
	}
	return reply, raw, err
}

func (client *Client) LookupBedAssign(ctx context.Context, w BedAssignAttempt) (*r.LookupReply, Result, error) {
	expected, err := bedAssignAttempt(w)
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
		err = bedAssignReceipt(v.Receipt, expected)
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, expected.Attempt) {
			err = contract("bed assign in-flight attempt mismatch")
		} else {
			err = buildingContext(v.InFlight.AdmittedContext, expected.Identity, expected.Generation, true)
		}
	case *r.LookupReply_Unknown:
		if v.Unknown == nil {
			err = contract("bed assign unknown context missing")
		} else {
			err = buildingContext(v.Unknown.Context, expected.Identity, 0, false)
		}
	case *r.LookupReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("bed assign lookup outcome missing")
	}
	return reply, raw, err
}

func (client *Client) ObserveBedAssignProgress(ctx context.Context, w BedAssignAttempt, admitted *r.Receipt) (*r.ProgressReply, Result, error) {
	expected, err := bedAssignAttempt(w)
	if err != nil {
		return nil, Result{}, err
	}
	if admitted != nil {
		if err = bedAssignReceipt(admitted, expected); err != nil {
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
		err = bedAssignProgress(v.Progress, expected, admitted)
	case *r.ProgressReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("bed assign progress outcome missing")
	}
	return reply, raw, err
}

func bedAssignProgress(v *r.Progress, expected BedAssignAttempt, admitted *r.Receipt) error {
	if v == nil || v.CompleteInspection == nil || !proto.Equal(v.Attempt, expected.Attempt) {
		return contract("bed assign progress attempt mismatch")
	}
	if err := buildingContext(v.Context, expected.Identity, 0, false); err != nil {
		return err
	}
	if admitted != nil && v.Context.GetTick() < admitted.AdmittedContext.GetTick() {
		return contract("bed assign progress predates admission")
	}
	switch outcome := v.Effect.(type) {
	case *r.Progress_Unknown:
		if outcome.Unknown == nil || !diagnostic(outcome.Unknown.Reason) {
			return contract("bed assign unknown progress missing")
		}
		return nil
	case *r.Progress_Pending:
		if outcome.Pending == nil || !v.GetCompleteInspection() {
			return contract("bed assign pending missing")
		}
		_, err := bedAssignEvidence(outcome.Pending.Evidence, expected)
		return err
	case *r.Progress_Completed:
		if outcome.Completed == nil || !v.GetCompleteInspection() {
			return contract("bed assign completed missing")
		}
		_, err := bedAssignEvidence(outcome.Completed.Evidence, expected)
		return err
	case *r.Progress_Absent:
		if outcome.Absent == nil || validID(outcome.Absent.GetInspectionToken()) != nil || !v.GetCompleteInspection() {
			return contract("bed assign absence lacks inspection")
		}
		return nil
	case *r.Progress_Unsuccessful:
		if outcome.Unsuccessful == nil || outcome.Unsuccessful.Reason == nil || outcome.Unsuccessful.GetReason() == 0 || !v.GetCompleteInspection() || !diagnostic(outcome.Unsuccessful.Detail) {
			return contract("bed assign unsuccessful reason missing")
		}
		_, err := bedAssignEvidence(outcome.Unsuccessful.Evidence, expected)
		return err
	default:
		return contract("bed assign progress state missing")
	}
}
