package policy

import (
	"fmt"
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Mech control (#1687, epic #1667): a mechanitor's mechs are split by role
// into control groups and each group is given the work mode its role needs.
// Workers (a race that does work, MechKindRow.work_mech) and guards (a
// combat mech) never share a group while the overseer has two or more
// (Mechanitor wiki: "By default, a mechanitor has 2 control groups"; the
// mode is per group, so a mixed group forces one role to idle or follow).
//
// Modes, from the wiki and MechWorkModeDefOf (Assembly-CSharp 1.6.4871: Work,
// SelfShutdown, Escort, Recharge): Work does available work tasks, Escort
// follows the mechanitor and fights enemies. Recharge is chosen by
// MechRechargeMode (#1688); SelfShutdown is not chosen here.
//
// Priority between colonist need and bandwidth, decided from the read:
//   - Bandwidth is spent only by acquiring mechs (gestation, #1686); control
//     never frees it, so this plan neither dismisses nor re-prices a mech.
//     A mech with no living overseer holds no group and is left alone.
//   - Colonist need decides what the next free bandwidth buys: MechRoleNext
//     answers Worker while some work type is short of colonist owners and no
//     work mech of the mechanitor covers it, and Guard otherwise. No free
//     bandwidth means no next role.
//   - With one control group the group mode follows threat over work: Escort
//     while a hostile is on the map or when the group holds only guards,
//     Work otherwise; a raid is the one time a worker's chores yield.

// MechCommandRange is how far from its overseer a mech can be ordered
// (Mechanitor wiki: 25 tiles; the game's own check is
// MechanitorUtility.InMechanitorCommandRange, which stays authoritative).
const MechCommandRange = 25

// MechRole is what a mech kind is for.
type MechRole string

const (
	MechWorker MechRole = "worker"
	MechGuard  MechRole = "guard"
)

// MechKind is one controllable mech kind's catalog facts (bridge.BiotechCatalog).
type MechKind struct {
	Name string
	// WorkMech is the race's IsWorkMech: it does work; any other kind is a
	// combat mech.
	WorkMech      bool
	WorkTypes     []WorkType
	BandwidthCost float64
	// CombatPower is the kind's MechKindRow combat_power.
	CombatPower float64
}

// Role is worker for a work mech, guard for a combat mech.
func (k MechKind) Role() MechRole {
	if k.WorkMech {
		return MechWorker
	}
	return MechGuard
}

// MechCatalog is the Biotech defs the planner reads: kinds and work mode
// names. The zero value is a game without Biotech.
type MechCatalog struct {
	Kinds map[string]MechKind
	// Work, Escort and Recharge are the work modes the game names
	// MechWorkModeDefOf.Work, .Escort and .Recharge (the catalog rows' role
	// flags, each on exactly one row); "" without Biotech.
	Work, Escort, Recharge string
}

// MechanitorInput is one colonist mechanitor.
type MechanitorInput struct {
	ID PawnID
	PawnMechanitor
	// Cell is where the mechanitor stands; unknown plans no guard orders.
	Cell domain.Fact[domain.Cell]
}

// MechInput is one colony mech with its Biotech row.
type MechInput struct {
	ID   PawnID
	Kind string
	PawnMech
	Cell    domain.Fact[domain.Cell]
	Downed  bool
	Drafted bool
	// Target is the pawn its current attack job targets, "" for none.
	Target PawnID
}

// MechFleet is the colony's mechanitors and mechs as one read recorded them.
type MechFleet struct {
	Mechanitors []MechanitorInput
	Mechs       []MechInput
}

// PlanMechControl is the settings that bring each mechanitor's mechs into
// role groups with the right modes: first every move, then every group
// mode, so a mode lands on a group after its mechs are in it. hostile is a
// hostile on the map; chargerReady is a charger a mech can use now
// (MechRechargeMode). An unknown group count, an unknown mech group or a
// mech whose overseer is not an input mechanitor is skipped; a mech kind or
// mode the catalog lacks is an error.
func PlanMechControl(catalog MechCatalog, mechanitors []MechanitorInput, mechs []MechInput, hostile, chargerReady bool) ([]domain.PawnSettings, error) {
	for role, mode := range map[string]string{"work": catalog.Work, "escort": catalog.Escort, "recharge": catalog.Recharge} {
		if mode == "" {
			return nil, fmt.Errorf("mech %s work mode is not in the catalog", role)
		}
	}
	var moves, modes []domain.PawnSettings
	for _, m := range sortedMechanitors(mechanitors) {
		groups, ok := m.ControlGroups.Value()
		if !ok || groups < 1 {
			continue
		}
		var workers, guards []MechInput
		for _, mech := range mechs {
			if mech.Overseer != string(m.ID) {
				continue
			}
			kind, ok := catalog.Kinds[mech.Kind]
			if !ok {
				return nil, fmt.Errorf("mech %s kind %s is not in the catalog", mech.ID, mech.Kind)
			}
			if _, known := mech.ControlGroup.Value(); !known {
				continue
			}
			if kind.Role() == MechWorker {
				workers = append(workers, mech)
			} else {
				guards = append(guards, mech)
			}
		}
		sortMechs(workers)
		sortMechs(guards)
		var assigned []mechGroup
		if groups == 1 {
			mode := catalog.Work
			if hostile || len(workers) == 0 {
				mode = catalog.Escort
			}
			assigned = []mechGroup{{index: 0, mode: mode, mechs: append(slices.Clone(workers), guards...)}}
		} else {
			workerGroup := busiestGroup(workers, -1, groups)
			guardGroup := busiestGroup(guards, workerGroup, groups)
			if len(workers) > 0 {
				assigned = append(assigned, mechGroup{index: workerGroup, mode: catalog.Work, mechs: workers})
			}
			if len(guards) > 0 {
				assigned = append(assigned, mechGroup{index: guardGroup, mode: catalog.Escort, mechs: guards})
			}
		}
		for _, g := range assigned {
			for _, mech := range g.mechs {
				if current, _ := mech.ControlGroup.Value(); current == g.index {
					continue
				}
				s, err := domain.NewMechControlGroupSetting(domain.PawnID(mech.ID), g.index)
				if err != nil {
					return nil, err
				}
				moves = append(moves, s)
			}
			g.mode = MechRechargeMode(catalog, g, mechs, chargerReady)
			if g.holdsMode(mechs) {
				continue
			}
			s, err := domain.NewMechWorkModeSetting(domain.PawnID(g.mechs[0].ID), g.mode)
			if err != nil {
				return nil, err
			}
			modes = append(modes, s)
		}
	}
	return append(moves, modes...), nil
}

type mechGroup struct {
	index int
	mode  string
	mechs []MechInput
}

// holdsMode reports whether the group already runs the mode: a mech now in
// the group has a known mode and every one that is has the wanted one. An
// empty group, or one with an unread mode, is written.
func (g mechGroup) holdsMode(all []MechInput) bool { return g.holds(all, g.mode) }

// holds is holdsMode for an arbitrary mode.
func (g mechGroup) holds(all []MechInput, wanted string) bool {
	seen := false
	for _, mech := range all {
		if group, ok := mech.ControlGroup.Value(); !ok || group != g.index {
			continue
		}
		mode, ok := mech.WorkMode.Value()
		if !ok || mode != wanted {
			return false
		}
		seen = true
	}
	return seen
}

// busiestGroup is the group index holding most of the mechs, lowest on a
// tie; with none, the lowest index that is not skip.
func busiestGroup(mechs []MechInput, skip, groups int) int {
	count := map[int]int{}
	for _, mech := range mechs {
		if g, ok := mech.ControlGroup.Value(); ok && g != skip && g < groups {
			count[g]++
		}
	}
	best := -1
	for g := 0; g < groups; g++ {
		if g != skip && (best < 0 || count[g] > count[best]) {
			best = g
		}
	}
	return best
}

func sortedMechanitors(in []MechanitorInput) []MechanitorInput {
	out := slices.Clone(in)
	sort.Slice(out, func(i, j int) bool { return out[i].ID < out[j].ID })
	return out
}

func sortMechs(in []MechInput) {
	sort.Slice(in, func(i, j int) bool { return in[i].ID < in[j].ID })
}

// MechRoleNext is the role the mechanitor's next free bandwidth should buy,
// by colonist need first (see the package note above): Worker while a work
// type has fewer colonist owners than demand and none of the mechanitor's
// work mechs covers it, Guard otherwise. ok is false with no free
// bandwidth or an unread budget.
func MechRoleNext(catalog MechCatalog, m MechanitorInput, mechs []MechInput, coverage []WorkCoverage) (role MechRole, ok bool, err error) {
	used, uk := m.UsedBandwidth.Value()
	total, tk := m.TotalBandwidth.Value()
	if !uk || !tk || total-used <= 0 {
		return "", false, nil
	}
	short, err := shortUncoveredWork(catalog, m, mechs, coverage)
	if err != nil {
		return "", false, err
	}
	for work := range short {
		for _, kind := range catalog.Kinds {
			if kind.WorkMech && slices.Contains(kind.WorkTypes, work) {
				return MechWorker, true, nil
			}
		}
	}
	return MechGuard, true, nil
}

// shortUncoveredWork is the work types with fewer colonist owners than
// demand that none of the mechanitor's work mechs covers.
func shortUncoveredWork(catalog MechCatalog, m MechanitorInput, mechs []MechInput, coverage []WorkCoverage) (map[WorkType]bool, error) {
	covered := map[WorkType]bool{}
	for _, mech := range mechs {
		if mech.Overseer != string(m.ID) {
			continue
		}
		kind, known := catalog.Kinds[mech.Kind]
		if !known {
			return nil, fmt.Errorf("mech %s kind %s is not in the catalog", mech.ID, mech.Kind)
		}
		for _, w := range kind.WorkTypes {
			covered[w] = true
		}
	}
	short := map[WorkType]bool{}
	for _, c := range coverage {
		if c.Owners < c.Demand && !covered[c.Work] {
			short[c.Work] = true
		}
	}
	return short, nil
}

// MechHostile is a hostile a guard may be ordered at.
type MechHostile struct {
	ID   PawnID
	Cell domain.Cell
}

// MechGuardPlan is the combat writes for the guard mechs: the drafts, then
// the attack orders, in the existing combat batch's shapes.
type MechGuardPlan struct {
	Drafts []domain.PawnID
	Orders []CombatOrder
}

// PlanMechGuards orders every standing guard mech at the hostile nearest to
// it among those within MechCommandRange of its overseer, drafting it first
// when it is not. A guard already attacking that hostile, a downed guard, a
// guard whose overseer or position is unread or is no input mechanitor, and
// a hostile out of the overseer's range are left alone.
func PlanMechGuards(catalog MechCatalog, mechanitors []MechanitorInput, mechs []MechInput, hostiles []MechHostile) (MechGuardPlan, error) {
	var plan MechGuardPlan
	overseers := map[string]domain.Cell{}
	for _, m := range mechanitors {
		if cell, ok := m.Cell.Value(); ok {
			overseers[string(m.ID)] = cell
		}
	}
	ordered := slices.Clone(mechs)
	sortMechs(ordered)
	for _, mech := range ordered {
		kind, ok := catalog.Kinds[mech.Kind]
		if !ok {
			return MechGuardPlan{}, fmt.Errorf("mech %s kind %s is not in the catalog", mech.ID, mech.Kind)
		}
		home, hok := overseers[mech.Overseer]
		at, aok := mech.Cell.Value()
		if kind.Role() != MechGuard || mech.Downed || !hok || !aok {
			continue
		}
		var best *MechHostile
		for i := range hostiles {
			h := &hostiles[i]
			if cellDistSq(home, h.Cell) > MechCommandRange*MechCommandRange {
				continue
			}
			if best == nil || cellDistSq(at, h.Cell) < cellDistSq(at, best.Cell) || cellDistSq(at, h.Cell) == cellDistSq(at, best.Cell) && h.ID < best.ID {
				best = h
			}
		}
		if best == nil || mech.Target == best.ID {
			continue
		}
		if !mech.Drafted {
			plan.Drafts = append(plan.Drafts, domain.PawnID(mech.ID))
		}
		plan.Orders = append(plan.Orders, CombatOrder{Pawn: domain.PawnID(mech.ID), Kind: OrderAttack, Target: domain.PawnID(best.ID), Reason: ReasonFormation})
	}
	return plan, nil
}

func cellDistSq(a, b domain.Cell) int64 {
	dx, dz := int64(a.X-b.X), int64(a.Z-b.Z)
	return dx*dx + dz*dz
}
