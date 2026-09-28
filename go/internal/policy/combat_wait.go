package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// raidGiveUpTicks bounds a humanoid raid's wait (#1065): raiders give up
// and leave 26k-38k ticks after they arrive (sappers 33k-38k), so a raid
// still here past the window's end is not leaving and the fight re-forms.
const raidGiveUpTicks = 38000

// outmatched is the wait-it-out comparison (#902, #1065): ours is the
// health fraction summed over the armed, eligible defenders; theirs is
// the live hostiles' body sizes. Unknown health or size counts 1.
func outmatched(view CombatView) bool {
	ours, theirs := 0.0, 0.0
	for _, d := range view.Defenders {
		if squadDefenderEligible(d) && positive(d.Armed) {
			h, ok := d.HealthFraction.Value()
			if !ok {
				h = 1
			}
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

// Extreme outdoor temperatures (#1077): past these a fight shelters
// indoors and lets hypothermia or heatstroke wear the raiders down.
const (
	extremeColdC = -20.0
	extremeHeatC = 45.0
)

// extremeWeather reports a known outdoor temperature at or past the cold
// or heat bound; unknown never shelters.
func extremeWeather(view CombatView) bool {
	t, ok := view.OutdoorTemperatureC.Value()
	return ok && (t <= extremeColdC || t >= extremeHeatC)
}

// humanoidRaid reports a fight whose live hostile pawns (at least one) are
// all known humanlike, with no pods arrival and no siege lord (those wait
// by their own tactics, #893, #776), and no complete hold-the-line layout:
// a killbox keeps fighting; only the squad fallback waits (#1065).
func humanoidRaid(view CombatView, m CombatMemory) bool {
	if _, held := view.Layout.Value(); held {
		return false
	}
	if _, pods := view.Pods.Value(); pods || m.Pods != nil || len(liveBesiegers(view)) > 0 {
		return false
	}
	down := downPawns(view)
	n := 0
	for _, t := range view.Threats {
		if t.Building || positive(t.Dead) || positive(t.Downed) || down[domain.PawnID(t.ID)] {
			continue
		}
		if !positive(t.Humanlike) {
			return false
		}
		n++
	}
	return n > 0
}

// waitTurn runs before formation (#902, #1065, #1077): it decides whether a
// manhunter fight or a humanoid raid waits this stop: outmatched, or in
// extreme outdoor cold or heat. A raid waits at most
// raidGiveUpTicks from its first waiting stop. A fight that stops waiting
// drops its roles so it re-forms to fight, and allows the doors it forbade.
func waitTurn(view CombatView, m *CombatMemory) {
	raid := humanoidRaid(view, *m)
	if !ManhunterPack(view) && !raid {
		return
	}
	wait := outmatched(view) || extremeWeather(view)
	if wait && !m.Wait {
		m.WaitSince = view.Tick
	}
	if raid && view.Tick-m.WaitSince >= raidGiveUpTicks {
		wait = false
	}
	if m.Wait && !wait {
		m.Roles = nil
		var allow []PodDoor
		for _, d := range m.WaitDoors {
			if d.Mode == DoorForbid {
				allow = append(allow, PodDoor{Cell: d.Cell, Mode: DoorAllow})
			}
		}
		m.WaitDoors = allow
	}
	m.Wait = wait
}

// shelter applies the wait after formation (#902, #1065): nobody engages
// or goes out (no kiter, potshot door, intercept, rush or lure), every
// role takes an inner-line cell when the layout has one (else holds where
// it stands), and every planned room door is closed and forbidden, so
// colony animals cannot open it and nobody paths out through it.
func shelter(view CombatView, m *CombatMemory) {
	if !m.Wait || m.Tactic == TacticShelter {
		// A fight with no armed defender already shelters (#968).
		return
	}
	m.Kiter, m.Leading, m.PotshotDoor = "", false, nil
	m.Intercept, m.Rushing, m.MechLure = false, false, false
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
	m.WaitRooms = nil
	for _, room := range view.Rooms {
		for _, d := range room.Doors {
			if !slices.ContainsFunc(want, func(p PodDoor) bool { return p.Cell == d }) {
				want = append(want, PodDoor{Cell: d, Mode: DoorClose}, PodDoor{Cell: d, Mode: DoorForbid})
				if in, ok := behindDoor(room, d); ok {
					m.WaitRooms = append(m.WaitRooms, WaitDoor{Door: d, Inside: in})
				}
			}
		}
	}
	m.WaitDoors = keepSent(m.WaitDoors, want)
}

// WaitDoor is one sheltering room's door and the room's floor cell
// directly behind it (#1065).
type WaitDoor struct{ Door, Inside domain.Cell }

// behindDoor is the room floor cell orthogonally next to door.
func behindDoor(room CombatRoom, door domain.Cell) (domain.Cell, bool) {
	for _, d := range []domain.Cell{{X: 0, Z: 1}, {X: 0, Z: -1}, {X: 1, Z: 0}, {X: -1, Z: 0}} {
		if c := (domain.Cell{X: door.X + d.X, Z: door.Z + d.Z}); room.contains(c) {
			return c, true
		}
	}
	return domain.Cell{}, false
}

// WaitDoorCell is the census of one cell the wait hardening reads: the
// edifice standing on it and its stuff.
type WaitDoorCell struct {
	Edifice, Stuff string
	Walkable       bool
}

// Wait hardening's stuff (#1065): a plasteel door holds far longer than a
// wooden one; plasteelDoorCost is a Door's stuff cost.
const (
	WaitDoorStuff    = "Plasteel"
	plasteelDoorCost = 25
)

// WaitHardening is the layout planner's builds for a waiting fight
// (#1065): each standing door of a sheltering room not already plasteel is
// rebuilt in plasteel while the stock holds 25 plasteel for it, and each
// door the census no longer shows (broken) gets a wall of wallStuff on
// the open floor cell behind it. A cell the census does not carry is left
// alone. A door takes no rotation: the game orients it between its walls.
func WaitHardening(m CombatMemory, census map[domain.Cell]WaitDoorCell, plasteel int64, door, wall, wallStuff string) ([]domain.Building, error) {
	if !m.Wait {
		return nil, nil
	}
	var out []domain.Building
	for _, w := range m.WaitRooms {
		at, ok := census[w.Door]
		if !ok {
			continue
		}
		switch {
		case at.Edifice == door && at.Stuff != WaitDoorStuff && plasteel >= plasteelDoorCost:
			b, err := domain.NewBuilding(door, w.Door, domain.North, WaitDoorStuff)
			if err != nil {
				return nil, err
			}
			plasteel -= plasteelDoorCost
			out = append(out, b)
		case at.Edifice == "":
			if in, ok := census[w.Inside]; ok && in.Edifice == "" && in.Walkable {
				b, err := domain.NewBuilding(wall, w.Inside, domain.North, wallStuff)
				if err != nil {
					return nil, err
				}
				out = append(out, b)
			}
		}
	}
	return out, nil
}
