package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// HuntAdmission is the formation hunts the plan opened, in memory: their prey
// are admitted when the plan opens them (HuntRequest, which raises the
// ActiveCombat hunt origin), and a formation channel of admitted prey is
// Designated from then, so the delivery credit commits it and moves the hunt
// group to Delivering at the first kill. It ends when no admitted animal
// stands any more. A restart forgets it; the next plan admits again.
type HuntAdmission struct {
	admitted map[string]bool
}

// Admit records the prey the plan opened; call it with HuntRequest of each
// plan.
func (h *HuntAdmission) Admit(prey []domain.PawnID) {
	for _, id := range prey {
		if h.admitted == nil {
			h.admitted = map[string]bool{}
		}
		h.admitted[string(id)] = true
	}
}

// Apply marks the formation channels that hold admitted prey Designated; lone
// channels and channels of other prey are returned as they are.
func (h *HuntAdmission) Apply(channels []SupplyCandidate) []SupplyCandidate {
	if h == nil || len(h.admitted) == 0 {
		return channels
	}
	standing := false
	out := make([]SupplyCandidate, len(channels))
	copy(out, channels)
	for i, c := range out {
		if c.Kind != CandidateHunt || c.Mode() != HuntFormation {
			continue
		}
		for _, id := range c.Prey {
			if h.admitted[id] {
				standing = true
				c.State = FoodState(domain.Known(false), domain.Known(true))
				out[i] = c
				break
			}
		}
	}
	if !standing {
		*h = HuntAdmission{}
	}
	return out
}
