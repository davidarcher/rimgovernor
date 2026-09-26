package buildingruntime

import (
	"context"
	"errors"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"

	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

type DeconstructionNative interface {
	ReadClearanceTargets(context.Context, *c.Identity) (*n.ClearanceTargetsReply, bridge.Result, error)
	ReadAncientShrines(context.Context, *c.Identity) (*n.AncientShrinesReply, bridge.Result, error)
	ReadColonyFacts(context.Context, *c.Identity, bool, []string) (*n.ColonyFactsReply, bridge.Result, error)
	ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
	LookupDeconstruction(context.Context, bridge.DeconstructionAttempt) (*r.LookupReply, bridge.Result, error)
	ObserveDeconstructionProgress(context.Context, bridge.DeconstructionAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type DeconstructionWriter interface {
	ApplyDeconstruction(context.Context, *a.WritePrecondition, string) (*o.ExecuteReply, bridge.Result, error)
	ReleaseDeconstructions(context.Context, *a.WritePrecondition) (*o.ExecuteReply, bridge.Result, error)
}
type DeconstructionCapabilities struct {
	Native DeconstructionNative
	Writer DeconstructionWriter
}
type DeconstructionBoundary struct {
	native  DeconstructionNative
	writer  DeconstructionWriter
	leases  boundary.LeaseSource
	clock   executor.Clock
	session string
}

func NewDeconstructionBoundary(native DeconstructionNative, writer DeconstructionWriter, leases boundary.LeaseSource, clock executor.Clock, session string) (*DeconstructionBoundary, error) {
	if native == nil || writer == nil || leases == nil || clock == nil || !boundary.ValidID(session) {
		return nil, errors.New("invalid deconstruction boundary dependencies")
	}
	return &DeconstructionBoundary{native, writer, leases, clock, session}, nil
}

func (b *DeconstructionBoundary) InspectDeconstruction(ctx context.Context, target executor.Target) (executor.DeconstructionInspection, error) {
	out := executor.DeconstructionInspection{StartedAt: b.clock.Now()}
	value, ok := target.Action.Deconstruction()
	if !ok {
		return out, executor.ErrEvidence
	}
	if value.Breach() {
		return b.inspectBreach(ctx, target, value, out)
	}
	if value.Drill() {
		return b.inspectDrill(ctx, target, value, out)
	}
	reply, _, err := b.native.ReadClearanceTargets(ctx, boundary.Identity(target.Snapshot))
	if err != nil {
		return out, err
	}
	observed := reply.GetObserved()
	if err = bridge.ValidateClearanceTargets(observed, boundary.Identity(target.Snapshot)); err != nil {
		return out, err
	}
	current, err := boundary.Context(observed.Context, target.Snapshot)
	if err != nil {
		return out, err
	}
	out.Current, out.Tick, out.Target = current, domain.Tick(observed.Context.GetTick()), value
	for _, row := range observed.Targets {
		if row.GetEntityId() != value.Target() {
			continue
		}
		if row.GetDefName() != value.Definition() || row.Occupied.Minimum.GetX() != value.Cell().X || row.Occupied.Minimum.GetZ() != value.Cell().Z {
			return out, executor.ErrDeconstructionAbsent
		}
		candidate := policy.ClearanceTarget{EntityID: row.GetEntityId(), InHome: row.GetInHome(), Deconstructible: row.GetDeconstructible(), AncientDanger: row.GetAncientDanger(), RoofBlocker: row.GetRoofBlocker(), Designated: row.GetDesignated(), Faction: row.GetFaction()}
		if row.Class == n.ClearanceClass_CLEARANCE_CLASS_ANCIENT_CASKET {
			candidate.Class = "ancient_casket"
		}
		candidate.SalvageSelected = !candidate.InHome && row.Salvage != nil && row.Salvage.Safe
		// A shelter ring clears the ruin on its own site (#709), which
		// the Home area need not cover yet.
		if strings.HasPrefix(string(target.Snapshot.Plan), shellClearPlanPrefix) {
			candidate.InHome = true
		}
		out.Eligible = policy.ClearanceHoldReason(candidate) == ""
		out.Accepted = out.Eligible
		out.Refusals = deconstructionRefusals(target.Action.ID(), candidate, row.Salvage)
		emergency, _, err := b.native.ReadEmergency(ctx, boundary.Identity(current))
		if err != nil {
			return out, err
		}
		if _, err = boundary.Context(emergency.Context, current); err != nil {
			return out, err
		}
		if bridge.FactEmergency.Outrun(emergency.Context.GetTick(), observed.Context.GetTick()) {
			return out, executor.ErrHeld
		}
		out.Emergency, err = policy.NewEmergencySnapshot(current, out.Tick, emergency.Facts)
		if err != nil {
			return out, err
		}
		out.ObservedAt = b.clock.Now()
		return out, ctx.Err()
	}
	return out, executor.ErrDeconstructionAbsent
}

// deconstructionRefusals names why the census refuses the target now (#525):
// a roof the removal would drop, a remote ruin with no safe route or no
// accepting storage for its yield, or a structure the clearance census will
// not have removed at all. A remote ruin's storage hold reads the native
// salvage row's per-yield headroom exactly as the review's filter does.
func deconstructionRefusals(action domain.ActionID, candidate policy.ClearanceTarget, salvage *n.SalvageEvidence) []policy.Refusal {
	var out []policy.Refusal
	refuse := func(reason policy.Reason) { out = append(out, policy.Refusal{Action: action, Reason: reason}) }
	switch policy.ClearanceHoldReason(candidate) {
	case "":
		return nil
	case "roof_blocker":
		refuse(policy.RoofSupportRisk)
	case "outside_home":
		if salvage == nil || !salvage.GetSafe() {
			refuse(policy.UnsafeRoute)
		}
		headroom := false
		for _, y := range salvage.GetYields() {
			headroom = headroom || y.GetStorageHeadroom() > 0
		}
		if salvage != nil && len(salvage.GetYields()) > 0 && !headroom {
			refuse(policy.StorageMissing)
		}
		if len(out) == 0 {
			refuse(policy.StructureIneligible)
		}
	default:
		refuse(policy.StructureIneligible)
	}
	return out
}

// inspectBreach guards a shrine breach (#458) on the shrine census instead
// of the Home clearance census: the wall is eligible while it is still a
// breach wall of a sealed shrine (deconstructible, unfogged, no roof
// blocker, an outside cell to stand on). A wall gone from that list is
// absent: the shrine opened by other means or the wall fell. The emergency
// hold is the same one an ordinary deconstruction takes.
func (b *DeconstructionBoundary) inspectBreach(ctx context.Context, target executor.Target, value domain.Deconstruction, out executor.DeconstructionInspection) (executor.DeconstructionInspection, error) {
	identity := boundary.Identity(target.Snapshot)
	reply, _, err := b.native.ReadAncientShrines(ctx, identity)
	if err != nil {
		return out, err
	}
	observed := reply.GetObserved()
	if err = bridge.ValidateAncientShrines(observed, identity); err != nil {
		return out, err
	}
	current, err := boundary.Context(observed.Context, target.Snapshot)
	if err != nil {
		return out, err
	}
	out.Current, out.Tick, out.Target = current, domain.Tick(observed.Context.GetTick()), value
	for _, shrine := range observed.Shrines {
		for _, wall := range shrine.BreachWalls {
			if wall.GetEntityId() != value.Target() {
				continue
			}
			if wall.DefName != nil && wall.GetDefName() != value.Definition() || wall.Cell.GetX() != value.Cell().X || wall.Cell.GetZ() != value.Cell().Z {
				return out, executor.ErrDeconstructionAbsent
			}
			out.Eligible = shrine.GetSealed()
			out.Accepted = out.Eligible
			emergency, _, err := b.native.ReadEmergency(ctx, boundary.Identity(current))
			if err != nil {
				return out, err
			}
			if _, err = boundary.Context(emergency.Context, current); err != nil {
				return out, err
			}
			if bridge.FactEmergency.Outrun(emergency.Context.GetTick(), observed.Context.GetTick()) {
				return out, executor.ErrHeld
			}
			out.Emergency, err = policy.NewEmergencySnapshot(current, out.Tick, emergency.Facts)
			if err != nil {
				return out, err
			}
			out.ObservedAt = b.clock.Now()
			return out, ctx.Err()
		}
	}
	return out, executor.ErrDeconstructionAbsent
}

// inspectDrill guards an exhausted-drill removal (#538) on the typed drill
// census in colony facts instead of the Home clearance census, which excludes
// player buildings. The exact drill (id, definition, cell) must still be
// present and depleted at dispatch: a seam that still reads a deposit or an
// unknown census never dispatches. A drill gone from the census is absent.
func (b *DeconstructionBoundary) inspectDrill(ctx context.Context, target executor.Target, value domain.Deconstruction, out executor.DeconstructionInspection) (executor.DeconstructionInspection, error) {
	reply, _, err := b.native.ReadColonyFacts(ctx, boundary.Identity(target.Snapshot), false, nil)
	if err != nil {
		return out, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return out, executor.ErrHeld
	}
	current, err := boundary.Context(observed.Context, target.Snapshot)
	if err != nil {
		return out, err
	}
	out.Current, out.Tick, out.Target = current, domain.Tick(observed.Context.GetTick()), value
	census := observed.GetDeepResources().GetObserved()
	if census == nil {
		return out, executor.ErrHeld
	}
	for _, drill := range census.Drills {
		if drill.GetBuildingId() != value.Target() {
			continue
		}
		if drill.GetDefName() != value.Definition() || drill.Position.GetX() != value.Cell().X || drill.Position.GetZ() != value.Cell().Z {
			return out, executor.ErrDeconstructionAbsent
		}
		out.Eligible = drill.GetDepleted()
		out.Accepted = out.Eligible
		emergency, _, err := b.native.ReadEmergency(ctx, boundary.Identity(current))
		if err != nil {
			return out, err
		}
		if _, err = boundary.Context(emergency.Context, current); err != nil {
			return out, err
		}
		if bridge.FactEmergency.Outrun(emergency.Context.GetTick(), observed.Context.GetTick()) {
			return out, executor.ErrHeld
		}
		out.Emergency, err = policy.NewEmergencySnapshot(current, out.Tick, emergency.Facts)
		if err != nil {
			return out, err
		}
		out.ObservedAt = b.clock.Now()
		return out, ctx.Err()
	}
	return out, executor.ErrDeconstructionAbsent
}

func (b *DeconstructionBoundary) attempt(p executor.Placement) (bridge.DeconstructionAttempt, error) {
	value, ok := p.Action.Deconstruction()
	if !ok || p.Attempt == 0 || p.Tick < 0 {
		return bridge.DeconstructionAttempt{}, executor.ErrEvidence
	}
	return bridge.DeconstructionAttempt{Identity: boundary.Identity(p.Snapshot), Attempt: &c.AttemptKey{ControllerSessionId: proto.String(b.session), ActionId: proto.String(string(p.Action.ID())), AttemptId: proto.Uint64(uint64(p.Attempt))}, Generation: uint64(p.Snapshot.Native), Target: value.Target()}, nil
}

func (b *DeconstructionBoundary) DesignateDeconstruction(ctx context.Context, dispatch executor.DeconstructionDispatch) (executor.Receipt, error) {
	p := dispatch.Attempt
	out := executor.Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptUnknown}
	if !dispatch.Eligible {
		return out, executor.ErrEvidence
	}
	attempt, err := b.attempt(dispatch.Attempt)
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
	reply, _, err := b.writer.ApplyDeconstruction(ctx, pre, attempt.Target)
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

func (b *DeconstructionBoundary) checkReceipt(receipt *r.Receipt, dispatch executor.DeconstructionDispatch) error {
	p := dispatch.Attempt
	if err := boundary.Admission(receipt, p, b.session); err != nil {
		return err
	}
	if receipt.AdmittedContext.GetTick() < int64(dispatch.Attempt.Tick) {
		return executor.ErrEvidence
	}
	return nil
}

func (b *DeconstructionBoundary) ObserveDeconstruction(ctx context.Context, placement executor.Placement, current domain.GenerationSnapshot) (executor.DeconstructionEvidence, error) {
	dispatch := executor.DeconstructionDispatch{Attempt: placement, Eligible: true}
	value, _ := placement.Action.Deconstruction()
	out := executor.DeconstructionEvidence{StartedAt: b.clock.Now(), Target: value}
	attempt, err := b.attempt(dispatch.Attempt)
	if err != nil {
		return out, err
	}
	reply, _, err := b.native.LookupDeconstruction(ctx, attempt)
	if err != nil {
		return out, err
	}
	p := dispatch.Attempt
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
		// The attempt key pins the receipt to this dispatch. The placement
		// tick is the last observation's, not the dispatch's, so it is no
		// floor for the admitted tick: after one pending observation it
		// refused the receipt forever (#678).
		if err = boundary.Admission(v.Receipt, p, b.session); err != nil {
			return out, err
		}
	default:
		return out, executor.ErrEvidence
	}
	progress, _, err := b.native.ObserveDeconstructionProgress(ctx, attempt, nil)
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
	case *r.Progress_Pending:
		if !prog.GetCompleteInspection() {
			return out, executor.ErrHeld
		}
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: tickCtx, Tick: tick, Effect: domain.EffectPending, Causality: domain.AfterDispatch}
		out.Demolished = domain.Known(false)
		return out, ctx.Err()
	case *r.Progress_Completed:
		if !outcome.Completed.Evidence.GetDeconstruct().GetDemolitionObserved() || outcome.Completed.Evidence.GetDeconstruct().GetTargetId() != attempt.Target {
			return out, executor.ErrEvidence
		}
		out.Demolished = domain.Known(true)
		if !prog.GetCompleteInspection() {
			return out, executor.ErrHeld
		}
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: tickCtx, Tick: tick, Effect: domain.EffectCompleted, Causality: domain.AfterDispatch}
		out.Complete = true
		return out, ctx.Err()
	case *r.Progress_Absent:
		if !prog.GetCompleteInspection() {
			return out, executor.ErrHeld
		}
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: tickCtx, Tick: tick, Effect: domain.EffectAbsent, Causality: domain.AfterDispatch}
		out.Complete = true
		return out, ctx.Err()
	case *r.Progress_Unsuccessful:
		out.Demolished = domain.Known(false)
		if !prog.GetCompleteInspection() {
			return out, executor.ErrHeld
		}
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: tickCtx, Tick: tick, Effect: domain.EffectUnsuccessful, UnsuccessfulReason: domain.OutcomeNotAchieved, Causality: domain.AfterDispatch}
		out.Complete = true
		return out, ctx.Err()
	default:
		_ = outcome
		return out, executor.ErrEvidence
	}
}

var _ executor.DeconstructionBoundary = (*DeconstructionBoundary)(nil)
