package policy

import (
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// TacticHunt is a squad hunt (#1616): the view's threats are wild prey the
// threat census does not call hostile, and the squad stages in a half circle
// at weapon range and focus-fires them down. Once the prey are all manhunter
// the fight is the manhunter tactic's (#898).
const TacticHunt CombatTactic = "hunt"

// huntDefaultRange is the staging reach when no gunner's weapon range is
// known, in cells.
const huntDefaultRange = 20

// huntFormation puts every armed ranged defender on a half circle around the
// prey's centre, at the shortest gunner's weapon range so each can reach the
// group, on the squad's side of it so no one shoots across a friend. Targets
// are focusFire's. Melee defenders take no part.
func huntFormation(view CombatView) []CombatRole {
	prey := rankThreats(view)
	state := map[domain.PawnID]CombatPawnState{}
	for _, p := range view.Pawns {
		state[p.ID] = p
	}
	// A gunner whose weapon is outranged by any prey stays out of the squad.
	var preyRange float64
	for _, p := range prey {
		preyRange = math.Max(preyRange, p.WeaponRange)
	}
	var gunners []SquadDefenderFacts
	for _, d := range view.Defenders {
		if r := state[d.ID].WeaponRange; r > 0 && r < preyRange {
			continue
		}
		if squadDefenderEligible(d) && positive(d.Armed) && positive(d.RangedEquipped) {
			gunners = append(gunners, d)
		}
	}
	if len(gunners) < SquadHuntMinGunners || len(prey) == 0 {
		return nil
	}
	// A larger roster keeps its other members working.
	gunners = gunners[:min(len(gunners), SquadHuntMaxGunners)]
	top := prey[0].ID
	var sum domain.Cell
	known := int32(0)
	for _, p := range prey {
		if c, ok := p.Cell.Value(); ok {
			sum.X, sum.Z = sum.X+c.X, sum.Z+c.Z
			known++
		}
	}
	roles := make([]CombatRole, 0, len(gunners))
	if known == 0 {
		for _, g := range gunners {
			roles = append(roles, CombatRole{Pawn: g.ID, Target: top, Ranged: true})
		}
		return sortRoles(roles)
	}
	centre := domain.Cell{X: sum.X / known, Z: sum.Z / known}
	reach := math.MaxFloat64
	for _, g := range gunners {
		if r := state[g.ID].WeaponRange; r > 0 {
			reach = math.Min(reach, r)
		}
	}
	if reach == math.MaxFloat64 {
		reach = huntDefaultRange
	}
	// Every prey stays within reach of every cell: the radius gives up the
	// group's own spread.
	spread := 0.0
	for _, p := range prey {
		if c, ok := p.Cell.Value(); ok {
			spread = math.Max(spread, math.Hypot(float64(c.X-centre.X), float64(c.Z-centre.Z)))
		}
	}
	radius := math.Max(1, math.Floor(reach-spread))
	// The axis points from the prey to the squad's side.
	var sx, sz float64
	for _, g := range gunners {
		if c, ok := state[g.ID].Cell.Value(); ok {
			sx, sz = sx+float64(c.X-centre.X), sz+float64(c.Z-centre.Z)
		}
	}
	axis := math.Atan2(sz, sx)
	if sx == 0 && sz == 0 {
		axis = math.Pi / 2
	}
	// Gunners sorted by where they stand around the axis take the arc's
	// cells in order, so their walks do not cross.
	rel := func(id domain.PawnID) float64 {
		c, ok := state[id].Cell.Value()
		if !ok {
			return 0
		}
		a := math.Atan2(float64(c.Z-centre.Z), float64(c.X-centre.X)) - axis
		return math.Atan2(math.Sin(a), math.Cos(a))
	}
	sort.SliceStable(gunners, func(i, j int) bool { return rel(gunners[i].ID) < rel(gunners[j].ID) })
	for i, g := range gunners {
		offset := 0.0
		if len(gunners) > 1 {
			offset = -math.Pi/2 + math.Pi*float64(i)/float64(len(gunners)-1)
		}
		a := axis + offset
		cell := domain.Cell{X: centre.X + int32(math.Round(radius*math.Cos(a))), Z: centre.Z + int32(math.Round(radius*math.Sin(a)))}
		roles = append(roles, CombatRole{Pawn: g.ID, Cell: &cell, Target: top, Ranged: true})
	}
	return sortRoles(roles)
}

// reformHunt is the hunt's re-formation row: a role's target is down, or the
// prey turned into a manhunter pack.
func reformHunt(view CombatView, m CombatMemory) bool {
	return squadTargetDown(view, m) || ManhunterPack(view)
}
