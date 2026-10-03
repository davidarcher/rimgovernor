package bridge

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// ReadMechs reads the mechs the ids name (a mechanitor's controlled mechs,
// PawnMechanitor.ControlledMechs) as the mech planners' input rows: kind
// (PawnState.kind_def_name) and the Biotech mech block. No ids reads
// nothing. A row that is no mech, or carries no kind or mech block, is a
// contract failure, never skipped.
func (client *Client) ReadMechs(ctx context.Context, identity *c.Identity, ids []string) ([]policy.MechInput, error) {
	if len(ids) == 0 {
		return nil, nil
	}
	reply, _, err := client.ReadPawns(ctx, identity, ids)
	if err != nil {
		return nil, err
	}
	return MechInputs(reply.GetObserved())
}

// MechInputs lifts mech pawn rows into planner input.
func MechInputs(snapshot *o.PawnSnapshot) ([]policy.MechInput, error) {
	out := make([]policy.MechInput, 0, len(snapshot.GetPawns()))
	for _, row := range snapshot.GetPawns() {
		id := row.GetPawn().GetId()
		if !row.GetMechanoid() || row.KindDefName == nil {
			return nil, contract("mech %s: not a mechanoid with a kind", id)
		}
		bt, ok := PawnBiotech(row.Biotech).Value()
		mech, mk := bt.Mech.Value()
		if !ok || !mk || mech == nil {
			return nil, contract("mech %s: no Biotech mech block", id)
		}
		out = append(out, policy.MechInput{ID: policy.PawnID(id), Kind: row.GetKindDefName(), PawnMech: *mech, Downed: row.GetDowned(), Drafted: row.GetDrafted()})
	}
	return out, nil
}
