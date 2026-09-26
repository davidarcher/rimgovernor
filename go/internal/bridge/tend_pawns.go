package bridge

import (
	"context"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// ReadTendPawns retains combat facts (health, equipment, biography including
// Medicine skill), work settings (Doctor work-type enablement) and care policy
// (medical_care, self_tend) for exact doctor/patient IDs, and adds the tend
// detail: the doctor-side native gates (pawn-control eligibility, WorkGiver_Tend
// capacities) plus reachability between the requested pawns, so SelectTend never
// proposes a pair the native tend gate would refuse (#657).
func (client *Client) ReadTendPawns(ctx context.Context, identity *c.Identity, ids []string) (*o.ListPawnsReply, Result, error) {
	return client.readPawnDetails(ctx, identity, ids, pawnDetails{Combat: true, Work: true, Care: true, Tend: true})
}
