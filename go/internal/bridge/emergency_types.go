package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// EmergencyObservation contains actual native context, not controller authority.
// The runtime binds these facts to its captured direction and plan after validation.
type EmergencyObservation struct {
	Context *c.ObservationContext
	Facts   policy.EmergencyFacts
}
