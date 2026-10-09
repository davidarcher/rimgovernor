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
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

type Native interface {
	ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
	PreviewBuilding(context.Context, domain.Action, domain.GenerationSnapshot) (bridge.BuildingPreview, bridge.Result, error)
	ReadMapBounds(context.Context, *c.Identity, domain.Cell) (bridge.MapBounds, bridge.Result, error)
}

// BuildingWriter sends building intents through Actions/Apply.
type BuildingWriter = ActionsWriter
type LeaseSource interface {
	Lease(domain.GenerationSnapshot) (string, error)
}

// FixedClock is a shared test fixture: a clock stuck at a fixed instant, used
// by every family package's boundary tests to avoid duplicating a fake clock.
type FixedClock struct{}

func (FixedClock) Now() time.Time { return time.Unix(100, 0) }

type Boundary struct {
	Native  Native
	Writer  BuildingWriter
	Leases  LeaseSource
	Clock   executor.Clock
	Session string
}

var _ executor.Boundary = (*Boundary)(nil)

func NewBoundary(native Native, writer BuildingWriter, leases LeaseSource, clock executor.Clock, controllerSessionID string) (*Boundary, error) {
	if native == nil || writer == nil || leases == nil || clock == nil || !ValidID(controllerSessionID) {
		return nil, errors.New("invalid building boundary dependencies")
	}
	return &Boundary{native, writer, leases, clock, controllerSessionID}, nil
}

// InspectIntent anchors an intent to the current world and tick with one
// map bounds read (at a building's cell, else the map origin). It previews
// nothing: native validates the intent when it applies it.
func (b *Boundary) InspectIntent(ctx context.Context, target executor.Target) (executor.IntentInspection, error) {
	out := executor.IntentInspection{StartedAt: b.Clock.Now()}
	var anchor domain.Cell
	if value, ok := target.Action.Building(); ok {
		anchor = value.Cell()
	}
	bounds, _, err := b.Native.ReadMapBounds(ctx, Identity(target.Snapshot), anchor)
	if err != nil {
		return out, err
	}
	if out.Current, err = Context(bounds.Context, target.Snapshot); err != nil {
		return out, err
	}
	out.Tick = domain.Tick(bounds.Context.GetTick())
	out.ObservedAt = b.Clock.Now()
	return out, ctx.Err()
}

// WriteIntents sends the intents through one Actions/Apply call.
func (b *Boundary) WriteIntents(ctx context.Context, placements []executor.Placement) ([]executor.Receipt, error) {
	return DispatchIntents(ctx, b.Leases, placements, b.Writer)
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
func Admission(receipt *r.Receipt, placement executor.Placement, session string) error {
	if receipt == nil || receipt.Attempt == nil || receipt.Attempt.GetControllerSessionId() != session || receipt.Attempt.GetActionId() != string(placement.Action.ID()) || receipt.Attempt.GetAttemptId() != uint64(placement.Attempt) {
		return executor.ErrEvidence
	}
	if _, err := Context(receipt.AdmittedContext, placement.Snapshot); err != nil {
		return err
	}
	return nil
}

// Unadmitted resolves a receipt-free ledger lookup under the attempt's load.
// Admission and lookup share the native main thread, so a missing entry proves
// no effect occurred and permits a fresh Pending attempt. In-flight entries
// remain held until their receipts arrive.
func Unadmitted(lookup *r.LookupReply, placement executor.Placement, current domain.GenerationSnapshot) (domain.Observation, error) {
	out := domain.Observation{Action: placement.Action.ID(), Attempt: placement.Attempt, Snapshot: current, Effect: domain.EffectUnknown}
	switch v := lookup.GetOutcome().(type) {
	case *r.LookupReply_InFlight:
		return out, fmt.Errorf("%w: attempt %d is admitted and still in flight", executor.ErrHeld, placement.Attempt)
	case *r.LookupReply_Unknown:
		observed, err := Context(v.Unknown.GetContext(), current)
		if err != nil {
			return out, err
		}
		if v.Unknown.GetContext().GetTick() < int64(placement.Tick) {
			return out, executor.ErrEvidence
		}
		out.Snapshot, out.Tick, out.Causality, out.Effect = observed, domain.Tick(v.Unknown.GetContext().GetTick()), domain.AfterDispatch, domain.EffectAbsent
		return out, nil
	}
	return out, executor.ErrEvidence
}
