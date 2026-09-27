package draft

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// ReleaseFightClaim releases the draft claim a combat fight holds on pawn
// in world (#910); claim is empty when the admission batch left it
// unknown, and the pawn's row names it. It reports whether the fight no
// longer holds the pawn: released or already released, another world
// loaded, the pawn gone from a complete read, or no claim or another claim
// on its draft. An uncertain release keeps it.
func (b *DraftBoundary) ReleaseFightClaim(ctx context.Context, world store.World, pawn domain.PawnID, claim string) (bool, error) {
	current, err := b.ReadWorld(ctx)
	if err != nil {
		return false, err
	}
	if current != world {
		return true, nil
	}
	identity := &c.Identity{ColonyId: proto.String(string(world.Colony)), LoadToken: proto.String(string(world.Load)), MapId: proto.Int32(int32(world.Map))}
	reply, _, err := b.native.ReadPawns(ctx, identity, []string{string(pawn)})
	if err != nil {
		return false, err
	}
	v := reply.GetObserved()
	if v == nil || bridge.ValidateContext(v.Context) != nil || !proto.Equal(v.Context.Identity, identity) {
		return false, fmt.Errorf("%w: pawn %s not observed", executor.ErrHeld, pawn)
	}
	if len(v.Pawns) > 1 {
		return false, fmt.Errorf("%w: pawn %s read incomplete", executor.ErrHeld, pawn)
	}
	if len(v.Pawns) == 0 {
		return true, nil
	}
	row := v.Pawns[0]
	if row == nil || row.Pawn == nil || row.Pawn.GetId() != string(pawn) {
		return false, executor.ErrEvidence
	}
	switch state := row.GetDraftClaim().GetState().(type) {
	case *n.DraftClaimObservation_Unowned:
		return true, nil
	case *n.DraftClaimObservation_Owned:
		owned := state.Owned.GetClaimId()
		if !boundary.ValidID(owned) {
			return false, executor.ErrEvidence
		}
		if claim != "" && owned != claim {
			return true, nil
		}
		claim = owned
	default:
		return false, fmt.Errorf("%w: pawn %s claim unknown", executor.ErrHeld, pawn)
	}
	token, err := boundary.PawnToken(row, v.Context)
	if err != nil {
		return false, err
	}
	request := &o.ReleaseOwnedDraftRequest{Identity: identity, Pawn: &o.EntityPrecondition{EntityId: proto.String(string(pawn)), ExpectedSnapshotToken: proto.String(token)}, ExpectedClaimId: proto.String(claim)}
	released, _, err := b.cleanup.ReleaseOwnedDraft(ctx, request)
	if err != nil {
		return false, err
	}
	switch released.GetOutcome().(type) {
	case *o.ReleaseOwnedDraftReply_Released, *o.ReleaseOwnedDraftReply_AlreadyReleased:
		return true, nil
	case *o.ReleaseOwnedDraftReply_Uncertain:
		return false, nil
	}
	return false, executor.ErrEvidence
}
