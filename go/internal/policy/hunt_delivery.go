package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// HuntDelivery is the lifecycle of the formation hunt in memory: its prey are
// admitted when the plan opens them (HuntRequest, which raises the
// ActiveCombat hunt origin), and the hunt is delivering from the first KILL of
// an admitted animal in the delivery ledger. It ends when no admitted animal
// stands any more. A restart forgets it; the next plan admits again.
type HuntDelivery struct {
	admitted   map[string]bool
	delivering bool
}

// Admit records the prey the plan opened; call it with HuntRequest of each
// plan.
func (h *HuntDelivery) Admit(prey []domain.PawnID) {
	for _, id := range prey {
		if h.admitted == nil {
			h.admitted = map[string]bool{}
		}
		h.admitted[string(id)] = true
	}
}

// Apply states the formation channels that hold admitted prey: Designated
// from admission, Delivering from the first kill of an admitted animal
// (killed holds the pawn ids of the ledger's kills). Lone channels and
// channels of other prey are returned as they are.
func (h *HuntDelivery) Apply(channels []FoodChannel, killed map[string]bool) []FoodChannel {
	if h == nil || len(h.admitted) == 0 {
		return channels
	}
	for id := range h.admitted {
		h.delivering = h.delivering || killed[id]
	}
	standing := false
	out := make([]FoodChannel, len(channels))
	copy(out, channels)
	for i, c := range out {
		if c.Kind != FoodHunt || c.Mode() != HuntFormation {
			continue
		}
		for _, id := range c.Prey {
			if h.admitted[id] {
				standing = true
				c.Open, c.Designated = domain.Known(h.delivering), domain.Known(!h.delivering)
				out[i] = c
				break
			}
		}
	}
	if !standing {
		*h = HuntDelivery{}
	}
	return out
}
