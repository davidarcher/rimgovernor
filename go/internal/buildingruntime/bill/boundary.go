package bill

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
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
type BillBoundary struct {
	*boundary.Boundary
	bill BillCapabilities
}

func NewBillBoundary(base *boundary.Boundary, bill BillCapabilities) *BillBoundary {
	return &BillBoundary{Boundary: base, bill: bill}
}

func (b *BillBoundary) InspectBill(ctx context.Context, target executor.Target) (executor.BillInspection, error) {
	out := executor.BillInspection{StartedAt: b.Clock.Now()}
	bill, ok := target.Action.ProductionBill()
	if !ok {
		return out, executor.ErrEvidence
	}
	read, _, err := b.bill.Native.ReadBillTarget(ctx, boundary.Identity(target.Snapshot), bill.Bench())
	if err != nil {
		return out, err
	}
	current, err := boundary.Context(read.Context, target.Snapshot)
	if err != nil {
		return out, err
	}
	if read.Token != bill.BeforeToken() {
		return out, executor.ErrHeld
	}
	selected := bill
	preview, _, err := b.bill.Native.PreviewBill(ctx, boundary.Identity(current), selected)
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
	if _, err = boundary.Context(v.Context, current); err != nil {
		return out, err
	}
	emergency, _, err := b.bill.Native.ReadEmergency(ctx, boundary.Identity(current))
	if err != nil {
		return out, err
	}
	if _, err = boundary.Context(emergency.Context, current); err != nil {
		return out, err
	}
	// The three reads need only be ordered, not simultaneous: the bench's before-token already binds the bill to the stack the read listed,
	// so a tick that advanced between them is a running clock, not stale
	// evidence. Demanding one tick held every dispatch until the game
	// paused, starving the Worker under 2500-tick windows (#150); supply
	// and the building family accept the same monotonic order.
	if v.Context.GetTick() < read.Context.GetTick() || emergency.Context.GetTick() < v.Context.GetTick() {
		return out, executor.ErrHeld
	}
	out.Current, out.Tick, out.Bill, out.SnapshotToken, out.Accepted = current, domain.Tick(v.Context.GetTick()), bill, selected.BeforeToken(), true
	out.Emergency, err = policy.NewEmergencySnapshot(current, out.Tick, emergency.Facts)
	out.ObservedAt = b.Clock.Now()
	return out, err
}
func (b *BillBoundary) billAttempt(p executor.Placement) bridge.BillAttempt {
	bill, _ := p.Action.ProductionBill()
	return bridge.BillAttempt{Identity: boundary.Identity(p.Snapshot), Attempt: b.Attempt(p), Generation: uint64(p.Snapshot.Native), Bill: bill}
}
func (b *BillBoundary) AddBill(ctx context.Context, request executor.BillDispatch) (executor.Receipt, error) {
	p := request.Attempt
	bill, ok := p.Action.ProductionBill()
	return b.DispatchWrite(ctx, p,
		func() error {
			if !ok || request.SnapshotToken != bill.BeforeToken() {
				return executor.ErrEvidence
			}
			return nil
		},
		func(pre *a.WritePrecondition) (*op.ExecuteReply, bridge.Result, error) {
			return b.bill.Writer.AddBill(ctx, pre, bill)
		},
	)
}
func (b *BillBoundary) ObserveBill(ctx context.Context, p executor.Placement, current domain.GenerationSnapshot) (executor.BillEvidence, error) {
	out := executor.BillEvidence{StartedAt: b.Clock.Now(), Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Effect: domain.EffectUnknown}}
	if !boundary.World(current, p.Snapshot) {
		return out, executor.ErrAuthority
	}
	w := b.billAttempt(p)
	lookup, _, err := b.bill.Native.LookupBill(ctx, w)
	if err != nil {
		return out, err
	}
	admitted := lookup.GetReceipt()
	if admitted == nil {
		absent, err := boundary.Unadmitted(lookup, p, current)
		if err != nil {
			return out, err
		}
		out.Observation, out.Complete, out.Bill, out.ObservedAt = absent, true, w.Bill, b.Clock.Now()
		return out, nil
	}
	if err = boundary.Admission(admitted, p, b.Session); err != nil {
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
	out.Observation.Snapshot, err = boundary.Context(v.Context, current)
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
		if !out.Complete || effect.Absent == nil || !boundary.ValidID(effect.Absent.GetInspectionToken()) {
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
	out.ObservedAt = b.Clock.Now()
	return out, nil
}
