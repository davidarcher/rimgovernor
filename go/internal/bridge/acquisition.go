package bridge

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

type AcquisitionTarget struct {
	Acquisition domain.Acquisition
	Token       string
}
type AcquisitionRead struct {
	Context *c.ObservationContext
	Targets []AcquisitionTarget
}
type AcquisitionAttempt struct {
	Identity    *c.Identity
	Attempt     *c.AttemptKey
	Generation  uint64
	Acquisition domain.Acquisition
}
type AcquisitionControl struct{ client *Client }

func NewAcquisitionControl(client *Client) (*AcquisitionControl, error) {
	if client == nil {
		return nil, contract("acquisition client missing")
	}
	return &AcquisitionControl{client}, nil
}
func (client *Client) ReadAcquisition(ctx context.Context, identity *c.Identity, cell domain.Cell) (AcquisitionRead, Result, error) {
	reply, raw, err := client.ReadColonyFacts(ctx, identity, false, nil)
	if err != nil {
		return AcquisitionRead{}, raw, err
	}
	v := reply.GetObserved()
	for _, issue := range v.Issues {
		if issue.GetField() == "acquisition" {
			return AcquisitionRead{}, raw, ErrUnavailable
		}
	}
	out := AcquisitionRead{Context: proto.Clone(v.Context).(*c.ObservationContext), Targets: []AcquisitionTarget{}}
	for _, row := range v.Acquisition {
		if row.GetDesignated() || row.Source.Position.GetX() != cell.X || row.Source.Position.GetZ() != cell.Z {
			continue
		}
		target, err := domain.NewAcquisition(row.Source.GetId(), row.GetResource(), cell)
		if err != nil {
			return AcquisitionRead{}, raw, err
		}
		out.Targets = append(out.Targets, AcquisitionTarget{target, row.Source.Snapshot.GetToken()})
	}
	return out, raw, nil
}

// acquisitionOperation is the wire operation for target. A preview carries
// the snapshot token the census read; an execute omits it (#243): the
// acquisition kinds are dispatched under a running clock, where the token
// (growth, hit points, position) moves every tick, and the native
// apply-time preconditions (#242) are the check that refuses a moved world.
func acquisitionOperation(target AcquisitionTarget, withToken bool) *op.Operation {
	source := &op.EntityPrecondition{EntityId: proto.String(target.Acquisition.Thing())}
	if withToken {
		source.ExpectedSnapshotToken = proto.String(target.Token)
	}
	return &op.Operation{Command: &op.Operation_AcquireResource{AcquireResource: &op.AcquireResource{Source: source, ResourceDefName: proto.String(target.Acquisition.Definition()), Cell: &c.Cell{X: proto.Int32(target.Acquisition.Cell().X), Z: proto.Int32(target.Acquisition.Cell().Z)}}}}
}

// cancelAcquisitionOperation withdraws the designation acquisitionOperation
// placed; it carries no token (the designation, not the plant, is the
// precondition).
func cancelAcquisitionOperation(target AcquisitionTarget) *op.Operation {
	return &op.Operation{Command: &op.Operation_CancelAcquisition{CancelAcquisition: &op.CancelAcquisition{Source: &op.EntityPrecondition{EntityId: proto.String(target.Acquisition.Thing())}, ResourceDefName: proto.String(target.Acquisition.Definition()), Cell: &c.Cell{X: proto.Int32(target.Acquisition.Cell().X), Z: proto.Int32(target.Acquisition.Cell().Z)}}}}
}
func validAcquisition(target AcquisitionTarget) error {
	if _, err := domain.NewAcquisition(target.Acquisition.Thing(), target.Acquisition.Definition(), target.Acquisition.Cell()); err != nil {
		return err
	}
	return validID(target.Token)
}
func (client *Client) PreviewAcquisition(ctx context.Context, identity *c.Identity, target AcquisitionTarget) (*op.PreviewReply, Result, error) {
	if ValidateIdentity(identity) != nil || validAcquisition(target) != nil {
		return nil, Result{}, contract("invalid acquisition preview")
	}
	reply := &op.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &op.PreviewRequest{Identity: proto.Clone(identity).(*c.Identity), Operation: acquisitionOperation(target, true)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown acquisition preview fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetEvaluated()
	if v == nil || v.Accepted == nil || !v.GetAccepted() || v.Preparation != nil || buildingContext(v.Context, identity, 0, false) != nil || v.Projected != nil {
		return nil, raw, contract("invalid acquisition preview evidence")
	}
	return reply, raw, nil
}
func (writer *AcquisitionControl) Acquire(ctx context.Context, pre *a.WritePrecondition, target AcquisitionTarget) (*op.ExecuteReply, Result, error) {
	if writer == nil || writer.client == nil || pre == nil || buildingUnknown(pre) != nil || ValidateIdentity(pre.Identity) != nil || buildingAttempt(pre.Attempt) != nil || pre.GetExpectedGeneration() == 0 || validAcquisition(target) != nil {
		return nil, Result{}, contract("invalid acquisition execution")
	}
	reply := &op.ExecuteReply{}
	raw, err := writer.client.protoCall(ctx, "rimgovernor/operations_execute", &op.ExecuteRequest{Precondition: proto.Clone(pre).(*a.WritePrecondition), Operation: acquisitionOperation(target, false)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown acquisition execute fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetReceipt()
	err = acquisitionReceipt(v, AcquisitionAttempt{pre.Identity, pre.Attempt, pre.GetExpectedGeneration(), target.Acquisition}, proto.Bool(true))
	return reply, raw, err
}

// Withdraw cancels the plant-harvest designation an earlier Acquire placed
// (#291), under the withdrawal attempt the journal opened for it. The
// applied evidence must show the designation gone.
func (writer *AcquisitionControl) Withdraw(ctx context.Context, pre *a.WritePrecondition, target AcquisitionTarget) (*op.ExecuteReply, Result, error) {
	if writer == nil || writer.client == nil || pre == nil || buildingUnknown(pre) != nil || ValidateIdentity(pre.Identity) != nil || buildingAttempt(pre.Attempt) != nil || pre.GetExpectedGeneration() == 0 {
		return nil, Result{}, contract("invalid acquisition withdrawal")
	}
	if _, err := domain.NewAcquisition(target.Acquisition.Thing(), target.Acquisition.Definition(), target.Acquisition.Cell()); err != nil {
		return nil, Result{}, contract("invalid acquisition withdrawal")
	}
	reply := &op.ExecuteReply{}
	raw, err := writer.client.protoCall(ctx, "rimgovernor/operations_execute", &op.ExecuteRequest{Precondition: proto.Clone(pre).(*a.WritePrecondition), Operation: cancelAcquisitionOperation(target)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown acquisition withdrawal fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	err = acquisitionReceipt(reply.GetReceipt(), AcquisitionAttempt{pre.Identity, pre.Attempt, pre.GetExpectedGeneration(), target.Acquisition}, proto.Bool(false))
	return reply, raw, err
}
func validAcquisitionAttempt(w AcquisitionAttempt) error {
	if ValidateIdentity(w.Identity) != nil || buildingAttempt(w.Attempt) != nil || w.Generation == 0 {
		return contract("invalid acquisition attempt")
	}
	_, err := domain.NewAcquisition(w.Acquisition.Thing(), w.Acquisition.Definition(), w.Acquisition.Cell())
	return err
}

// ValidateAcquisitionEffect checks exact output identities and conservation of
// placement counts. Positive expected yield is never effect evidence.
func ValidateAcquisitionEffect(v *r.EffectEvidence, acquisition domain.Acquisition) error {
	d := v.GetAcquisition()
	if d == nil || buildingUnknown(v) != nil || d.GetSourceId() != acquisition.Thing() || d.GetResourceDef() != acquisition.Definition() || d.Cell == nil || d.Cell.X == nil || d.Cell.Z == nil || d.Cell.GetX() != acquisition.Cell().X || d.Cell.GetZ() != acquisition.Cell().Z || d.Designated == nil || d.LaborFinished == nil || d.ProducedUnits == nil || d.OutputComplete == nil || d.OutputObserved == nil || d.GetProducedUnits() < 0 || len(d.Outputs) > 256 || len(d.GetPendingReason()) > 512 {
		return contract("acquisition effect mismatch")
	}
	var units int64
	seen := map[string]bool{}
	for _, row := range d.Outputs {
		if row == nil || validID(row.GetThingId()) != nil || seen[row.GetThingId()] || row.Units == nil || row.GetUnits() <= 0 {
			return contract("invalid acquisition output")
		}
		seen[row.GetThingId()] = true
		units += int64(row.GetUnits())
	}
	if d.GetOutputComplete() && units != int64(d.GetProducedUnits()) || d.GetOutputObserved() && (len(d.Outputs) == 0 || !d.GetOutputComplete()) {
		return contract("inconsistent acquisition output")
	}
	return nil
}

// acquisitionEffect validates admission evidence; designated, when set,
// is the designation state the write must have left (true after Acquire,
// false after Withdraw). A lookup passes nil: the ledger entry may be
// either attempt of the action.
func acquisitionEffect(v *r.EffectEvidence, acquisition domain.Acquisition, designated *bool) error {
	if err := ValidateAcquisitionEffect(v, acquisition); err != nil {
		return err
	}
	if designated != nil && v.GetAcquisition().GetDesignated() != *designated {
		return contract("acquisition designation mismatch")
	}
	return nil
}
func acquisitionReceipt(v *r.Receipt, w AcquisitionAttempt, designated *bool) error {
	if v == nil || buildingUnknown(v) != nil || !proto.Equal(v.Attempt, w.Attempt) || buildingContext(v.AdmittedContext, w.Identity, w.Generation, true) != nil {
		return contract("acquisition admission mismatch")
	}
	switch out := v.Outcome.(type) {
	case *r.Receipt_Applied:
		return acquisitionEffect(out.Applied.GetObserved(), w.Acquisition, designated)
	case *r.Receipt_Uncertain:
		if out.Uncertain == nil {
			return contract("acquisition uncertainty missing")
		}
		if out.Uncertain.LastObserved != nil {
			return acquisitionEffect(out.Uncertain.LastObserved, w.Acquisition, designated)
		}
		return nil
	default:
		return contract("unsupported acquisition receipt")
	}
}

// acquisitionUnsuccessful is the evidence shape of a failed acquisition:
// labor finished with nothing produced, or the designation gone before any
// labor (withdrawn, removed by the player, or the source vanished; #291).
func acquisitionUnsuccessful(d *r.AcquisitionEffect) bool {
	return d.GetLaborFinished() && d.GetOutputComplete() && d.GetProducedUnits() == 0 || !d.GetLaborFinished() && !d.GetDesignated() && d.GetProducedUnits() == 0
}
func (client *Client) LookupAcquisition(ctx context.Context, w AcquisitionAttempt) (*r.LookupReply, Result, error) {
	if err := validAcquisitionAttempt(w); err != nil {
		return nil, Result{}, err
	}
	reply := &r.LookupReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_lookup", &r.LookupRequest{Identity: w.Identity, Attempt: w.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown acquisition lookup fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	switch v := reply.Outcome.(type) {
	case *r.LookupReply_Receipt:
		err = acquisitionReceipt(v.Receipt, w, nil)
	case *r.LookupReply_Unknown:
		err = buildingContext(v.Unknown.GetContext(), w.Identity, 0, false)
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, w.Attempt) {
			return nil, raw, contract("acquisition in-flight mismatch")
		}
		err = buildingContext(v.InFlight.AdmittedContext, w.Identity, w.Generation, true)
	default:
		err = contract("acquisition lookup outcome missing")
	}
	return reply, raw, err
}
func (client *Client) ObserveAcquisition(ctx context.Context, w AcquisitionAttempt, admitted *r.Receipt) (*r.ProgressReply, Result, error) {
	if validAcquisitionAttempt(w) != nil || acquisitionReceipt(admitted, w, nil) != nil {
		return nil, Result{}, contract("acquisition observation admission mismatch")
	}
	reply := &r.ProgressReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_observe_progress", &r.ProgressRequest{Identity: w.Identity, Attempt: w.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown acquisition progress fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetProgress()
	if v == nil || !proto.Equal(v.Attempt, w.Attempt) || buildingContext(v.Context, w.Identity, 0, false) != nil || v.Context.GetTick() < admitted.AdmittedContext.GetTick() {
		return nil, raw, contract("acquisition progress scope mismatch")
	}
	switch out := v.Effect.(type) {
	case *r.Progress_Unknown:
		if out.Unknown == nil {
			return nil, raw, contract("missing acquisition uncertainty")
		}
	case *r.Progress_Completed:
		if !v.GetCompleteInspection() {
			return nil, raw, contract("incomplete acquisition completion")
		}
		err = ValidateAcquisitionEffect(out.Completed.GetEvidence(), w.Acquisition)
		d := out.Completed.GetEvidence().GetAcquisition()
		if !d.GetLaborFinished() || !d.GetOutputComplete() || !d.GetOutputObserved() || d.GetProducedUnits() <= 0 {
			return nil, raw, contract("unverified acquisition completion")
		}
	case *r.Progress_Unsuccessful:
		if !v.GetCompleteInspection() || out.Unsuccessful.GetReason() != r.UnsuccessfulReason_UNSUCCESSFUL_REASON_OUTCOME_NOT_ACHIEVED {
			return nil, raw, contract("unverified acquisition failure")
		}
		err = ValidateAcquisitionEffect(out.Unsuccessful.GetEvidence(), w.Acquisition)
		if !acquisitionUnsuccessful(out.Unsuccessful.GetEvidence().GetAcquisition()) {
			return nil, raw, contract("unverified acquisition failure")
		}
	case *r.Progress_Pending:
		err = ValidateAcquisitionEffect(out.Pending.GetEvidence(), w.Acquisition)
		d := out.Pending.GetEvidence().GetAcquisition()
		if d.GetLaborFinished() || !d.GetDesignated() {
			return nil, raw, contract("invalid acquisition pending")
		}
	default:
		err = contract("unsupported acquisition progress")
	}
	return reply, raw, err
}
