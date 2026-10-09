package policy

import (
	"math"
	"slices"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

const (
	// empAdaptAfterStun is how long a mech stays EMP-adapted once its stun
	// ends: vanilla adapts it for 2200 ticks from the hit, and an EMP
	// grenade (50 damage x 30 ticks) stuns it for 1500.
	empAdaptAfterStun = 700
	// empDisengageTicks is how early blockers leave a stunned mech: time
	// to step back to the inner line before it wakes.
	empDisengageTicks = 120
	// empChokeReach is how near (Chebyshev) the choke a scyther counts as
	// in it; empNearBlocker how near a blocker a mech counts as on it.
	empChokeReach  = 2
	empNearBlocker = 2
)

// EMPAdaptation is a mech EMP no longer stuns until Until.
type EMPAdaptation struct {
	Pawn  domain.PawnID
	Until domain.Tick
}

// noteEMPAdapted records every hostile mech seen stunned, until its EMP
// adaptation ends, and drops the expired records. A mech already recorded
// keeps its first Until: the stun it is adapted to is the first.
func noteEMPAdapted(view CombatView, m *CombatMemory) {
	m.EMPAdapted = slices.DeleteFunc(m.EMPAdapted, func(a EMPAdaptation) bool { return a.Until <= view.Tick })
	for _, h := range rankThreats(view) {
		if !isMech(h) || h.StunTicks <= 0 || empAdapted(*m, h.ID) {
			continue
		}
		m.EMPAdapted = append(m.EMPAdapted, EMPAdaptation{Pawn: h.ID, Until: view.Tick + domain.Tick(h.StunTicks+empAdaptAfterStun)})
	}
	slices.SortFunc(m.EMPAdapted, func(a, b EMPAdaptation) int { return strings.Compare(string(a.Pawn), string(b.Pawn)) })
}

func empAdapted(m CombatMemory, id domain.PawnID) bool {
	return slices.ContainsFunc(m.EMPAdapted, func(a EMPAdaptation) bool { return a.Pawn == id })
}

// grenadeAim is the grenade step's ground cell. Against mechs an EMP only
// stuns scythers already in the choke, awake and not adapted; the
// blockers hold the choke, so when every hostile cell is too near them it
// aims at any cell whose blast still reaches such a scyther. Otherwise it
// is GrenadeTarget.
func grenadeAim(view CombatView, m CombatMemory, carrier CombatPawnState, hostiles []CombatPawnState, colonists []domain.Cell) (domain.Cell, bool) {
	if !carrier.WeaponFacts.EMP || !slices.ContainsFunc(hostiles, isMech) {
		return GrenadeTarget(carrier, hostiles, colonists)
	}
	layout, ok := view.Layout.Value()
	choke, cok := layout.Choke.Value()
	from, fok := carrier.Cell.Value()
	blast := carrier.WeaponFacts.Blast
	bok := blast > 0
	if !ok || !cok || !fok || !bok {
		return domain.Cell{}, false
	}
	var scythers []domain.Cell
	for _, h := range hostiles {
		c, known := h.Cell.Value()
		if known && !h.Dead && !h.Downed && strings.HasPrefix(h.Kind, "Mech_Scyther") && h.StunTicks == 0 &&
			!empAdapted(m, h.ID) && chebyshev(c, choke) <= empChokeReach {
			scythers = append(scythers, c)
		}
	}
	if len(scythers) == 0 {
		return domain.Cell{}, false
	}
	reach := grenadeReach(carrier)
	if c, ok := bestGround(from, reach, blast, scythers, scythers, colonists); ok {
		return c, true
	}
	r := int32(math.Floor(blast))
	var around []domain.Cell
	for _, s := range scythers {
		for dx := -r; dx <= r; dx++ {
			for dz := -r; dz <= r; dz++ {
				around = append(around, domain.Cell{X: s.X + dx, Z: s.Z + dz})
			}
		}
	}
	return bestGround(from, reach, blast, around, scythers, colonists)
}

// mechDisengage pulls each blocker back to the inner line before a
// mech on it wakes from a stun: the stun ends within
// empDisengageTicks, or it already woke EMP-adapted, when a second EMP
// would not hold it. A pulled-back blocker stays on the inner line.
func mechDisengage(view CombatView, m *CombatMemory) {
	layout, ok := view.Layout.Value()
	if !ok || len(layout.Retreat) == 0 || len(m.EMPAdapted) == 0 {
		return
	}
	var waking []domain.Cell
	for _, h := range rankThreats(view) {
		c, known := h.Cell.Value()
		if known && isMech(h) && empAdapted(*m, h.ID) && h.StunTicks <= empDisengageTicks {
			waking = append(waking, c)
		}
	}
	if len(waking) == 0 {
		return
	}
	taken := map[domain.Cell]bool{}
	for _, r := range m.Roles {
		if r.Retreat && r.Cell != nil {
			taken[*r.Cell] = true
		}
	}
	for i := range m.Roles {
		r := &m.Roles[i]
		if r.Duty != DutyBlocker || r.Retreat || r.Cell == nil ||
			!slices.ContainsFunc(waking, func(c domain.Cell) bool { return chebyshev(c, *r.Cell) <= empNearBlocker }) {
			continue
		}
		if cell, ok := retreatCell(layout, r.Cell, taken); ok {
			r.Cell, r.Retreat, r.Ground, r.Target = &cell, true, nil, ""
		}
	}
}
