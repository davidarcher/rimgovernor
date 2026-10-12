package policy

import (
	"errors"
	"fmt"
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The defense tiers as plan nodes (#2973). PlanDefenseTiers cuts the killbox
// node into the tiers that stand in it: the corridor and its trap lane, the
// funnel walls, the fence bar, the firing line and the shooters' floor, the
// spike traps, the turrets, the mortars, the IEDs and the bait. Each is a
// PlannedSite whose Tier facts name the node it stands under (the killbox),
// the tier of the defense record it belongs to and the placements it holds.
//
// Where a tier stands depends on live cover, trees and standing buildings, so
// the nodes are a function of the plan and a census, not of the survey: they
// are re-sited from every census and are not part of the stored plan. A tier
// that is built is pinned, as replanPins pins a room that holds anything of
// ours: its node is the built tier where it stands, and the tiers still to
// build are sited around it. The census is an explicit input.

const (
	// NodeDefCorridor is the passage raiders walk from the opening to the kill
	// row; NodeDefLane the ordered snake path inside it.
	NodeDefCorridor NodeKind = "defense_corridor"
	NodeDefLane     NodeKind = "defense_trap_lane"
	NodeDefFunnel   NodeKind = "defense_funnel"
	// NodeDefFence is the fence bar across the kill zone, NodeDefTraps the
	// spike traps on the lane's turns.
	NodeDefFence NodeKind = "defense_kill_fence"
	NodeDefTraps NodeKind = "defense_spike_traps"
	// NodeDefFiring is the cover of the firing line and NodeDefShooter the
	// floor under each shooter.
	NodeDefFiring  NodeKind = "defense_firing_line"
	NodeDefShooter NodeKind = "defense_shooter_floor"
	NodeDefTurrets NodeKind = "defense_turrets"
	NodeDefMortars NodeKind = "defense_mortars"
	NodeDefIEDs    NodeKind = "defense_ieds"
	NodeDefBait    NodeKind = "defense_bait"
)

// defenseTierKinds are the nodes PlanDefenseTiers produces.
var defenseTierKinds = map[NodeKind]bool{NodeDefCorridor: true, NodeDefLane: true, NodeDefFunnel: true, NodeDefFence: true, NodeDefTraps: true,
	NodeDefFiring: true, NodeDefShooter: true, NodeDefTurrets: true, NodeDefMortars: true, NodeDefIEDs: true, NodeDefBait: true}

// IsDefenseTierNode reports a node PlanDefenseTiers produced.
func IsDefenseTierNode(kind NodeKind) bool { return defenseTierKinds[kind] }

// Why a tier node stands where it does; the caller journals them as
// layout_plan rows (target defense_tiers).
const (
	// ReasonTierSited: the tier is sited from the census.
	ReasonTierSited = "tier_sited"
	// ReasonTierPinned: the tier is built, so it stays where it stands.
	ReasonTierPinned = "tier_pinned"
	// ReasonTierLayoutFailed: the census holds no corridor (the layout
	// planner's refusal is the attr); only the built tiers stand as nodes.
	ReasonTierLayoutFailed = "tier_layout_failed"
	// ReasonTierDisplaced: a tier still to build was sited on a cell a built
	// tier holds, and gave it up.
	ReasonTierDisplaced = "tier_displaced_by_built"
)

// TierPlacement is one building a tier node places.
type TierPlacement struct {
	Definition string
	Cell       domain.Cell
	Rotation   domain.Rotation
	Stuff      string
}

// Building is the placement as the domain building.
func (p TierPlacement) Building() (domain.Building, error) {
	return domain.NewBuilding(p.Definition, p.Cell, p.Rotation, p.Stuff)
}

// TierFacts are a defense tier node's own attributes.
type TierFacts struct {
	// Under is the killbox node the tier stands under and Tier the defense
	// tier it is cut from (a tier of several nodes: the firing line and its
	// floor, the trap corridor's traps and fence).
	Under string
	Tier  DefenseTierName
	// Places are the buildings the node places, in the layout's order.
	Places []TierPlacement `json:",omitempty"`
	// Reserved are the cells the tier holds against routine construction;
	// the tier's first node carries them.
	Reserved []domain.Cell `json:",omitempty"`
	// Path is the trap lane in raider order, the exit cell last (the lane
	// node only).
	Path []domain.Cell `json:",omitempty"`
	// Firing are the shooters with their cover and retreat cells (the firing
	// line node only).
	Firing []FiringPosition `json:",omitempty"`
	// Pinned marks a node kept where it stands because the tier is built.
	Pinned bool `json:",omitempty"`
}

// DefenseTierPins are the tiers of the defense record that are built: kept
// where they stand while the tiers still to build are sited around them.
type DefenseTierPins struct {
	// Tiers are the built tiers' placements and reserved cells.
	Tiers map[DefenseTierName]DefenseTier
	// TrapLane and Firing are the geometry of the built corridor and firing
	// line, used while the chokepoint and firing line tiers are pinned.
	TrapLane []domain.Cell
	Firing   []FiringPosition
}

// DefenseTiers is what PlanDefenseTiers cut.
type DefenseTiers struct {
	Nodes []PlannedSite
	// Layout is the tier geometry the nodes were cut from: its firing line,
	// trap lane, approaches and the gates that held tiers short.
	Layout DefenseLayout
	// Refusal is why the layout planner sited nothing, when it refused.
	Refusal   string
	Decisions []SiteDecision
	// Sited are the tiers the census sites with nothing pinned: the places a
	// blocked tier could move to (PriceTierBlocks).
	Sited []DefenseTier
}

// defenseTierOrder is the order tiers become nodes in, and so the order of
// the nodes in the plan.
var defenseTierOrder = []DefenseTierName{TierChokepoint, TierFunnel, TierTrapCorridor, TierFiringLine, TierTurrets, TierMortars, TierIEDs, TierBait}

// WithDefenseTiers is the plan with its defense tier nodes replaced by the
// given ones. The result is for drawing and reading; it is not stored.
func (p LayoutPlan) WithDefenseTiers(nodes []PlannedSite) LayoutPlan {
	kinds := make([]NodeKind, 0, len(defenseTierKinds))
	for k := range defenseTierKinds {
		kinds = append(kinds, k)
	}
	p = p.withoutSites(kinds...)
	p.Sites = append(slices.Clone(p.Sites), nodes...)
	return p
}

// PlanDefenseTiers cuts the plan's killbox into the tier nodes standing in it,
// sited from the census. census carries the cells (any window of the map; the
// killbox's region is taken from the plan), the defenders, the definitions and
// the turret, mortar and IED gates; its Killbox, Home and Region are the
// plan's. pins are the built tiers. Deterministic: a census and pins give one
// node set. An error means the plan has no killbox opening; a census the
// layout planner refuses leaves the pinned tiers as the nodes.
func PlanDefenseTiers(plan LayoutPlan, census DefenseRequest, pins DefenseTierPins) (DefenseTiers, error) {
	id, _, ok := KillboxNode(plan)
	kb, region, home, found := LayoutKillbox(plan, census.Bounds)
	if !ok || !found {
		return DefenseTiers{}, errors.New("plan has no killbox opening")
	}
	req := census
	req.Killbox, req.Home, req.Region = kb, home, region
	req.Entrances = nil
	for _, c := range census.Entrances {
		if contains(region, c) {
			req.Entrances = append(req.Entrances, c)
		}
	}
	req.Cells = pinnedCensus(census.Cells, region, pins)

	var out DefenseTiers
	layout, err := DefenseLayouts(req)
	if err != nil {
		layout = DefenseLayout{Toward: kb.Toward, Width: int(kb.Width), Entry: kb.Entry}
		out.Decisions = append(out.Decisions, SiteDecision{Node: id, Verdict: EmbedUnplaceable, Reason: ReasonTierLayoutFailed})
		out.Refusal = err.Error()
	}
	out.Sited = slices.Clone(layout.Tiers)
	pinned := applyPins(&layout, pins)
	if out.Layout = layout; len(pinned) == 0 && err != nil {
		return out, nil
	}
	displaced := displaceBuilt(&layout, pinned)
	if _, hasMortars := pinned[TierMortars]; !hasMortars {
		geometry := layout.Geometry()
		for _, t := range layout.Tiers {
			if t.Name != TierTurrets {
				continue
			}
			geometry.Turrets = append(geometry.Turrets, t.Reserved...)
			for _, b := range t.Buildings {
				geometry.Turrets = append(geometry.Turrets, b.Cell())
			}
		}
		if mortars, merr := DefenseMortars(req, geometry); merr == nil && len(mortars.Buildings) > 0 {
			layout.Tiers = append(layout.Tiers, mortars)
		}
	}
	out.Layout = layout
	out.Nodes = tierNodes(id, req.Definitions, layout, pinned)
	for _, n := range out.Nodes {
		reason := ReasonTierSited
		if n.Tier.Pinned {
			reason = ReasonTierPinned
		}
		out.Decisions = append(out.Decisions, SiteDecision{Node: n.Node, Verdict: EmbedPlace, Reason: reason})
	}
	for _, name := range displaced {
		out.Decisions = append(out.Decisions, SiteDecision{Node: id + "-" + string(name), Verdict: EmbedPlace, Reason: ReasonTierDisplaced})
	}
	return out, nil
}

// pinnedCensus is the census window with the built tiers' standing buildings
// read as the ground they stand on: free, walkable and uncovered, so the same
// census that sited the tier sites it on the same cells (siting against a
// census that holds its own walls shifts the corridor by a cell). Cells
// outside region drop.
func pinnedCensus(cells []DefenseCell, region Rectangle, pins DefenseTierPins) []DefenseCell {
	standing := map[domain.Cell]string{}
	for _, t := range pins.Tiers {
		for _, b := range t.Buildings {
			standing[b.Cell()] = b.Definition()
		}
	}
	out := make([]DefenseCell, 0, len(cells))
	for _, c := range cells {
		if !contains(region, c.Cell) {
			continue
		}
		if def, ok := standing[c.Cell]; ok && c.Edifice == def {
			c.Edifice = ""
			c.PlayerOwned, c.Door = domain.Known(false), domain.Known(false)
			c.Walkable, c.Passable, c.CoverFill = domain.Known(true), domain.Known(true), domain.Known(0.0)
		}
		out = append(out, c)
	}
	return out
}

// applyPins replaces the layout's tiers by the pinned ones (appending a built
// tier the census no longer sites) and returns the pinned tiers with the
// cells they hold. The pinned corridor and firing line keep their geometry.
func applyPins(layout *DefenseLayout, pins DefenseTierPins) map[DefenseTierName]DefenseTier {
	pinned := map[DefenseTierName]DefenseTier{}
	names := make([]DefenseTierName, 0, len(pins.Tiers))
	for name := range pins.Tiers {
		names = append(names, name)
	}
	sort.Slice(names, func(i, j int) bool { return names[i] < names[j] })
	for _, name := range names {
		tier := pins.Tiers[name]
		tier.Name = name
		pinned[name] = tier
		if i := slices.IndexFunc(layout.Tiers, func(t DefenseTier) bool { return t.Name == name }); i >= 0 {
			layout.Tiers[i] = tier
		} else {
			layout.Tiers = append(layout.Tiers, tier)
		}
	}
	if _, ok := pinned[TierChokepoint]; ok && len(pins.TrapLane) > 0 {
		layout.TrapLane = slices.Clone(pins.TrapLane)
	}
	if _, ok := pinned[TierFiringLine]; ok && len(pins.Firing) > 0 {
		layout.Firing = slices.Clone(pins.Firing)
	}
	return pinned
}

// displaceBuilt drops the buildings of the tiers still to build that sit on a
// cell a built tier holds, and returns the tiers that lost one.
func displaceBuilt(layout *DefenseLayout, pinned map[DefenseTierName]DefenseTier) []DefenseTierName {
	held := map[domain.Cell]bool{}
	for _, t := range pinned {
		for _, c := range t.Reserved {
			held[c] = true
		}
		for _, b := range t.Buildings {
			held[b.Cell()] = true
		}
	}
	var lost []DefenseTierName
	for i, t := range layout.Tiers {
		if _, built := pinned[t.Name]; built {
			continue
		}
		var kept []domain.Building
		for _, b := range t.Buildings {
			if !held[b.Cell()] {
				kept = append(kept, b)
			}
		}
		if len(kept) < len(t.Buildings) {
			layout.Tiers[i].Buildings = kept
			lost = append(lost, t.Name)
		}
	}
	return lost
}

// tierNodes cuts the layout's tiers into nodes under the killbox node id, in
// defenseTierOrder; a tier that places nothing and reserves nothing has no
// node.
func tierNodes(under string, defs DefenseDefinitions, layout DefenseLayout, pinned map[DefenseTierName]DefenseTier) []PlannedSite {
	var out []PlannedSite
	add := func(tier DefenseTier, kind NodeKind, cells []domain.Cell, places []domain.Building, facts TierFacts) {
		if len(cells) == 0 {
			return
		}
		facts.Under, facts.Tier = under, tier.Name
		_, facts.Pinned = pinned[tier.Name]
		for _, b := range places {
			facts.Places = append(facts.Places, TierPlacement{Definition: b.Definition(), Cell: b.Cell(), Rotation: b.Rotation(), Stuff: b.Stuff()})
		}
		site := newSite(fmt.Sprintf("%s-%s", under, kind), kind, RegionOf(cells), "")
		site.Tier = &facts
		out = append(out, site)
	}
	cellsOf := func(bs []domain.Building) []domain.Cell {
		cells := make([]domain.Cell, len(bs))
		for i, b := range bs {
			cells[i] = b.Cell()
		}
		return cells
	}
	split := func(bs []domain.Building, definition string) (match, rest []domain.Building) {
		for _, b := range bs {
			if b.Definition() == definition {
				match = append(match, b)
			} else {
				rest = append(rest, b)
			}
		}
		return match, rest
	}
	for _, name := range defenseTierOrder {
		i := slices.IndexFunc(layout.Tiers, func(t DefenseTier) bool { return t.Name == name })
		if i < 0 {
			continue
		}
		tier := layout.Tiers[i]
		switch name {
		case TierChokepoint:
			add(tier, NodeDefCorridor, tier.Reserved, nil, TierFacts{Reserved: tier.Reserved})
			add(tier, NodeDefLane, layout.TrapLane, nil, TierFacts{Path: slices.Clone(layout.TrapLane)})
		case TierFunnel:
			add(tier, NodeDefFunnel, cellsOf(tier.Buildings), tier.Buildings, TierFacts{Reserved: tier.Reserved})
		case TierTrapCorridor:
			traps, rest := split(tier.Buildings, defs.Trap)
			fences, _ := split(rest, defs.Fence)
			add(tier, NodeDefTraps, cellsOf(traps), traps, TierFacts{})
			add(tier, NodeDefFence, cellsOf(fences), fences, TierFacts{})
		case TierFiringLine:
			floors, cover := split(tier.Buildings, defs.Floor)
			add(tier, NodeDefFiring, cellsOf(cover), cover, TierFacts{Reserved: tier.Reserved, Firing: slices.Clone(layout.Firing)})
			add(tier, NodeDefShooter, cellsOf(floors), floors, TierFacts{})
		case TierTurrets:
			add(tier, NodeDefTurrets, cellsOf(tier.Buildings), tier.Buildings, TierFacts{Reserved: tier.Reserved})
		case TierMortars:
			add(tier, NodeDefMortars, cellsOf(tier.Buildings), tier.Buildings, TierFacts{Reserved: tier.Reserved})
		case TierIEDs:
			add(tier, NodeDefIEDs, cellsOf(tier.Buildings), tier.Buildings, TierFacts{Reserved: tier.Reserved})
		case TierBait:
			add(tier, NodeDefBait, cellsOf(tier.Buildings), tier.Buildings, TierFacts{Reserved: tier.Reserved})
		}
	}
	return out
}
