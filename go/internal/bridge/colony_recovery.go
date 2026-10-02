package bridge

import (
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func validateColonyRecovery(colony *o.ColonyFactsSnapshot) error {
	reply := colony.Recovery
	if reply == nil {
		return nil
	} // Older native producers explicitly omit this section.
	for _, issue := range colony.Issues {
		if issue.GetField() == "recovery" {
			return contract("unavailable recovery contains a reply")
		}
	}
	if reply.GetUnavailable() != nil {
		return validateUnavailable(reply.GetUnavailable())
	}
	v := reply.GetObserved()
	if v == nil || !proto.Equal(v.Context, colony.Context) || !proto.Equal(v, &o.RecoverySnapshot{Context: v.Context, Restrictions: v.Restrictions, Buildings: v.Buildings}) {
		return contract("invalid recovery context or outcome")
	}
	seen := map[string]bool{}
	for _, ref := range v.Buildings {
		if !powerEntity(ref, colony.Context.Identity, colony.MapSize) || seen[ref.GetId()] {
			return contract("invalid recovery building identity")
		}
		seen[ref.GetId()] = true
	}
	seen = map[string]bool{}
	for _, r := range v.Restrictions {
		if r == nil || !powerEntity(r.Pawn, colony.Context.Identity, colony.MapSize) || seen[r.Pawn.GetId()] || r.AreaId != nil && validID(r.GetAreaId()) != nil || !proto.Equal(r, &o.RecoveryRestriction{Pawn: r.Pawn, AreaId: r.AreaId}) {
			return contract("invalid recovery restriction")
		}
		seen[r.Pawn.GetId()] = true
	}
	return nil
}
