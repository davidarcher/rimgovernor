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

type AncientShrine = policy.AncientShrine

type ShrineSource interface {
	ReadAncientShrines(context.Context, *c.Identity) (*o.AncientShrinesReply, bridge.Result, error)
}

// ObserveShrines tolerates an explicit unavailable native stub as unknown,
// distinct from a complete census with no shrines. Transport,
// malformed-contract and identity errors remain errors.
func ObserveShrines(ctx context.Context, source ShrineSource, expected Identity) (domain.Fact[[]AncientShrine], error) {
	unknown := domain.Unknown[[]AncientShrine]()
	if source == nil || expected.Validate() != nil || !sameColonyBoundary(expected, expected) {
		return unknown, ErrContract
	}
	id := &c.Identity{ColonyId: proto.String(string(expected.Colony)), LoadToken: proto.String(string(expected.Load)), MapId: proto.Int32(int32(expected.Map))}
	reply, _, err := source.ReadAncientShrines(ctx, id)
	if errors.Is(err, bridge.ErrUnavailable) {
		return unknown, nil
	}
	if err != nil {
		return unknown, err
	}
	if reply == nil {
		return unknown, ErrContract
	}
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
	if err := bridge.ValidateAncientShrines(v, id); err != nil {
		return unknown, err
	}
	actual, err := contextIdentity(v.Context)
	if err != nil {
		return unknown, err
	}
	if !sameColonyBoundary(actual, expected) {
		return unknown, ErrChanged
	}
	kinds := map[o.ShrineGuardKind]policy.ShrineGuardKind{
		o.ShrineGuardKind_SHRINE_GUARD_KIND_MECHANOID:  policy.ShrineGuardMechanoid,
		o.ShrineGuardKind_SHRINE_GUARD_KIND_INSECTOID:  policy.ShrineGuardInsectoid,
		o.ShrineGuardKind_SHRINE_GUARD_KIND_FLESHBEAST: policy.ShrineGuardFleshbeast,
		o.ShrineGuardKind_SHRINE_GUARD_KIND_HUMAN:      policy.ShrineGuardHuman,
		o.ShrineGuardKind_SHRINE_GUARD_KIND_HIVE:       policy.ShrineGuardHive,
		o.ShrineGuardKind_SHRINE_GUARD_KIND_OTHER:      policy.ShrineGuardOther,
	}
	cell := func(v *c.Cell) domain.Cell { return domain.Cell{X: v.GetX(), Z: v.GetZ()} }
	rows := make([]AncientShrine, 0, len(v.Shrines))
	for _, row := range v.Shrines {
		shrine := AncientShrine{ID: row.GetShrineId(), Minimum: cell(row.Room.Minimum), Maximum: cell(row.Room.Maximum), Sealed: row.GetSealed(), InHome: row.GetInHome(), GuardsKnown: row.GetGuardsKnown()}
		for _, casket := range row.Caskets {
			shrine.Caskets = append(shrine.Caskets, policy.ShrineCasket{EntityID: casket.GetEntityId(), Cell: cell(casket.Cell), InteractionCell: cell(casket.InteractionCell), HitPoints: casket.GetHitPoints(), MaxHitPoints: casket.GetMaxHitPoints(), HasContents: casket.GetHasContents(), PlayerClaimed: casket.GetPlayerClaimed()})
		}
		for _, guard := range row.Guards {
			shrine.Guards = append(shrine.Guards, policy.ShrineGuard{EntityID: guard.GetEntityId(), Kind: kinds[guard.Kind], Downed: guard.GetDowned(), Dead: guard.GetDead()})
		}
		for _, wall := range row.BreachWalls {
			shrine.BreachWalls = append(shrine.BreachWalls, policy.ShrineBreachWall{EntityID: wall.GetEntityId(), DefName: wall.GetDefName(), Cell: cell(wall.Cell), Outside: cell(wall.Outside)})
		}
		for _, occupant := range row.Occupants {
			shrine.Occupants = append(shrine.Occupants, policy.ShrineOccupant{EntityID: occupant.GetEntityId(), Hostile: occupant.GetHostile(), Downed: occupant.GetDowned(), Dead: occupant.GetDead(), Prisoner: occupant.GetPrisoner(), Faction: occupant.GetFaction()})
		}
		rows = append(rows, shrine)
	}
	return domain.Known(rows), ctx.Err()
}
