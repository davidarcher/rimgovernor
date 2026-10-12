package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"
	"log/slog"
	"slices"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// Perimeter upgrades: the record's perimeter tiers are re-cut from
// the layout plan whenever what the plan asks for changes: the ring
// straightened over ground a moisture pump dried, heavy bridges researched,
// moisture pumps researched or first powered. The buildings the new cut no
// longer wants are removed by removal tiers: walls, doors, pumps and
// conduits deconstructed (a wooden wall the ring now wants in stone
// included, the census being stuff-blind), then plain bridges lifted. The
// new sections are built before the removals, so the old stretch stands
// until its replacement does; only a removal on a cell the new cut
// builds on (a wall re-stuffed, a bridge swapped for a heavy one, laid
// only where no foundation is) goes first.
//
// A tier cut from the plan's site nodes (the ring and its gates, the fence,
// the pumps, the geothermal enclosure) stores the node ids and its state; the
// buildings are resolved from the plan after every load (defenseHydrate). The
// plan regenerates its nodes under fixed ids, so a re-cut cannot read the
// old tiers' cells from the plan it holds: it reads them from the plan the
// tiers were cut from (the record keeps its tick) and freezes them into the
// removal tiers.
const (
	defenseMoisturePump     = "MoisturePump"
	defenseMoistureResearch = "MoisturePump"
	// defenseMoisturePumpW is a moisture pump's draw.
	defenseMoisturePumpW = 150.0
	// defenseRoomLamp lights the planned rooms against infestations.
	defenseRoomLamp = "StandingLamp"
)

// defenseCut is what the perimeter tiers are cut and resolved from: the plan
// and the observed inputs the sections depend on besides it.
type defenseCut struct {
	plan         policy.LayoutPlan
	bridge       string
	transmitters []domain.Cell
	spare        float64
	prison       []policy.PerimeterSection
	// tick is the round's tick. plans reads the plan record the world held
	// at a tick, with the tick it was recorded at; a re-cut reads the plan
	// the record's node tiers were cut from there.
	tick  domain.Tick
	plans func(domain.Tick) (plan policy.LayoutPlan, recorded domain.Tick, ok bool, err error)
}

// defensePerimeterCut gathers the cut's inputs from the round's reading.
func defensePerimeterCut(read observation.RoundsReading, plans func(domain.Tick) (policy.LayoutPlan, domain.Tick, bool, error)) (defenseCut, bool) {
	projection := read.Projection
	plan, known := projection.LayoutPlan.Value()
	if !known {
		return defenseCut{}, false
	}
	cut := defenseCut{plan: plan, bridge: defensePerimeterBridge(projection), tick: projection.Identity.Tick, plans: plans}
	q := defenseTurretRequest(read).Turret
	if defenseResearched(projection, defenseMoistureResearch) {
		cut.transmitters = q.Transmitters
		cut.spare, _ = q.SpareW.Value()
	}
	cut.prison = defensePrisonTurrets(plan, q)
	return cut, true
}

// defensePrisonTurrets are the mini-turrets outside the prison doors,
// planned only while the turret is available and the network's
// spare watts carry every one.
func defensePrisonTurrets(plan policy.LayoutPlan, q policy.DefenseTurretRequest) []policy.PerimeterSection {
	available, ak := q.Available.Value()
	draw, dk := q.DrawW.Value()
	spare, sk := q.SpareW.Value()
	if !ak || !available || !dk || !sk {
		return nil
	}
	sections, err := policy.PerimeterPrisonTurrets(plan, q.Definition, q.Stuff, q.Conduit, q.Transmitters)
	if err != nil {
		return nil
	}
	for i := range sections {
		if float64(i+1)*draw > spare {
			return sections[:i]
		}
	}
	return sections
}

func defenseResearched(projection observation.ColonyProjection, project string) bool {
	research, known := projection.Facts.Research.Value()
	return known && slices.Contains(research.Finished, policy.ResearchProjectID(project))
}

// defenseBuildingKey is a placement's identity for the diff: definition,
// cell and stuff.
func defenseBuildingKey(b store.DefenseBuilding) string {
	return fmt.Sprintf("%s@%d,%d/%s", b.Definition, b.Cell.X, b.Cell.Z, b.Stuff)
}

func defenseStored(b domain.Building) store.DefenseBuilding {
	return store.DefenseBuilding{Definition: b.Definition(), Cell: b.Cell(), Rotation: b.Rotation(), Stuff: b.Stuff()}
}

// defenseNodeSections are the sections cut from the plan's nodes: the ring's
// walls with their gates and the geothermal enclosure, the killbox fence and
// the moisture pumps, each naming its nodes.
func defenseNodeSections(plan policy.LayoutPlan, bridge string, transmitters []domain.Cell) (ring, fences, pumps []policy.PerimeterSection, err error) {
	if ring, err = policy.PerimeterSections(plan, defenseDefinitions.Wall, defenseDefinitions.Door, bridge); err != nil {
		return nil, nil, nil, err
	}
	// The fence across the killbox opening stops roamers only.
	if fences, err = policy.FenceSections(plan, defenseDefinitions.Fence, defenseDefinitions.FenceStuff); err != nil {
		return nil, nil, nil, err
	}
	if pumps, err = policy.PerimeterPumps(plan, defenseMoisturePump, defenseConduitDefinition, transmitters); err != nil {
		return nil, nil, nil, err
	}
	return ring, fences, pumps, nil
}

// defenseSectionFor is the section a node-backed tier is cut from: the one
// naming the tier's nodes (a pump, the fence), told apart by the name the
// tier was cut under where several do (the ring's sections).
func defenseSectionFor(tier store.DefenseTierRecord, sections []policy.PerimeterSection) (policy.PerimeterSection, bool) {
	var named []policy.PerimeterSection
	for _, s := range sections {
		if slices.Equal(s.Nodes, tier.Nodes) {
			named = append(named, s)
		}
	}
	if len(named) == 1 {
		return named[0], true
	}
	base := policy.PerimeterSectionBase(tier.Name)
	for _, s := range named {
		if policy.PerimeterSectionBase(s.Name) == base {
			return s, true
		}
	}
	return policy.PerimeterSection{}, false
}

// defenseResolve is a node-backed tier's buildings in the sections: the
// section's less the cells a killbox tier builds on and those the game
// refused.
func defenseResolve(tier store.DefenseTierRecord, sections []policy.PerimeterSection, taken map[domain.Cell]bool) []store.DefenseBuilding {
	section, ok := defenseSectionFor(tier, sections)
	if !ok {
		return nil
	}
	var out []store.DefenseBuilding
	for _, b := range section.Buildings {
		if !taken[b.Cell()] && !slices.Contains(tier.Refused, b.Cell()) {
			out = append(out, defenseStored(b))
		}
	}
	return out
}

// defenseTaken are the cells the killbox's tiers build on: a firing-line
// embrasure in the wall is that tier's, not the perimeter's.
func defenseTaken(tiers []store.DefenseTierRecord) map[domain.Cell]bool {
	taken := map[domain.Cell]bool{}
	for _, t := range tiers {
		if policy.IsPerimeterTier(t.Name) {
			continue
		}
		for _, b := range t.Buildings {
			taken[b.Cell] = true
		}
	}
	return taken
}

// defenseHydrate fills each node-backed tier's buildings in from the plan.
func defenseHydrate(record *store.DefenseLayoutRecord, cut defenseCut) error {
	ring, fences, pumps, err := defenseNodeSections(cut.plan, cut.bridge, cut.transmitters)
	if err != nil {
		return err
	}
	sections := slices.Concat(ring, fences, pumps)
	taken := defenseTaken(record.Tiers)
	for i := range record.Tiers {
		if t := record.Tiers[i]; t.NodeBacked() {
			record.Tiers[i].Buildings = defenseResolve(t, sections, taken)
		}
	}
	return nil
}

// layoutPlans reads the plan records of the world: the plan current at a tick
// and the tick it was recorded at.
func (r *RoundsDefenseLayoutPlanner) layoutPlans(ctx context.Context, world domain.GenerationSnapshot) func(domain.Tick) (policy.LayoutPlan, domain.Tick, bool, error) {
	return func(tick domain.Tick) (policy.LayoutPlan, domain.Tick, bool, error) {
		rec, ok, err := r.reviewer.player.journal.LayoutPlan(ctx, world, tick)
		return rec.Plan, rec.Tick, ok, err
	}
}

// defenseRecutPerimeter anchors a fresh record on the layout plan's perimeter
// and re-cuts a stored one's when the plan changed, resolving the node tiers'
// buildings from the plan either way. It replaces the record's perimeter tiers with the
// plan's, pumps included when transmitters are given, behind removal tiers
// for what the old ones built that the new ones do not want. Unchanged
// (same key) it resolves the node tiers and does nothing more. A killbox the
// plan has moved off the record's un-anchors the record, so the layout is
// proposed afresh on the new one. A pump not already in the record is planned
// only while spare watts cover it.
func defenseRecutPerimeter(record *store.DefenseLayoutRecord, cut defenseCut) (bool, error) {
	var kept, old []store.DefenseTierRecord
	have := map[string]bool{}
	for _, t := range record.Tiers {
		if policy.IsPerimeterTier(t.Name) {
			old = append(old, t)
			if !t.Remove {
				for _, id := range t.Nodes {
					have[id] = true
				}
			}
		} else {
			kept = append(kept, t)
		}
	}
	if len(old) > 0 {
		id, digest, ok := policy.KillboxNode(cut.plan)
		if !ok {
			return false, defenseHydrate(record, cut)
		}
		if id != record.Killbox || digest != record.KillboxDigest {
			record.Killbox = ""
			return true, nil
		}
	}
	ring, fences, pumps, err := defenseNodeSections(cut.plan, cut.bridge, cut.transmitters)
	if err != nil {
		return false, err
	}
	// Infestation prevention: small overhead-mountain pockets
	// walled solid and every planned room lit, with the wall.
	pockets, err := policy.PocketSections(cut.plan, defenseDefinitions.Wall)
	if err != nil {
		return false, err
	}
	lights, err := policy.LightSections(cut.plan, defenseRoomLamp)
	if err != nil {
		return false, err
	}
	// The dark bait room away from the base: stools around an
	// incendiary IED; jelly (spike traps) is not wanted yet.
	bait, err := policy.BaitRoomSections(cut.plan, defenseDefinitions.Wall, defenseDefinitions.Door, defenseDefinitions.Bait, false)
	if err != nil {
		return false, err
	}
	// A later pump's run may ride an earlier one's, so the cut stops at the
	// first new pump spare power cannot carry.
	spare := cut.spare
	for i, s := range pumps {
		if have[s.Nodes[0]] {
			continue
		}
		if spare < defenseMoisturePumpW {
			pumps = pumps[:i]
			break
		}
		spare -= defenseMoisturePumpW
	}
	taken := defenseTaken(kept)
	// The key leaves out the conduit runs: a network growing a conduit
	// re-routes nothing already standing.
	digest := sha256.New()
	fmt.Fprintf(digest, "sections/%s/%v\n", cut.bridge, len(pumps) > 0)
	// Desired cells key the cut; construction progress does not re-cut it.
	var keys []string
	var fresh []store.DefenseTierRecord
	wanted := map[string]bool{}
	for _, s := range slices.Concat(ring, pockets, lights, bait, fences, pumps, cut.prison) {
		t := store.DefenseTierRecord{Name: s.Name, Nodes: s.Nodes}
		for _, b := range s.Buildings {
			if taken[b.Cell()] {
				continue
			}
			rb := defenseStored(b)
			t.Buildings = append(t.Buildings, rb)
			wanted[defenseBuildingKey(rb)] = true
			if rb.Definition != defenseConduitDefinition {
				keys = append(keys, defenseBuildingKey(rb))
			}
		}
		if len(t.Buildings) > 0 {
			fresh = append(fresh, t)
		}
	}
	slices.Sort(keys)
	for _, k := range keys {
		fmt.Fprintln(digest, k)
	}
	key := fmt.Sprintf("%x", digest.Sum(nil)[:16])
	if key == record.PerimeterKey {
		return false, defenseHydrate(record, cut)
	}
	// The plan record the new tiers are cut from: a later re-cut reads the
	// old tiers' cells there.
	var recorded domain.Tick
	if cut.plans != nil {
		_, tick, ok, err := cut.plans(cut.tick)
		if err != nil {
			return false, err
		}
		if ok {
			recorded = tick
		}
	}
	if len(old) == 0 {
		record.PerimeterKey, record.PerimeterPlanTick, record.PerimeterBridge = key, recorded, cut.bridge
		record.Tiers = append(kept, fresh...)
		return true, record.Validate()
	}
	oldBuildings, err := defenseOldBuildings(record, old, cut)
	if err != nil {
		return false, err
	}
	record.PerimeterKey, record.PerimeterPlanTick, record.PerimeterBridge = key, recorded, cut.bridge
	record.PerimeterRevision++
	prefix := fmt.Sprintf("%sr%d-", policy.TierPerimeterPrefix, record.PerimeterRevision)
	// Removals: a pending one carried over less what the new cut wants
	// again, then what the old cut built that it does not; deconstructions
	// before foundation lifts, a wall standing on its bridge. One on a cell
	// the new cut builds on goes ahead of the new sections, the rest after.
	building := map[domain.Cell]bool{}
	for _, t := range fresh {
		for _, b := range t.Buildings {
			building[b.Cell] = true
		}
	}
	var decon, lift [2][]store.DefenseTierRecord // [0] before, [1] after the new sections
	edifices, foundations := 0, 0
	for i, t := range old {
		var d, l [2][]store.DefenseBuilding
		for _, b := range oldBuildings[i] {
			if wanted[defenseBuildingKey(b)] {
				continue
			}
			after := 1
			if building[b.Cell] {
				after = 0
			}
			if defenseFoundation(b.Definition) {
				l[after] = append(l[after], b)
			} else {
				d[after] = append(d[after], b)
			}
		}
		for i := range 2 {
			if len(d[i]) > 0 {
				decon[i] = append(decon[i], store.DefenseTierRecord{Name: policy.DefenseTierName(fmt.Sprintf("%sx%02d", prefix, edifices)), Nodes: t.Nodes, Buildings: d[i], Remove: true})
				edifices++
			}
			if len(l[i]) > 0 {
				lift[i] = append(lift[i], store.DefenseTierRecord{Name: policy.DefenseTierName(fmt.Sprintf("%su%02d", prefix, foundations)), Nodes: t.Nodes, Buildings: l[i], Remove: true})
				foundations++
			}
		}
	}
	for i := range fresh {
		fresh[i].Name = policy.DefenseTierName(prefix + strings.TrimPrefix(string(fresh[i].Name), policy.TierPerimeterPrefix))
	}
	record.Tiers = slices.Concat(kept, decon[0], lift[0], fresh, decon[1], lift[1])
	record.Complete = false
	return true, record.Validate()
}

// defenseOldBuildings are the buildings each old perimeter tier stood for
// before the re-cut: a removal tier's frozen cells, a stored tier's own, and
// a node tier's as the plan it was cut from (with the bridge it was cut
// with) resolves them. Where that plan is gone the node tier's cells are
// unknown and nothing is removed for it.
func defenseOldBuildings(record *store.DefenseLayoutRecord, old []store.DefenseTierRecord, cut defenseCut) ([][]store.DefenseBuilding, error) {
	out := make([][]store.DefenseBuilding, len(old))
	var sections []policy.PerimeterSection
	resolved := false
	taken := defenseTaken(record.Tiers)
	for i, t := range old {
		if !t.NodeBacked() {
			out[i] = t.Buildings
			continue
		}
		if !resolved {
			resolved = true
			if cut.plans != nil {
				plan, _, ok, err := cut.plans(record.PerimeterPlanTick)
				if err != nil {
					return nil, err
				}
				if ok {
					ring, fences, pumps, err := defenseNodeSections(plan, record.PerimeterBridge, cut.transmitters)
					if err != nil {
						return nil, err
					}
					sections = slices.Concat(ring, fences, pumps)
				}
			}
		}
		out[i] = defenseResolve(t, sections, taken)
	}
	return out, nil
}

// defenseFoundation is a perimeter definition laid as a foundation: the
// bridges.
func defenseFoundation(definition string) bool {
	return definition == policy.PerimeterBridge || definition == policy.PerimeterHeavyBridge
}

// defenseRemovalGone reports whether none of a removal tier's buildings
// stands in the census.
func defenseRemovalGone(tier store.DefenseTierRecord, census *defenseCensus) bool {
	for _, b := range tier.Buildings {
		if census.standing(b.Definition, b.Cell) {
			return false
		}
	}
	return true
}

// remove admits the next removal tier's orders: a Deconstruct clearance on
// each standing wall or door the census identifies, or a foundation removal
// on each standing bridge. A target already designated is work in
// progress; one the census cannot identify (no removable cover thing of
// its definition) is left out of the tier, as a refused placement is.
func (r *RoundsDefenseLayoutPlanner) remove(call, epoch context.Context, goal store.StandardState, state ControlState, read observation.RoundsReading, record store.DefenseLayoutRecord, tier store.DefenseTierRecord, census *defenseCensus) (RoundsDefenseLayoutResult, error) {
	p := r.reviewer.player
	key := defenseTierMethodID(tier)
	id := domain.MintPlanID()
	actions, pending, lost, err := defenseRemovalActions(id, tier, census)
	if err != nil {
		return RoundsDefenseLayoutResult{}, err
	}
	if len(lost) > 0 {
		tier.Buildings = slices.DeleteFunc(tier.Buildings, func(b store.DefenseBuilding) bool { return lost[b.Cell] })
		record.SetTier(tier)
		defenseAction(call, "defense-layout", slog.LevelInfo, "refused", "nothing_removable", string(tier.Name), map[string]any{"cells": len(lost)})
		if err = p.journal.SaveDefenseLayout(call, record); err != nil {
			return RoundsDefenseLayoutResult{}, err
		}
	}
	if len(actions) == 0 {
		if pending {
			return RoundsDefenseLayoutResult{Verdict: fieldUnavailable("perimeter_preview"), Tier: tier.Name, NativeWorkTicks: defenseNativeWorkTicks}, nil
		}
		return RoundsDefenseLayoutResult{Verdict: noSpace("perimeter_section"), Tier: tier.Name}, nil
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoundsDefenseLayoutResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsDefenseLayoutResult{}, err
	}
	now := r.reviewer.clock.Now()
	if p.session.State() != state || now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge {
		return RoundsDefenseLayoutResult{}, defenseControlErr(1)
	}
	if _, err = p.journal.CommitMethodReason(call, goal.Standard.ID, goal.Revision, key, "", plan); err != nil {
		return RoundsDefenseLayoutResult{}, err
	}
	tier.Attempts++
	record.SetTier(tier)
	if err = p.journal.SaveDefenseLayout(call, record); err != nil {
		return RoundsDefenseLayoutResult{}, err
	}
	defenseAction(call, "defense-layout", slog.LevelInfo, "applied", "removals", string(tier.Name), map[string]any{"removals": len(actions), "method": string(key)})
	return RoundsDefenseLayoutResult{Verdict: BuildingReasonAdmitted, Plan: id, Tier: tier.Name}, nil
}

// maxDefenseRemovals bounds one removal method's orders.
const maxDefenseRemovals = 64

// defenseRemovalActions orders the removal tier's standing targets.
// pending reports a target already designated; lost the cells whose target
// stands but cannot be ordered.
func defenseRemovalActions(id domain.PlanID, tier store.DefenseTierRecord, census *defenseCensus) ([]domain.Action, bool, map[domain.Cell]bool, error) {
	var actions []domain.Action
	pending, lost := false, map[domain.Cell]bool{}
	for _, b := range tier.Buildings {
		if !census.standing(b.Definition, b.Cell) || len(actions) == maxDefenseRemovals {
			continue
		}
		var action domain.Action
		var err error
		actionID := domain.ActionID(fmt.Sprintf("%s-%d", id, len(actions)))
		if defenseFoundation(b.Definition) {
			if census.unbridging[b.Cell] {
				pending = true
				continue
			}
			var lift domain.FoundationRemoval
			if lift, err = domain.NewFoundationRemoval(b.Definition, b.Cell); err == nil {
				action, err = domain.NewFoundationRemovalAction(actionID, lift)
			}
		} else {
			cover := census.cover[b.Cell]
			switch {
			case cover != nil && cover.Designated:
				pending = true
				continue
			case cover == nil || cover.DefName != b.Definition || cover.Designation() != domain.CoverClearanceDeconstruct:
				lost[b.Cell] = true
				continue
			}
			var clearance domain.CoverClearance
			if clearance, err = domain.NewCoverClearance(cover.ThingID, cover.DefName, domain.CoverClearanceDeconstruct, b.Cell); err == nil {
				action, err = domain.NewCoverClearanceAction(actionID, clearance)
			}
		}
		if err != nil {
			return nil, false, nil, err
		}
		actions = append(actions, action)
	}
	return actions, pending, lost, nil
}
