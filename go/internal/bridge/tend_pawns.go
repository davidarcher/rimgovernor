package bridge

import (
	"context"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// ReadTendPawns retains combat facts (health, equipment, biography including
// Medicine skill), work settings (Doctor work-type enablement) and care policy
// (medical_care, self_tend) for exact doctor/patient IDs.
func (client *Client) ReadTendPawns(ctx context.Context, identity *c.Identity, ids []string) (*o.ListPawnsReply, Result, error) {
	return client.readPawnDetails(ctx, identity, ids, true, true, true, false)
}
