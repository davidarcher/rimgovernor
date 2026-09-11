// Package buildingruntime adapts typed native evidence to the deterministic executor.
package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"strings"
	"unicode/utf8"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/placementpb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

type Native interface {
	PreviewBuilding(context.Context, domain.Action, domain.GenerationSnapshot) (bridge.BuildingPreview, bridge.Result, error)
	ReadMapBounds(context.Context, *c.Identity, domain.Cell) (bridge.MapBounds, bridge.Result, error)
	LookupBuildingAttempt(context.Context, *c.Identity, *c.AttemptKey, uint64, *p.PlacementCandidate) (*r.LookupReply, bridge.Result, error)
	ObserveBuildingProgress(context.Context, *r.Receipt, *p.PlacementCandidate) (*r.ProgressReply, bridge.Result, error)
}
type BuildingWriter interface {
	PlaceBuilding(context.Context, *a.WritePrecondition, *p.PlacementCandidate) (*o.ExecuteReply, bridge.Result, error)
}
type LeaseSource interface {
	Lease(domain.GenerationSnapshot) (string, error)
}
type HoldsSource interface {
	Holds(context.Context, domain.GenerationSnapshot) ([]policy.Reservation, error)
}
type Boundary struct {
	native  Native
	writer  BuildingWriter
	leases  LeaseSource
	holds   HoldsSource
	clock   executor.Clock
	session string
	rules   []policy.ResourceRule
}

var _ executor.Boundary = (*Boundary)(nil)

func NewBoundary(native Native, writer BuildingWriter, leases LeaseSource, holds HoldsSource, clock executor.Clock, controllerSessionID string, rules []policy.ResourceRule) (*Boundary, error) {
	if native == nil || writer == nil || leases == nil || holds == nil || clock == nil || !boundaryID(controllerSessionID) {
		return nil, errors.New("invalid building boundary dependencies")
	}
	return &Boundary{native, writer, leases, holds, clock, controllerSessionID, append([]policy.ResourceRule(nil), rules...)}, nil
}
func (b *Boundary) Inspect(ctx context.Context, target executor.Target) (executor.Inspection, error) {
	out := executor.Inspection{StartedAt: b.clock.Now()}
	candidate, err := boundaryCandidate(target.Action, target.Snapshot)
	if err != nil {
		return out, err
	}
	bounds, _, err := b.native.ReadMapBounds(ctx, boundaryIdentity(target.Snapshot), domain.Cell{X: candidate.GetX(), Z: candidate.GetZ()})
	if err != nil {
		return out, err
	}
	current, err := boundaryContext(bounds.Context, target.Snapshot)
	if err != nil {
		return out, err
	}
	if bounds.Bounds.Width <= 0 || bounds.Bounds.Height <= 0 {
		return out, executor.ErrEvidence
	}
	preview, _, err := b.native.PreviewBuilding(ctx, target.Action, target.Snapshot)
	if err != nil {
		return out, err
	}
	if !preview.Preview.Snapshot.Matches(current) || !preview.Stock.Snapshot.Matches(current) || preview.Preview.Action != target.Action || preview.Preview.Tick < domain.Tick(bounds.Context.GetTick()) || preview.Stock.Tick != preview.Preview.Tick {
		return out, executor.ErrEvidence
	}
	held, err := b.holds.Holds(ctx, current)
	if err != nil {
		return out, err
	}
	if err = ctx.Err(); err != nil {
		return out, err
	}
	out.Current = current
	out.Tick = preview.Preview.Tick
	out.Bounds = domain.Known(bounds.Bounds)
	out.Preview = preview.Preview
	out.Stock = preview.Stock
	out.Held = held
	out.ExternalHoldsComplete = true
	out.Rules = append([]policy.ResourceRule(nil), b.rules...)
	out.ObservedAt = b.clock.Now()
	return out, nil
}
func (b *Boundary) Place(ctx context.Context, placement executor.Placement) (executor.Receipt, error) {
	out := executor.Receipt{Action: placement.Action.ID(), Attempt: placement.Attempt, Snapshot: placement.Snapshot, Kind: domain.ReceiptUnknown}
	candidate, err := boundaryPlacement(placement)
	if err != nil {
		return out, err
	}
	if err = ctx.Err(); err != nil {
		return out, err
	}
	lease, err := b.leases.Lease(placement.Snapshot)
	if err != nil {
		return out, err
	}
	if !boundaryID(lease) {
		return out, executor.ErrAuthority
	}
	pre := &a.WritePrecondition{Identity: boundaryIdentity(placement.Snapshot), ExpectedGeneration: proto.Uint64(uint64(placement.Snapshot.Native)), LeaseId: proto.String(lease), Attempt: b.attempt(placement)}
	if err = ctx.Err(); err != nil {
		return out, err
	}
	reply, _, err := b.writer.PlaceBuilding(ctx, pre, candidate)
	var refused *bridge.NativeFailure
	if errors.As(err, &refused) {
		out.Kind = domain.ReceiptRefused
		return out, nil
	}
	if err != nil {
		return out, err
	}
	admitted := reply.GetReceipt()
	if err = boundaryAdmission(admitted, placement, b.session); err != nil {
		return out, err
	}
	if admitted.AdmittedContext.GetTick() < int64(placement.Tick) {
		return out, executor.ErrEvidence
	}
	switch admitted.Outcome.(type) {
	case *r.Receipt_Applied:
		out.Kind = domain.ReceiptAccepted
	case *r.Receipt_NoChange:
		out.Kind = domain.ReceiptAccepted
	case *r.Receipt_Uncertain:
		out.Kind = domain.ReceiptUnknown
	default:
		return out, executor.ErrEvidence
	}
	return out, nil
}
func (b *Boundary) Observe(ctx context.Context, placement executor.Placement, current domain.GenerationSnapshot) (executor.Evidence, error) {
	out := executor.Evidence{StartedAt: b.clock.Now(), Observation: domain.Observation{Action: placement.Action.ID(), Attempt: placement.Attempt, Snapshot: current, Effect: domain.EffectUnknown}}
	candidate, err := boundaryPlacement(placement)
	if err != nil {
		return out, err
	}
	if current.Validate() != nil || current.Native == 0 || !boundaryWorld(placement.Snapshot, current) {
		return out, executor.ErrAuthority
	}
	lookup, _, err := b.native.LookupBuildingAttempt(ctx, boundaryIdentity(placement.Snapshot), b.attempt(placement), uint64(placement.Snapshot.Native), candidate)
	if err != nil {
		return out, err
	}
	admitted := lookup.GetReceipt()
	if admitted == nil {
		return out, executor.ErrHeld
	} // Missing ledger or in-flight is never no-effect proof.
	if err = boundaryAdmission(admitted, placement, b.session); err != nil {
		return out, err
	}
	reply, _, err := b.native.ObserveBuildingProgress(ctx, admitted, candidate)
	if err != nil {
		return out, err
	}
	progress := reply.GetProgress()
	if progress == nil || !proto.Equal(progress.Attempt, b.attempt(placement)) {
		return out, executor.ErrEvidence
	}
	observed, err := boundaryContext(progress.Context, current)
	if err != nil {
		return out, err
	}
	if progress.Context.GetTick() < int64(placement.Tick) || progress.Context.GetTick() < admitted.AdmittedContext.GetTick() {
		return out, executor.ErrEvidence
	}
	out.Observation.Snapshot = observed
	out.Observation.Tick = domain.Tick(progress.Context.GetTick())
	out.Observation.Causality = domain.AfterDispatch
	out.Complete = progress.GetCompleteInspection()
	switch v := progress.Effect.(type) {
	case *r.Progress_Unknown:
		out.Observation.Effect = domain.EffectUnknown
	case *r.Progress_Pending:
		out.Observation.Effect = domain.EffectPending
	case *r.Progress_Absent:
		if !out.Complete || v.Absent == nil || !boundaryID(v.Absent.GetInspectionToken()) {
			return out, executor.ErrEvidence
		}
		out.Observation.Effect = domain.EffectAbsent
	case *r.Progress_Unsuccessful:
		if !out.Complete || v.Unsuccessful == nil {
			return out, executor.ErrEvidence
		}
		reason := map[r.UnsuccessfulReason]domain.UnsuccessfulReason{r.UnsuccessfulReason_UNSUCCESSFUL_REASON_NATIVE_FAILURE: domain.NativeFailure, r.UnsuccessfulReason_UNSUCCESSFUL_REASON_CANCELLED: domain.NativeCancelled, r.UnsuccessfulReason_UNSUCCESSFUL_REASON_INTERRUPTED: domain.NativeInterrupted, r.UnsuccessfulReason_UNSUCCESSFUL_REASON_EXPIRED: domain.NativeExpired, r.UnsuccessfulReason_UNSUCCESSFUL_REASON_TARGET_DEAD: domain.TargetDead, r.UnsuccessfulReason_UNSUCCESSFUL_REASON_OUTCOME_NOT_ACHIEVED: domain.OutcomeNotAchieved}[v.Unsuccessful.GetReason()]
		if reason == "" {
			return out, executor.ErrEvidence
		}
		out.Observation.Effect = domain.EffectUnsuccessful
		out.Observation.UnsuccessfulReason = reason
	case *r.Progress_Completed:
		effect := v.Completed.GetEvidence().GetConstruction()
		if !out.Complete || effect == nil || effect.Stage == nil || effect.GetStage() != r.ConstructionStage_CONSTRUCTION_STAGE_BUILDING || effect.Present == nil || !effect.GetPresent() || effect.Failed == nil || effect.GetFailed() || effect.Cell == nil || effect.DefName == nil || effect.Stuff == nil || effect.Rotation == nil || !boundaryID(effect.GetOriginThingId()) || !boundaryID(effect.GetCurrentThingId()) {
			return out, executor.ErrEvidence
		}
		origin := boundaryOrigin(admitted)
		if origin != "" && effect.GetOriginThingId() != origin {
			return out, executor.ErrEvidence
		}
		building, err := domain.NewBuilding(effect.GetDefName(), domain.Cell{X: effect.Cell.GetX(), Z: effect.Cell.GetZ()}, boundaryRotation(effect.GetRotation()), effect.GetStuff())
		wanted, _ := placement.Action.Building()
		if err != nil || building != wanted {
			return out, executor.ErrEvidence
		}
		out.Built = domain.Known(building)
		out.Observation.Effect = domain.EffectCompleted
	default:
		return out, executor.ErrEvidence
	}
	if err = ctx.Err(); err != nil {
		return out, err
	}
	out.ObservedAt = b.clock.Now()
	return out, nil
}
func boundaryID(s string) bool {
	return utf8.ValidString(s) && strings.TrimSpace(s) != "" && len(s) <= 256 && !strings.ContainsRune(s, 0)
}
func boundaryIdentity(s domain.GenerationSnapshot) *c.Identity {
	return &c.Identity{ColonyId: proto.String(string(s.Colony)), LoadToken: proto.String(string(s.Load)), MapId: proto.Int32(int32(s.Map))}
}
func (b *Boundary) attempt(p executor.Placement) *c.AttemptKey {
	return &c.AttemptKey{ControllerSessionId: proto.String(b.session), ActionId: proto.String(string(p.Action.ID())), AttemptId: proto.Uint64(uint64(p.Attempt))}
}
func boundaryWorld(a, b domain.GenerationSnapshot) bool {
	return a.Colony == b.Colony && a.Load == b.Load && a.Map == b.Map
}
func boundaryContext(v *c.ObservationContext, expected domain.GenerationSnapshot) (domain.GenerationSnapshot, error) {
	if err := bridge.ValidateContext(v); err != nil {
		return domain.GenerationSnapshot{}, err
	}
	if v.NativeGeneration == nil || v.GetNativeGeneration() != uint64(expected.Native) {
		return domain.GenerationSnapshot{}, executor.ErrAuthority
	}
	out := expected
	out.Colony = domain.ColonyID(v.Identity.GetColonyId())
	out.Load = domain.LoadID(v.Identity.GetLoadToken())
	out.Map = domain.MapID(v.Identity.GetMapId())
	out.Native = domain.NativeGeneration(v.GetNativeGeneration())
	if !boundaryWorld(out, expected) {
		return domain.GenerationSnapshot{}, executor.ErrAuthority
	}
	return out, nil
}
func boundaryCandidate(action domain.Action, snapshot domain.GenerationSnapshot) (*p.PlacementCandidate, error) {
	if snapshot.Validate() != nil || snapshot.Native == 0 {
		return nil, executor.ErrAuthority
	}
	value, ok := action.Building()
	if !ok {
		return nil, executor.ErrEvidence
	}
	rotation := map[domain.Rotation]p.Rotation{domain.North: p.Rotation_ROTATION_NORTH, domain.East: p.Rotation_ROTATION_EAST, domain.South: p.Rotation_ROTATION_SOUTH, domain.West: p.Rotation_ROTATION_WEST}[value.Rotation()]
	if rotation == 0 {
		return nil, executor.ErrEvidence
	}
	return &p.PlacementCandidate{DefName: proto.String(value.Definition()), Stuff: proto.String(value.Stuff()), X: proto.Int32(value.Cell().X), Z: proto.Int32(value.Cell().Z), Rotation: rotation.Enum()}, nil
}
func boundaryRotation(v p.Rotation) domain.Rotation {
	return map[p.Rotation]domain.Rotation{p.Rotation_ROTATION_NORTH: domain.North, p.Rotation_ROTATION_EAST: domain.East, p.Rotation_ROTATION_SOUTH: domain.South, p.Rotation_ROTATION_WEST: domain.West}[v]
}
func boundaryPlacement(value executor.Placement) (*p.PlacementCandidate, error) {
	if value.Attempt == 0 || value.Tick < 0 {
		return nil, executor.ErrEvidence
	}
	return boundaryCandidate(value.Action, value.Snapshot)
}
func boundaryAdmission(receipt *r.Receipt, placement executor.Placement, session string) error {
	if receipt == nil || receipt.Attempt == nil || receipt.Attempt.GetControllerSessionId() != session || receipt.Attempt.GetActionId() != string(placement.Action.ID()) || receipt.Attempt.GetAttemptId() != uint64(placement.Attempt) {
		return executor.ErrEvidence
	}
	if _, err := boundaryContext(receipt.AdmittedContext, placement.Snapshot); err != nil {
		return err
	}
	owner := receipt.AuthorizingOwner
	if owner == nil || owner.ControllerSessionId == nil || owner.PlayerDirection == nil || owner.GetControllerSessionId() != session || owner.GetPlayerDirection() != uint64(placement.Snapshot.Direction) {
		return fmt.Errorf("%w: original authorizing owner changed", executor.ErrEvidence)
	}
	return nil
}

func boundaryOrigin(receipt *r.Receipt) string {
	switch value := receipt.Outcome.(type) {
	case *r.Receipt_Applied:
		return value.Applied.GetObserved().GetConstruction().GetOriginThingId()
	case *r.Receipt_NoChange:
		return value.NoChange.GetObserved().GetConstruction().GetOriginThingId()
	case *r.Receipt_Uncertain:
		return value.Uncertain.GetLastObserved().GetConstruction().GetOriginThingId()
	}
	return ""
}
