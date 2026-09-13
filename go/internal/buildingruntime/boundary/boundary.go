// Package boundary holds the native-facing building-placement primitives and
// pawn/receipt fact helpers shared by every action-family boundary
// (acquisition, bill, draft, equip, haul, melee, ranged, rescue, supply,
// tend, work, zone). It has no dependency on buildingruntime session/worker
// orchestration, so those family packages and the orchestration core can
// both depend on it without an import cycle.
package boundary

import (
	"context"
	"errors"
	"fmt"
	"strings"
	"time"
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
	ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
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

// FixedClock is a shared test fixture: a clock stuck at a fixed instant, used
// by every family package's boundary tests to avoid duplicating a fake clock.
type FixedClock struct{}

func (FixedClock) Now() time.Time { return time.Unix(100, 0) }

type Boundary struct {
	Native  Native
	Writer  BuildingWriter
	Leases  LeaseSource
	Holds   HoldsSource
	Clock   executor.Clock
	Session string
	Rules   []policy.ResourceRule
}

var _ executor.Boundary = (*Boundary)(nil)

func NewBoundary(native Native, writer BuildingWriter, leases LeaseSource, holds HoldsSource, clock executor.Clock, controllerSessionID string, rules []policy.ResourceRule) (*Boundary, error) {
	if err := policy.ValidateResourceRules(rules); err != nil {
		return nil, err
	}
	if native == nil || writer == nil || leases == nil || holds == nil || clock == nil || !ValidID(controllerSessionID) {
		return nil, errors.New("invalid building boundary dependencies")
	}
	return &Boundary{native, writer, leases, holds, clock, controllerSessionID, append([]policy.ResourceRule(nil), rules...)}, nil
}
func (b *Boundary) Inspect(ctx context.Context, target executor.Target) (executor.Inspection, error) {
	out := executor.Inspection{StartedAt: b.Clock.Now()}
	candidate, err := Candidate(target.Action, target.Snapshot)
	if err != nil {
		return out, err
	}
	bounds, _, err := b.Native.ReadMapBounds(ctx, Identity(target.Snapshot), domain.Cell{X: candidate.GetX(), Z: candidate.GetZ()})
	if err != nil {
		return out, err
	}
	current, err := Context(bounds.Context, target.Snapshot)
	if err != nil {
		return out, err
	}
	if bounds.Bounds.Width <= 0 || bounds.Bounds.Height <= 0 {
		return out, executor.ErrEvidence
	}
	preview, _, err := b.Native.PreviewBuilding(ctx, target.Action, target.Snapshot)
	if err != nil {
		return out, err
	}
	if !preview.Preview.Snapshot.Matches(current) || !preview.Stock.Snapshot.Matches(current) || preview.Preview.Action != target.Action || preview.Preview.Tick < domain.Tick(bounds.Context.GetTick()) || preview.Stock.Tick != preview.Preview.Tick {
		return out, executor.ErrEvidence
	}
	emergency, _, err := b.Native.ReadEmergency(ctx, Identity(current))
	if err != nil {
		return out, err
	}
	emergencyCurrent, err := Context(emergency.Context, current)
	if err != nil {
		return out, err
	}
	if emergency.Context.GetTick() < int64(preview.Preview.Tick) {
		return out, executor.ErrEvidence
	}
	// Independent live reads may advance ticks. Bind native facts to captured
	// controller authority only after validating their actual world/generation.
	out.Emergency, err = policy.NewEmergencySnapshot(emergencyCurrent, domain.Tick(emergency.Context.GetTick()), emergency.Facts)
	if err != nil {
		return out, err
	}
	held, err := b.Holds.Holds(ctx, current)
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
	out.Rules = append([]policy.ResourceRule(nil), b.Rules...)
	out.ObservedAt = b.Clock.Now()
	return out, nil
}
func (b *Boundary) Place(ctx context.Context, placement executor.Placement) (executor.Receipt, error) {
	out := executor.Receipt{Action: placement.Action.ID(), Attempt: placement.Attempt, Snapshot: placement.Snapshot, Kind: domain.ReceiptUnknown}
	candidate, err := ValidatePlacement(placement)
	if err != nil {
		return out, err
	}
	if err = ctx.Err(); err != nil {
		return out, err
	}
	lease, err := b.Leases.Lease(placement.Snapshot)
	if err != nil {
		return out, err
	}
	if !ValidID(lease) {
		return out, executor.ErrAuthority
	}
	pre := &a.WritePrecondition{Identity: Identity(placement.Snapshot), ExpectedGeneration: proto.Uint64(uint64(placement.Snapshot.Native)), LeaseId: proto.String(lease), Attempt: b.Attempt(placement)}
	if err = ctx.Err(); err != nil {
		return out, err
	}
	reply, _, err := b.Writer.PlaceBuilding(ctx, pre, candidate)
	var refused *bridge.NativeFailure
	if errors.As(err, &refused) {
		if refused.Value.GetCode() == c.FailureCode_FAILURE_CODE_ATTEMPT_CONFLICT {
			return out, err
		}
		out.Kind = domain.ReceiptRefused
		return out, nil
	}
	if err != nil {
		return out, err
	}
	admitted := reply.GetReceipt()
	if err = Admission(admitted, placement, b.Session); err != nil {
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
	out := executor.Evidence{StartedAt: b.Clock.Now(), Observation: domain.Observation{Action: placement.Action.ID(), Attempt: placement.Attempt, Snapshot: current, Effect: domain.EffectUnknown}}
	candidate, err := ValidatePlacement(placement)
	if err != nil {
		return out, err
	}
	if current.Validate() != nil || current.Native == 0 || !World(placement.Snapshot, current) {
		return out, executor.ErrAuthority
	}
	lookup, _, err := b.Native.LookupBuildingAttempt(ctx, Identity(placement.Snapshot), b.Attempt(placement), uint64(placement.Snapshot.Native), candidate)
	if err != nil {
		return out, err
	}
	admitted := lookup.GetReceipt()
	if admitted == nil {
		return out, executor.ErrHeld
	} // Missing ledger or in-flight is never no-effect proof.
	if err = Admission(admitted, placement, b.Session); err != nil {
		return out, err
	}
	reply, _, err := b.Native.ObserveBuildingProgress(ctx, admitted, candidate)
	if err != nil {
		return out, err
	}
	progress := reply.GetProgress()
	if progress == nil || !proto.Equal(progress.Attempt, b.Attempt(placement)) {
		return out, executor.ErrEvidence
	}
	observed, err := Context(progress.Context, current)
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
		if out.Complete {
			effect := v.Pending.GetEvidence().GetConstruction()
			if effect == nil || effect.Stage == nil || (effect.GetStage() != r.ConstructionStage_CONSTRUCTION_STAGE_BLUEPRINT && effect.GetStage() != r.ConstructionStage_CONSTRUCTION_STAGE_FRAME) || effect.Present == nil || !effect.GetPresent() || effect.Failed == nil || effect.GetFailed() || effect.Cell == nil || effect.DefName == nil || effect.Stuff == nil || effect.Rotation == nil || !ValidID(effect.GetOriginThingId()) || !ValidID(effect.GetCurrentThingId()) {
				return out, executor.ErrEvidence
			}
			origin := Origin(admitted)
			if origin != "" && effect.GetOriginThingId() != origin {
				return out, executor.ErrEvidence
			}
			building, err := domain.NewBuilding(effect.GetDefName(), domain.Cell{X: effect.Cell.GetX(), Z: effect.Cell.GetZ()}, RotationOf(effect.GetRotation()), effect.GetStuff())
			wanted, _ := placement.Action.Building()
			if err != nil || building != wanted {
				return out, executor.ErrEvidence
			}
			out.Observation.ConstructionObserved = true
		}
	case *r.Progress_Absent:
		if !out.Complete || v.Absent == nil || !ValidID(v.Absent.GetInspectionToken()) {
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
		if !out.Complete || effect == nil || effect.Stage == nil || effect.GetStage() != r.ConstructionStage_CONSTRUCTION_STAGE_BUILDING || effect.Present == nil || !effect.GetPresent() || effect.Failed == nil || effect.GetFailed() || effect.Cell == nil || effect.DefName == nil || effect.Stuff == nil || effect.Rotation == nil || !ValidID(effect.GetOriginThingId()) || !ValidID(effect.GetCurrentThingId()) {
			return out, executor.ErrEvidence
		}
		origin := Origin(admitted)
		if origin != "" && effect.GetOriginThingId() != origin {
			return out, executor.ErrEvidence
		}
		building, err := domain.NewBuilding(effect.GetDefName(), domain.Cell{X: effect.Cell.GetX(), Z: effect.Cell.GetZ()}, RotationOf(effect.GetRotation()), effect.GetStuff())
		wanted, _ := placement.Action.Building()
		if err != nil || building != wanted {
			return out, executor.ErrEvidence
		}
		out.Observation.Construction = &domain.ConstructionIdentity{Origin: effect.GetOriginThingId(), Current: effect.GetCurrentThingId()}
		out.Built = domain.Known(building)
		out.Observation.Effect = domain.EffectCompleted
	default:
		return out, executor.ErrEvidence
	}
	if err = ctx.Err(); err != nil {
		return out, err
	}
	out.ObservedAt = b.Clock.Now()
	return out, nil
}

// ValidID reports whether s is a well-formed opaque identifier: valid UTF-8,
// non-blank, NUL-free and bounded, as required for session/lease/native IDs.
func ValidID(s string) bool {
	return utf8.ValidString(s) && strings.TrimSpace(s) != "" && len(s) <= 256 && !strings.ContainsRune(s, 0)
}
func Identity(s domain.GenerationSnapshot) *c.Identity {
	return &c.Identity{ColonyId: proto.String(string(s.Colony)), LoadToken: proto.String(string(s.Load)), MapId: proto.Int32(int32(s.Map))}
}
func (b *Boundary) Attempt(p executor.Placement) *c.AttemptKey {
	return &c.AttemptKey{ControllerSessionId: proto.String(b.Session), ActionId: proto.String(string(p.Action.ID())), AttemptId: proto.Uint64(uint64(p.Attempt))}
}
func World(a, b domain.GenerationSnapshot) bool {
	return a.Colony == b.Colony && a.Load == b.Load && a.Map == b.Map
}
func Context(v *c.ObservationContext, expected domain.GenerationSnapshot) (domain.GenerationSnapshot, error) {
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
	if !World(out, expected) {
		return domain.GenerationSnapshot{}, executor.ErrAuthority
	}
	return out, nil
}
func Candidate(action domain.Action, snapshot domain.GenerationSnapshot) (*p.PlacementCandidate, error) {
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
func RotationOf(v p.Rotation) domain.Rotation {
	return map[p.Rotation]domain.Rotation{p.Rotation_ROTATION_NORTH: domain.North, p.Rotation_ROTATION_EAST: domain.East, p.Rotation_ROTATION_SOUTH: domain.South, p.Rotation_ROTATION_WEST: domain.West}[v]
}
func ValidatePlacement(value executor.Placement) (*p.PlacementCandidate, error) {
	if value.Attempt == 0 || value.Tick < 0 {
		return nil, executor.ErrEvidence
	}
	return Candidate(value.Action, value.Snapshot)
}
func Admission(receipt *r.Receipt, placement executor.Placement, session string) error {
	if receipt == nil || receipt.Attempt == nil || receipt.Attempt.GetControllerSessionId() != session || receipt.Attempt.GetActionId() != string(placement.Action.ID()) || receipt.Attempt.GetAttemptId() != uint64(placement.Attempt) {
		return executor.ErrEvidence
	}
	if _, err := Context(receipt.AdmittedContext, placement.Snapshot); err != nil {
		return err
	}
	owner := receipt.AuthorizingOwner
	if owner == nil || owner.ControllerSessionId == nil || owner.PlayerDirection == nil || owner.GetControllerSessionId() != session || owner.GetPlayerDirection() != uint64(placement.Snapshot.Direction) {
		return fmt.Errorf("%w: original authorizing owner changed", executor.ErrEvidence)
	}
	return nil
}

func Origin(receipt *r.Receipt) string {
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
