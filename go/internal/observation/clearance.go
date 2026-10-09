package observation

import (
	"context"
	"errors"
	"fmt"

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

// salvageUnitsPerTrip is the units one hauler carries per trip when pricing a
// salvage yield; policy, not a native fact.
const salvageUnitsPerTrip int64 = 75

// ClearanceTarget is an observed building, not an admitted demolition. Empty
// Faction means neutral and empty RoofBlocker means supported without this
// one building. Combined removals require a new counterfactual check.
type ClearanceTarget = policy.ClearanceTarget
type ClearanceChunk = policy.ClearanceChunk
type ClearanceCensus = policy.ClearanceCensus

type ClearanceSource interface {
	ReadClearanceTargets(context.Context, *c.Identity, bool) (*o.ClearanceTargetsReply, bridge.Result, error)
}

// GroundClearanceSource widens the census to planned ground.
type GroundClearanceSource interface {
	ReadClearanceTargetsOnGround(context.Context, *c.Identity, bool, []*o.Rectangle) (*o.ClearanceTargetsReply, bridge.Result, error)
}

// ObserveClearanceCensusOnGround also reads the player's buildings (Player
// rows) and constructed floors on planned ground. A source without
// the planned-ground read is a contract error when ground is requested.
func ObserveClearanceCensusOnGround(ctx context.Context, source ClearanceSource, expected Identity, includeSalvage bool, ground []policy.Rectangle) (domain.Fact[ClearanceCensus], error) {
	unknown := domain.Unknown[ClearanceCensus]()
	if source == nil || expected.Validate() != nil || !sameColonyContext(expected, expected) {
		return unknown, ErrContract
	}
	id := &c.Identity{ColonyId: proto.String(string(expected.Colony)), LoadToken: proto.String(string(expected.Load)), MapId: proto.Int32(int32(expected.Map))}
	var reply *o.ClearanceTargetsReply
	var err error
	if len(ground) > 0 {
		widened, ok := source.(GroundClearanceSource)
		if !ok {
			return unknown, fmt.Errorf("%w: clearance source lacks the planned-ground read", ErrContract)
		}
		rects := make([]*o.Rectangle, 0, len(ground))
		for _, g := range ground {
			rects = append(rects, &o.Rectangle{Minimum: &c.Cell{X: proto.Int32(g.X), Z: proto.Int32(g.Z)}, Maximum: &c.Cell{X: proto.Int32(g.X + g.Width - 1), Z: proto.Int32(g.Z + g.Height - 1)}})
		}
		reply, _, err = widened.ReadClearanceTargetsOnGround(ctx, id, includeSalvage, rects)
	} else {
		reply, _, err = source.ReadClearanceTargets(ctx, id, includeSalvage)
	}
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
	if !sameColonyContext(actual, expected) {
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
		rows = append(rows, ClearanceTarget{EntityID: row.GetEntityId(), DefName: row.GetDefName(), Class: classes[row.Class], Minimum: domain.Cell{X: row.Occupied.Minimum.GetX(), Z: row.Occupied.Minimum.GetZ()}, Maximum: domain.Cell{X: row.Occupied.Maximum.GetX(), Z: row.Occupied.Maximum.GetZ()}, Deconstructible: row.GetDeconstructible(), InHome: row.GetInHome(), AncientDanger: row.GetAncientDanger(), RoofBlocker: row.GetRoofBlocker(), Designated: row.GetDesignated(), Player: row.EnclosesRoom != nil, EnclosesRoom: row.GetEnclosesRoom(), SalvageSkipped: row.GetSalvageSkipped()})
		if s := row.Salvage; s != nil {
			yields := make([]policy.CandidateYield, 0, len(s.Yields))
			for _, y := range s.Yields {
				yields = append(yields, policy.SourceYield(policy.ResourceKey{Def: policy.Resource(y.DefName)}, y.Count, y.UnitValue, domain.Known(y.StorageHeadroom)))
			}
			candidate := policy.SourceCandidate(policy.CandidateSalvage, row.GetEntityId(), domain.Known(s.Labor), domain.Known(s.PathLength), true, salvageUnitsPerTrip, yields...)
			rows[len(rows)-1].Salvage = &policy.SalvageEvidence{Safe: domain.Known(s.Safe), Candidate: candidate, AgeTicks: s.AgeTicks}
		}
	}
	chunks := make([]ClearanceChunk, 0, len(v.Chunks))
	for _, row := range v.Chunks {
		chunks = append(chunks, ClearanceChunk{EntityID: row.GetEntityId(), DefName: row.GetDefName(), Cell: domain.Cell{X: row.Cell.GetX(), Z: row.Cell.GetZ()}, Forbidden: row.GetForbidden(), Stored: row.GetStored(), Destination: row.GetDestination()})
	}
	floors := make([]policy.ClearanceFloor, 0, len(v.Floors))
	for _, row := range v.Floors {
		floors = append(floors, policy.ClearanceFloor{Cell: domain.Cell{X: row.Cell.GetX(), Z: row.Cell.GetZ()}, DefName: row.GetDefName(), Designated: row.GetDesignated()})
	}
	return domain.Known(ClearanceCensus{Targets: rows, Chunks: chunks, Floors: floors}), ctx.Err()
}
