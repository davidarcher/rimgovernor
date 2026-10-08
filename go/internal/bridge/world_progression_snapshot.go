package bridge

import (
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// DecodeWorldProgressionSnapshot validates the world census already carried
// by a frame against that frame's exact identity.
func DecodeWorldProgressionSnapshot(snapshot *o.WorldProgressionSnapshot, identity *c.Identity) (WorldProgressionRead, error) {
	return worldProgressionSelected(snapshot, identity)
}
