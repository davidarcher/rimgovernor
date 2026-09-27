package policy

import (
	"math"
	"slices"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// OrderMortar sends a drafted pawn to man a colony mortar that fires at a
// cell (#931).
const OrderMortar CombatOrderKind = "mortar"

// ReasonCounterBattery is a mortar order (#931).
const ReasonCounterBattery CombatOrderReason = "counter_battery"

// CombatMortar is an unroofed player mortar the fight may crew (#931),
// with its verb's range in cells.
type CombatMortar struct {
	ID                 string
	Cell               domain.Cell
	MinRange, MaxRange float64
}

// HostileStructure is a standing hostile building in the census (#930,
// #931): a crashed ship part, a mech-cluster piece, a siege or mech
// mortar, a hive. Def is its ThingDef; Cell one occupied cell.
type HostileStructure struct {
	ID   domain.PawnID
	Def  string
	Cell domain.Cell
}

// enemyMortar and hive classify a structure by def: Turret_Mortar (a
// siege's) and Turret_AutoMortar (a mech cluster's) are mortars.
func (s HostileStructure) enemyMortar() bool { return strings.Contains(s.Def, "Mortar") }
func (s HostileStructure) hive() bool        { return s.Def == "Hive" }

// fromRange drops a melee target on a hostile structure other than a hive
// (#930): a crashed ship part and its cluster buildings are destroyed
// from range, so the mechs they wake walk into the line instead of
// catching a brawler at the part. Gunners keep theirs.
func fromRange(view CombatView, m *CombatMemory) {
	far := map[domain.PawnID]bool{}
	for _, s := range view.Structures {
		far[s.ID] = !s.hive()
	}
	for i := range m.Roles {
		if !m.Roles[i].Ranged && far[m.Roles[i].Target] {
			m.Roles[i].Target = ""
		}
	}
}

// counterBattery crews each colony mortar with an enemy mortar, else a
// non-hive structure, in its range (#931, #930): the enemy's mortars
// first (a siege's or a mech cluster's), then the nearest. The crew is
// the pawn that manned it at the last stop while still orderable, else
// the nearest orderable free pawn; its role keeps its cell and target
// for when the mortar has nothing to shoot.
func counterBattery(view CombatView, m *CombatMemory) {
	crewed := map[domain.Cell]domain.PawnID{}
	for i := range m.Roles {
		if r := &m.Roles[i]; r.Mortar != nil {
			crewed[*r.Mortar] = r.Pawn
			r.Mortar, r.Aim = nil, nil
		}
	}
	orderable := map[domain.PawnID]bool{}
	for _, id := range view.Orderable {
		orderable[id] = true
	}
	live := view.live()
	cells := map[domain.PawnID]domain.Cell{}
	for _, p := range view.Pawns {
		if c, ok := p.Cell.Value(); ok {
			cells[p.ID] = c
		}
	}
	busy := map[domain.PawnID]bool{}
	mortars := slices.Clone(view.Mortars)
	sort.Slice(mortars, func(i, j int) bool { return mortars[i].ID < mortars[j].ID })
	for _, mortar := range mortars {
		aim, ok := mortarAim(view, mortar)
		if !ok {
			continue
		}
		free := func(id domain.PawnID) bool {
			return orderable[id] && live[id] && !busy[id] && !m.Rescue.carrying(id)
		}
		crew := crewed[mortar.Cell]
		if !free(crew) {
			crew = ""
			best := math.MaxFloat64
			for _, id := range view.Orderable {
				c, known := cells[id]
				if !free(id) || !known {
					continue
				}
				if d := dist(c, mortar.Cell); d < best {
					crew, best = id, d
				}
			}
		}
		if crew == "" {
			continue
		}
		busy[crew] = true
		cell, target := mortar.Cell, aim
		i := slices.IndexFunc(m.Roles, func(r CombatRole) bool { return r.Pawn == crew })
		if i < 0 {
			m.Roles = append(m.Roles, CombatRole{Pawn: crew})
			i = len(m.Roles) - 1
		}
		m.Roles[i].Mortar, m.Roles[i].Aim = &cell, &target
	}
	m.Roles = sortRoles(m.Roles)
}

// mortarAim is the cell mortar fires at: the nearest enemy mortar in its
// range, else the nearest other non-hive structure in range.
func mortarAim(view CombatView, mortar CombatMortar) (domain.Cell, bool) {
	var best *HostileStructure
	for i, s := range view.Structures {
		d := dist(s.Cell, mortar.Cell)
		if s.hive() || d < mortar.MinRange || d > mortar.MaxRange {
			continue
		}
		if best == nil || s.enemyMortar() && !best.enemyMortar() ||
			s.enemyMortar() == best.enemyMortar() && d < dist(best.Cell, mortar.Cell) {
			best = &view.Structures[i]
		}
	}
	if best == nil {
		return domain.Cell{}, false
	}
	return best.Cell, true
}

func dist(a, b domain.Cell) float64 {
	return math.Hypot(float64(a.X-b.X), float64(a.Z-b.Z))
}
