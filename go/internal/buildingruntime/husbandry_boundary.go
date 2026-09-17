package buildingruntime

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

func husbandryMethodWire(method domain.HusbandryMethod) bridge.HusbandryMethod {
	switch method {
	case domain.HusbandryTrain:
		return bridge.HusbandryMethodTrain
	case domain.HusbandrySlaughter:
		return bridge.HusbandryMethodSlaughter
	case domain.HusbandryTame:
		return bridge.HusbandryMethodTame
	case domain.HusbandryRelease:
		return bridge.HusbandryMethodRelease
	default:
		return bridge.HusbandryMethodUnspecified
	}
}

// HusbandryNative reuses bridge.ReadHusbandryTarget for the animal's fresh
// settings/census CAS tokens and eligibility facts; no dedicated candidate
// search is needed to dispatch one already-selected animal/method pair, only
// to refresh CAS tokens immediately before dispatch (see RecoveryServiceNative).
type HusbandryNative interface {
	ReadHusbandryTarget(context.Context, *c.Identity, string, string, bool) (bridge.HusbandryTarget, bridge.Result, error)
	PreviewHusbandry(context.Context, *c.Identity, string, string, string, bridge.HusbandryMethod, string) (*o.PreviewReply, bridge.Result, error)
	LookupHusbandry(context.Context, bridge.HusbandryAttempt) (*r.LookupReply, bridge.Result, error)
	ObserveHusbandryProgress(context.Context, bridge.HusbandryAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type HusbandryWriter interface {
	ApplyHusbandry(context.Context, *a.WritePrecondition, string, string, string, bridge.HusbandryMethod, string) (*o.ExecuteReply, bridge.Result, error)
}
type HusbandryCapabilities struct {
	Native HusbandryNative
	Writer HusbandryWriter
}
type HusbandryBoundary struct {
	native  HusbandryNative
	writer  HusbandryWriter
	leases  boundary.LeaseSource
	clock   executor.Clock
	session string
}

func NewHusbandryBoundary(native HusbandryNative, writer HusbandryWriter, leases boundary.LeaseSource, clock executor.Clock, session string) (*HusbandryBoundary, error) {
	if native == nil || writer == nil || leases == nil || clock == nil || !boundary.ValidID(session) {
		return nil, errors.New("invalid husbandry boundary dependencies")
	}
	return &HusbandryBoundary{native, writer, leases, clock, session}, nil
}

func (b *HusbandryBoundary) InspectHusbandry(ctx context.Context, target executor.Target) (executor.HusbandryInspection, error) {
	out := executor.HusbandryInspection{StartedAt: b.clock.Now()}
	husbandry, ok := target.Action.Husbandry()
	if !ok {
		return out, executor.ErrEvidence
	}
	read, _, err := b.native.ReadHusbandryTarget(ctx, boundary.Identity(target.Snapshot), string(husbandry.Animal()), husbandry.TrainableDef(), husbandry.Method() == domain.HusbandryTame)
	if err != nil {
		return out, err
	}
	if read.Animal != string(husbandry.Animal()) {
		return out, executor.ErrEvidence
	}
	observedContext, err := boundary.Context(read.Context, target.Snapshot)
	if err != nil {
		return out, err
	}
	preview, _, err := b.native.PreviewHusbandry(ctx, boundary.Identity(observedContext), string(husbandry.Animal()), read.SettingsToken, read.CensusToken, husbandryMethodWire(husbandry.Method()), husbandry.TrainableDef())
	if err != nil {
		return out, err
	}
	evaluated := preview.GetEvaluated()
	if evaluated == nil {
		return out, executor.ErrHeld
	}
	if _, err = boundary.Context(evaluated.Context, observedContext); err != nil {
		return out, err
	}
	effect := evaluated.GetProjected().GetAnimal()
	if evaluated.Context.GetTick() < read.Context.GetTick() || evaluated.Accepted == nil || effect == nil || effect.Animal.GetEntityId() != string(husbandry.Animal()) {
		return out, executor.ErrEvidence
	}
	facts := policy.HusbandryFacts{Snapshot: observedContext, PawnTick: domain.Tick(read.Context.GetTick()), PreviewTick: domain.Tick(evaluated.Context.GetTick()), CensusToken: read.CensusToken, NativeCanTry: domain.Known(evaluated.GetAccepted())}
	facts.Animal = policy.HusbandryAnimalFacts{Animal: husbandry.Animal(), SnapshotToken: read.SettingsToken}
	if read.DeadKnown {
		facts.Animal.Dead = domain.Known(read.Dead)
	}
	if read.CanTrainKnown {
		facts.Animal.CanTrain = domain.Known(read.CanTrain)
	}
	if read.LearnedKnown {
		facts.Animal.Learned = domain.Known(read.Learned)
	}
	if read.SafeToSlaughterKnown {
		facts.Animal.SafeToSlaughter = domain.Known(read.SafeToSlaughter)
	}
	if read.TameableKnown {
		facts.Animal.Tameable = domain.Known(read.Tameable)
	}
	if read.SafeToReleaseKnown {
		facts.Animal.SafeToRelease = domain.Known(read.SafeToRelease)
	}
	out.Facts, out.ObservedAt = facts, b.clock.Now()
	return out, ctx.Err()
}

func (b *HusbandryBoundary) attempt(dispatch executor.HusbandryDispatch) (bridge.HusbandryAttempt, error) {
	p, admission := dispatch.Attempt, dispatch.Admission
	husbandry, ok := p.Action.Husbandry()
	if !ok || p.Attempt == 0 || p.Tick < 0 || admission.Snapshot != p.Snapshot || admission.Animal != husbandry.Animal() || admission.Method != husbandry.Method() || admission.TrainableDef != husbandry.TrainableDef() || admission.Tick > p.Tick || !boundary.ValidID(admission.AnimalSnapshotToken) || !boundary.ValidID(admission.CensusToken) {
		return bridge.HusbandryAttempt{}, executor.ErrEvidence
	}
	return bridge.HusbandryAttempt{
		Identity:            boundary.Identity(p.Snapshot),
		Attempt:             &c.AttemptKey{ControllerSessionId: proto.String(b.session), ActionId: proto.String(string(p.Action.ID())), AttemptId: proto.Uint64(uint64(p.Attempt))},
		Generation:          uint64(p.Snapshot.Native),
		Animal:              string(husbandry.Animal()),
		AnimalToken:         admission.AnimalSnapshotToken,
		ExpectedCensusToken: admission.CensusToken,
		Method:              husbandryMethodWire(husbandry.Method()),
		TrainableDef:        husbandry.TrainableDef(),
	}, nil
}

func (b *HusbandryBoundary) WriteHusbandry(ctx context.Context, dispatch executor.HusbandryDispatch) (executor.Receipt, error) {
	p := dispatch.Attempt
	out := executor.Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptUnknown}
	attempt, err := b.attempt(dispatch)
	if err != nil {
		return out, err
	}
	lease, err := b.leases.Lease(p.Snapshot)
	if err != nil {
		return out, err
	}
	if !boundary.ValidID(lease) {
		return out, executor.ErrAuthority
	}
	if err = ctx.Err(); err != nil {
		return out, err
	}
	pre := &a.WritePrecondition{Identity: attempt.Identity, Attempt: attempt.Attempt, ExpectedGeneration: proto.Uint64(attempt.Generation)}
	reply, _, err := b.writer.ApplyHusbandry(ctx, pre, attempt.Animal, attempt.AnimalToken, attempt.ExpectedCensusToken, attempt.Method, attempt.TrainableDef)
	var refused *bridge.NativeFailure
	if errors.As(err, &refused) && refused.Value != nil && refused.Value.GetCode() != c.FailureCode_FAILURE_CODE_ATTEMPT_CONFLICT {
		out.Kind = domain.ReceiptRefused
		return out, nil
	}
	if err != nil {
		return out, err
	}
	receipt := reply.GetReceipt()
	if err = b.checkReceipt(receipt, dispatch); err != nil {
		return out, err
	}
	switch receipt.Outcome.(type) {
	case *r.Receipt_Applied:
		out.Kind = domain.ReceiptAccepted
	case *r.Receipt_Uncertain:
	default:
		return out, executor.ErrEvidence
	}
	return out, ctx.Err()
}

func (b *HusbandryBoundary) checkReceipt(receipt *r.Receipt, dispatch executor.HusbandryDispatch) error {
	p := dispatch.Attempt
	if err := boundary.Admission(receipt, p, b.session); err != nil {
		return err
	}
	if receipt.AdmittedContext.GetTick() < int64(dispatch.Admission.Tick) {
		return executor.ErrEvidence
	}
	return nil
}

func (b *HusbandryBoundary) ObserveHusbandry(ctx context.Context, dispatch executor.HusbandryDispatch, current domain.GenerationSnapshot) (executor.HusbandryEvidence, error) {
	out := executor.HusbandryEvidence{StartedAt: b.clock.Now(), Animal: dispatch.Admission.Animal}
	attempt, err := b.attempt(dispatch)
	if err != nil {
		return out, err
	}
	p := dispatch.Attempt
	reply, _, err := b.native.LookupHusbandry(ctx, attempt)
	if err != nil {
		return out, err
	}
	switch v := reply.Outcome.(type) {
	case *r.LookupReply_Unknown:
		if _, err = boundary.Context(v.Unknown.GetContext(), current); err != nil {
			return out, err
		}
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Tick: domain.Tick(v.Unknown.GetContext().GetTick()), Effect: domain.EffectUnknown, Causality: domain.AfterDispatch}
		out.ObservedAt = b.clock.Now()
		return out, ctx.Err()
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, attempt.Attempt) {
			return out, executor.ErrEvidence
		}
		if err = boundary.Admission(&r.Receipt{Attempt: v.InFlight.Attempt, AdmittedContext: v.InFlight.AdmittedContext}, p, b.session); err != nil {
			return out, err
		}
	case *r.LookupReply_Receipt:
		if err = b.checkReceipt(v.Receipt, dispatch); err != nil {
			return out, err
		}
	default:
		return out, executor.ErrEvidence
	}
	progress, _, err := b.native.ObserveHusbandryProgress(ctx, attempt, nil)
	if err != nil {
		return out, err
	}
	prog := progress.GetProgress()
	if prog == nil {
		return out, executor.ErrHeld
	}
	if !proto.Equal(prog.Attempt, attempt.Attempt) {
		return out, executor.ErrEvidence
	}
	tickCtx, err := boundary.Context(prog.Context, current)
	if err != nil {
		return out, err
	}
	tick := domain.Tick(prog.Context.GetTick())
	out.ObservedAt = b.clock.Now()
	switch outcome := prog.Effect.(type) {
	case *r.Progress_Unknown:
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: tickCtx, Tick: tick, Effect: domain.EffectUnknown, Causality: domain.AfterDispatch}
		return out, ctx.Err()
	case *r.Progress_Completed:
		if !prog.GetCompleteInspection() || outcome.Completed == nil {
			return out, executor.ErrHeld
		}
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: tickCtx, Tick: tick, Effect: domain.EffectCompleted, Causality: domain.AfterDispatch}
		out.Complete = true
		return out, ctx.Err()
	case *r.Progress_Unsuccessful:
		if !prog.GetCompleteInspection() || outcome.Unsuccessful == nil {
			return out, executor.ErrHeld
		}
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: tickCtx, Tick: tick, Effect: domain.EffectUnsuccessful, UnsuccessfulReason: domain.NativeFailure, Causality: domain.AfterDispatch}
		out.Complete = true
		return out, ctx.Err()
	default:
		return out, executor.ErrEvidence
	}
}

var _ executor.HusbandryBoundary = (*HusbandryBoundary)(nil)
