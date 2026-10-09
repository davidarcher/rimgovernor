package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// CombatBatchObserved recognizes only current observable settings/positions.
// A historical attack, dose, or accepted job is never invented from a later frame.
func CombatBatchObserved(batch domain.CombatBatch, view CombatView) bool {
	pawns := map[domain.PawnID]CombatPawnState{}
	for _, p := range view.Pawns {
		pawns[p.ID] = p
	}
	doors, doorsKnown := view.DoorStates.Value()
	for _, order := range batch.Orders() {
		p, present := pawns[order.Pawn]
		switch order.Kind {
		case "draft":
			found := false
			for _, id := range view.Orderable {
				found = found || id == order.Pawn
			}
			if !found {
				return false
			}
		case "move":
			cell, known := p.Cell.Value()
			if !present || !known || cell != order.Cell {
				return false
			}
		case "fire_mode":
			if !present || p.FireMode == "" || p.FireMode != order.FireMode {
				return false
			}
		case "door":
			if !doorsKnown {
				return false
			}
			found := false
			for _, d := range doors {
				if d.Cell != order.Cell {
					continue
				}
				found = true
				switch order.Door {
				case "hold_open", "close":
					v, k := d.HoldOpen.Value()
					if !k || v != (order.Door == "hold_open") {
						return false
					}
				case "forbid", "allow":
					v, k := d.Forbidden.Value()
					if !k || v != (order.Door == "forbid") {
						return false
					}
				default:
					return false
				}
			}
			if !found {
				return false
			}
		default:
			return false
		}
	}
	return true
}
