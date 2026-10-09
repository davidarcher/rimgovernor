package bridge

import (
	"context"
	"errors"
	"fmt"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// StepRequest is what a scheduler step, an event poll or a renewal opens
// with: the current scope, and on request the owned clock status and the
// emergency census. Identity, when set, is the world the caller expects;
// another one is ErrRefused.
type StepRequest struct {
	Identity    *c.Identity
	ClockStatus bool
	Emergency   bool
}

// ReadStep composes the step's snapshot: the live tick (scope and pause
// state), then the clock status the controller owns, then the emergency
// census, served from the snapshot stream. The sections are read in
// that order, so the clock status and the census never predate the scope.
func (client *Client) ReadStep(ctx context.Context, request StepRequest) (*o.BundleSnapshot, Result, error) {
	if request.Identity != nil {
		if err := ValidateIdentity(request.Identity); err != nil {
			return nil, Result{}, err
		}
	}
	tick, raw, err := client.Tick(ctx)
	if err != nil {
		return nil, raw, err
	}
	loaded := tick.GetLoaded()
	if loaded == nil || loaded.Paused == nil {
		return nil, raw, contract("step tick missing")
	}
	identity := loaded.Context.Identity
	if request.Identity != nil && !sameIdentity(identity, request.Identity) {
		return nil, raw, fmt.Errorf("%w: step identity changed", ErrRefused)
	}
	v := &o.BundleSnapshot{Context: loaded.Context, Paused: loaded.Paused}
	var unavailableStatus error
	if request.ClockStatus {
		reply, statusRaw, err := client.ReadClockStatus(ctx, identity)
		if err != nil && !errors.Is(err, ErrUnavailable) {
			return nil, statusRaw, err
		}
		v.ClockStatus, unavailableStatus = reply.GetStatus(), err
		if v.ClockStatus == nil && err == nil {
			return nil, statusRaw, contract("step clock status missing")
		}
	}
	if request.Emergency {
		reply := &o.StatusReply{}
		emergencyRaw, err := client.protoRead(ctx, "rimgovernor/observations_read_status", emergencyRequest(identity), reply)
		if err != nil {
			return nil, emergencyRaw, err
		}
		switch value := reply.Outcome.(type) {
		case *o.StatusReply_Observed:
			// The census references resolve against the pawn table.
			table, err := client.framePawnSnapshot(ctx, identity)
			if err != nil {
				return nil, emergencyRaw, err
			}
			pawns, err := PawnTable(table, identity)
			if err != nil {
				return nil, emergencyRaw, err
			}
			if _, err := DecodeEmergencyStatus(value.Observed, pawns, identity); err != nil {
				return nil, emergencyRaw, err
			}
			v.Emergency, v.Pawns = proto.Clone(value.Observed).(*o.StatusSnapshot), table
		case *o.StatusReply_Unavailable:
			return nil, emergencyRaw, unavailable(value.Unavailable, emergencyRaw)
		case *o.StatusReply_Failure:
			return nil, emergencyRaw, failure(value.Failure, emergencyRaw)
		default:
			return nil, emergencyRaw, contract("emergency status outcome missing")
		}
		// A drop-pod raid's raiders are in no census row until the pods
		// open; the stream's arrival rows carry it.
		if client.frames != nil {
			combat := &o.BundleSnapshot{}
			if _, err := client.frameReadKey(ctx, combatFrameMethod, readCacheKey{method: combatFrameMethod}, identity, true, combat); err != nil {
				return nil, emergencyRaw, err
			}
			v.CombatEvents = podArrivals(combat.CombatEvents)
		}
	}
	return v, raw, unavailableStatus
}
