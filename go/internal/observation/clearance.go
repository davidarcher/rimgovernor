package observation

import (
	"context"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

type ClearanceClass = string

const (
	ClearanceAncientWallDoor ClearanceClass = "ancient_wall_door"
	ClearanceShipChunk       ClearanceClass = "ship_chunk"
	ClearanceAncientCasket   ClearanceClass = "ancient_casket"
	ClearanceOther           ClearanceClass = "other"
)

// ClearanceTarget is an observed building, not an admitted demolition. Empty
// Faction means neutral and empty RoofBlocker means supported without this
// one building. Combined removals require a new counterfactual check.
type ClearanceTarget = policy.ClearanceTarget

type ClearanceSource interface {
	ReadClearanceTargets(context.Context, *c.Identity) (*o.ClearanceTargetsReply, bridge.Result, error)
}

// ObserveClearance tolerates an explicit unavailable native stub as unknown.
// Transport, malformed-contract and identity errors remain errors.
func ObserveClearance(ctx context.Context, source ClearanceSource, expected Identity) (domain.Fact[[]ClearanceTarget], error) {
	unknown := domain.Unknown[[]ClearanceTarget]()
	if source == nil || expected.Validate() != nil || !sameColonyBoundary(expected, expected) {
		return unknown, ErrContract
	}
	id := &c.Identity{ColonyId: proto.String(string(expected.Colony)), LoadToken: proto.String(string(expected.Load)), MapId: proto.Int32(int32(expected.Map))}
	reply, _, err := source.ReadClearanceTargets(ctx, id)
	if errors.Is(err, bridge.ErrUnavailable) {
		return unknown, nil
	}
	if err != nil {
		return unknown, err
	}
	if reply == nil {
		return unknown, ErrContract
	}
	// Test and staged native sources can return the typed stub without an error.
	if u := reply.GetUnavailable(); u != nil {
		if u.Reason == nil || u.GetReason() == c.UnavailableReason_UNAVAILABLE_REASON_UNSPECIFIED {
			return unknown, ErrContract
		}
		if _, ok := c.UnavailableReason_name[int32(u.GetReason())]; !ok {
			return unknown, ErrContract
		}
		return unknown, nil
	}
	v := reply.GetObserved()
	if err := bridge.ValidateClearanceTargets(v, id); err != nil {
		return unknown, err
	}
	actual, err := contextIdentity(v.Context)
	if err != nil {
		return unknown, err
	}
	if !sameColonyBoundary(actual, expected) {
		return unknown, ErrChanged
	}
	rows := make([]ClearanceTarget, 0, len(v.Targets))
	classes := map[o.ClearanceClass]ClearanceClass{
		o.ClearanceClass_CLEARANCE_CLASS_ANCIENT_WALL_DOOR: ClearanceAncientWallDoor,
		o.ClearanceClass_CLEARANCE_CLASS_SHIP_CHUNK:        ClearanceShipChunk,
		o.ClearanceClass_CLEARANCE_CLASS_ANCIENT_CASKET:    ClearanceAncientCasket,
		o.ClearanceClass_CLEARANCE_CLASS_OTHER:             ClearanceOther,
	}
	for _, row := range v.Targets {
		rows = append(rows, ClearanceTarget{EntityID: row.GetEntityId(), DefName: row.GetDefName(), Faction: row.GetFaction(), Class: classes[row.Class], Minimum: domain.Cell{X: row.Occupied.Minimum.GetX(), Z: row.Occupied.Minimum.GetZ()}, Maximum: domain.Cell{X: row.Occupied.Maximum.GetX(), Z: row.Occupied.Maximum.GetZ()}, Deconstructible: row.GetDeconstructible(), InHome: row.GetInHome(), AncientDanger: row.GetAncientDanger(), RoofBlocker: row.GetRoofBlocker(), Designated: row.GetDesignated(), ControllerOwned: row.GetControllerOwned()})
	}
	return domain.Known(rows), ctx.Err()
}
