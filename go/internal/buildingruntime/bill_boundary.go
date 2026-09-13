package buildingruntime

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

type BillNative interface {
	ReadBillTarget(context.Context, *c.Identity, string) (bridge.BillRead, bridge.Result, error)
	PreviewBill(context.Context, *c.Identity, domain.ProductionBill) (*op.PreviewReply, bridge.Result, error)
	ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
	LookupBill(context.Context, bridge.BillAttempt) (*r.LookupReply, bridge.Result, error)
	ObserveBill(context.Context, bridge.BillAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type BillWriter interface {
	AddBill(context.Context, *a.WritePrecondition, domain.ProductionBill) (*op.ExecuteReply, bridge.Result, error)
}
type BillCapabilities struct {
	Native BillNative
	Writer BillWriter
}
type billBoundary struct {
	*Boundary
	bill BillCapabilities
}

func (b *billBoundary) InspectBill(ctx context.Context, target executor.Target) (executor.BillInspection, error) {
	out := executor.BillInspection{StartedAt: b.clock.Now()}
	bill, ok := target.Action.ProductionBill()
	if !ok {
		return out, executor.ErrEvidence
	}
	read, _, err := b.bill.Native.ReadBillTarget(ctx, boundaryIdentity(target.Snapshot), bill.Bench())
	if err != nil {
		return out, err
	}
	current, err := boundaryContext(read.Context, target.Snapshot)
	if err != nil {
		return out, err
	}
	if read.Token != bill.BeforeToken() {
		return out, executor.ErrHeld
	}
	selected := bill
	preview, _, err := b.bill.Native.PreviewBill(ctx, boundaryIdentity(current), selected)
	if err != nil {
		return out, err
	}
	v := preview.GetEvaluated()
	if v == nil || !v.GetAccepted() {
		return out, executor.ErrHeld
	}
	if v.Projected != nil {
		return out, executor.ErrEvidence
	}
	if _, err = boundaryContext(v.Context, current); err != nil {
		return out, err
	}
	emergency, _, err := b.bill.Native.ReadEmergency(ctx, boundaryIdentity(current))
	if err != nil {
		return out, err
	}
	if _, err = boundaryContext(emergency.Context, current); err != nil {
		return out, err
	}
	if read.Context.GetTick() != v.Context.GetTick() || v.Context.GetTick() != emergency.Context.GetTick() {
		return out, executor.ErrHeld
	}
	out.Current, out.Tick, out.Bill, out.SnapshotToken, out.Accepted = current, domain.Tick(v.Context.GetTick()), bill, selected.BeforeToken(), true
	out.Emergency, err = policy.NewEmergencySnapshot(current, out.Tick, emergency.Facts)
	out.ObservedAt = b.clock.Now()
	return out, err
}
func (b *billBoundary) billAttempt(p executor.Placement) bridge.BillAttempt {
	bill, _ := p.Action.ProductionBill()
	return bridge.BillAttempt{Identity: boundaryIdentity(p.Snapshot), Attempt: b.attempt(p), Owner: &a.Owner{ControllerSessionId: proto.String(b.session), PlayerDirection: proto.Uint64(uint64(p.Snapshot.Direction))}, Generation: uint64(p.Snapshot.Native), Bill: bill}
}
func (b *billBoundary) AddBill(ctx context.Context, request executor.BillDispatch) (executor.Receipt, error) {
	p := request.Attempt
	out := executor.Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptUnknown}
	bill, ok := p.Action.ProductionBill()
	if !ok || request.SnapshotToken != bill.BeforeToken() {
		return out, executor.ErrEvidence
	}
	lease, err := b.leases.Lease(p.Snapshot)
	if err != nil {
		return out, err
	}
	reply, _, err := b.bill.Writer.AddBill(ctx, &a.WritePrecondition{Identity: boundaryIdentity(p.Snapshot), Attempt: b.attempt(p), LeaseId: proto.String(lease), ExpectedGeneration: proto.Uint64(uint64(p.Snapshot.Native))}, bill)
	if err != nil {
		return out, err
	} // Refusals remain uncertain until correlated inspection.
	if err = boundaryAdmission(reply.GetReceipt(), p, b.session); err != nil {
		return out, err
	}
	if reply.GetReceipt().GetApplied() != nil {
		out.Kind = domain.ReceiptAccepted
	}
	return out, nil
}
func (b *billBoundary) ObserveBill(ctx context.Context, p executor.Placement, current domain.GenerationSnapshot) (executor.BillEvidence, error) {
	out := executor.BillEvidence{StartedAt: b.clock.Now(), Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Effect: domain.EffectUnknown}}
	if !boundaryWorld(current, p.Snapshot) {
		return out, executor.ErrAuthority
	}
	w := b.billAttempt(p)
	lookup, _, err := b.bill.Native.LookupBill(ctx, w)
	if err != nil {
		return out, err
	}
	admitted := lookup.GetReceipt()
	if admitted == nil {
		return out, executor.ErrHeld
	}
	if err = boundaryAdmission(admitted, p, b.session); err != nil {
		return out, err
	}
	reply, _, err := b.bill.Native.ObserveBill(ctx, w, admitted)
	if err != nil {
		return out, err
	}
	v := reply.GetProgress()
	if v == nil || !proto.Equal(v.Attempt, w.Attempt) {
		return out, executor.ErrEvidence
	}
	out.Observation.Snapshot, err = boundaryContext(v.Context, current)
	if err != nil {
		return out, err
	}
	if v.Context.GetTick() < int64(p.Tick) {
		return out, executor.ErrEvidence
	}
	out.Observation.Tick, out.Observation.Causality = domain.Tick(v.Context.GetTick()), domain.AfterDispatch
	out.Complete, out.Bill = v.GetCompleteInspection(), w.Bill
	var evidence *r.EffectEvidence
	switch effect := v.Effect.(type) {
	case *r.Progress_Completed:
		evidence = effect.Completed.GetEvidence()
		out.Observation.Effect = domain.EffectCompleted
	case *r.Progress_Unsuccessful:
		if effect.Unsuccessful.GetReason() != r.UnsuccessfulReason_UNSUCCESSFUL_REASON_OUTCOME_NOT_ACHIEVED {
			return out, executor.ErrEvidence
		}
		evidence = effect.Unsuccessful.GetEvidence()
		out.Observation.Effect = domain.EffectUnsuccessful
		out.Observation.UnsuccessfulReason = domain.OutcomeNotAchieved
	case *r.Progress_Pending:
		evidence = effect.Pending.GetEvidence()
		out.Observation.Effect = domain.EffectPending
	case *r.Progress_Absent:
		if !out.Complete || effect.Absent == nil || !boundaryID(effect.Absent.GetInspectionToken()) {
			return out, executor.ErrEvidence
		}
		out.Observation.Effect = domain.EffectAbsent
	case *r.Progress_Unknown:
	default:
		return out, executor.ErrEvidence
	}
	if evidence != nil {
		if bridge.ValidateBillEffect(evidence, w.Bill) != nil {
			return out, executor.ErrEvidence
		}
		d := evidence.GetBill()
		out.Matches = domain.Known(d.GetConfigurationMatches())
		out.Iterations = d.GetIterations()
		out.OutputComplete = d.GetOutputComplete()
		out.OutputObserved = d.GetOutputObserved()
		out.OutputCount = len(d.Outputs)

	}
	out.ObservedAt = b.clock.Now()
	return out, nil
}
