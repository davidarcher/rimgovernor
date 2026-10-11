package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// TendThreatCells is how close (Chebyshev cells) a live hostile pawn must stand
// to a patient for the tend to wait: a manhunter closes that gap in seconds,
// before a doctor arrives, and the doctor would meet it alone.
const TendThreatCells = 8

// ThreatenedPatients are the colonists with a holding hostile pawn (ThreatHolds,
// never a building) known within TendThreatCells. A patient or threat with an
// unknown cell is not named: the veto needs a measured gap, and a far or
// unplaced threat is the combat family's alone.
func ThreatenedPatients(facts EmergencyFacts) map[domain.PawnID]bool {
	var threats []domain.Cell
	for _, t := range facts.Threats {
		if t.Building() || !ThreatHolds(t) {
			continue
		}
		if at, ok := t.Position.Value(); ok {
			threats = append(threats, at)
		}
	}
	out := map[domain.PawnID]bool{}
	for _, p := range facts.Colonists {
		at, ok := p.Position.Value()
		if !ok {
			continue
		}
		for _, t := range threats {
			if chebyshev(at, t) <= TendThreatCells {
				out[domain.PawnID(p.ID)] = true
				break
			}
		}
	}
	return out
}
