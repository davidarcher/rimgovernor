package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// manhunterOutmatched is the wait-it-out comparison (#902): ours is the
// health fraction summed over the armed, eligible defenders; the pack's
// is its live animals' body sizes (unknown counts 1).
func manhunterOutmatched(view CombatView) bool {
	ours, theirs := 0.0, 0.0
	for _, d := range view.Defenders {
		if squadDefenderEligible(d) && positive(d.Armed) {
			h, _ := d.HealthFraction.Value()
			ours += h
		}
	}
	down := downPawns(view)
	for _, t := range view.Threats {
		if t.Building || positive(t.Dead) || positive(t.Downed) || down[domain.PawnID(t.ID)] {
			continue
		}
		size, ok := t.BodySize.Value()
		if !ok || size <= 0 {
			size = 1
		}
		theirs += size
	}
	return ours < theirs
}

// manhunterWaitTurn runs before formation (#902): it decides whether a
// manhunter fight waits this stop. A fight that stops waiting (the
// comparison flipped) drops its roles so it re-forms to fight, and allows
// the doors it forbade.
func manhunterWaitTurn(view CombatView, m *CombatMemory) {
	if !ManhunterPack(view) {
		return
	}
	wait := manhunterOutmatched(view)
	if m.ManhunterWait && !wait {
		m.Roles = nil
		var allow []PodDoor
		for _, d := range m.WaitDoors {
			if d.Mode == DoorForbid {
				allow = append(allow, PodDoor{Cell: d.Cell, Mode: DoorAllow})
			}
		}
		m.WaitDoors = allow
	}
	m.ManhunterWait = wait
}

// manhunterShelter applies the wait after formation (#902): nobody
// engages, every role takes an inner-line cell when the layout has one
// (else holds where it stands), no kiter or potshot door, and every
// planned room door is closed and forbidden, so colony animals cannot
// open it. The hold lasts until the pack is gone or the comparison flips.
func manhunterShelter(view CombatView, m *CombatMemory) {
	if !m.ManhunterWait || m.Tactic != TacticManhunter {
		return
	}
	m.Kiter, m.Leading, m.PotshotDoor = "", false, nil
	var inner []domain.Cell
	if layout, ok := view.Layout.Value(); ok {
		inner = slices.Clone(layout.Retreat)
	}
	at := map[domain.PawnID]domain.Cell{}
	for _, p := range view.Pawns {
		if c, ok := p.Cell.Value(); ok {
			at[p.ID] = c
		}
	}
	for i := range m.Roles {
		r := &m.Roles[i]
		r.Target, r.Duty, r.Cell, r.Retreat = "", "", nil, false
		if len(inner) > 0 {
			k := nearestIndex(at[r.Pawn], inner)
			c := inner[k]
			inner = slices.Delete(inner, k, k+1)
			r.Cell, r.Retreat = &c, true
		}
	}
	var want []PodDoor
	for _, room := range view.Rooms {
		for _, d := range room.Doors {
			if !slices.ContainsFunc(want, func(p PodDoor) bool { return p.Cell == d }) {
				want = append(want, PodDoor{Cell: d, Mode: DoorClose}, PodDoor{Cell: d, Mode: DoorForbid})
			}
		}
	}
	m.WaitDoors = keepSent(m.WaitDoors, want)
}
