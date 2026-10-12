package buildingruntime

import (
	"cmp"
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
// buildings are resolved from the plan after every load (defenseHydrate). A
// ring the plan traces again while the record holds the old one is a
// replacement under new ids, the old nodes retired beside it (#2924,
// policy.PerimeterHold): the removal tiers name the retired nodes and resolve
// the old buildings from them, less what the standing tiers want again, so
// they store no cells. The plan keeps the retired nodes until no tier names
// them; while one does, or a removal tier stands, it traces no new ring.
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
	lampReach    float64
	prison       []policy.PerimeterSection
}

// defensePerimeterCut gathers the cut's inputs from the round's reading.
func defensePerimeterCut(read observation.RoundsReading) (defenseCut, bool) {
	projection := read.Projection
	plan, known := projection.LayoutPlan.Value()
	if !known {
		return defenseCut{}, false
	}
	cut := defenseCut{plan: plan, bridge: defensePerimeterBridge(projection), lampReach: projection.Stations.Light.StandingReach()}
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

// defenseHydrate fills each node-backed tier's buildings in from the plan: the
// standing tiers' from the live nodes, the removal tiers' from the retired ones.
func defenseHydrate(record *store.DefenseLayoutRecord, cut defenseCut) error {
	ring, fences, pumps, err := defenseNodeSections(cut.plan, cut.bridge, cut.transmitters)
	if err != nil {
		return err
	}
	sections := slices.Concat(ring, fences, pumps)
	taken := defenseTaken(record.Tiers)
	for i := range record.Tiers {
		if t := record.Tiers[i]; t.NodeBacked() && !t.Remove {
			record.Tiers[i].Buildings = defenseResolve(t, sections, taken)
		}
	}
	wanted, building := defenseWanted(record.Tiers)
	// The removal tiers of one generation share its sections.
	cuts := map[string][]policy.PerimeterSection{}
	for i := range record.Tiers {
		if t := record.Tiers[i]; t.NodeBacked() && t.Remove {
			key := t.Bridge + "/" + strings.Join(t.Nodes, ",")
			if _, ok := cuts[key]; !ok {
				ring, fences, pumps, err := defenseNodeSections(cut.plan.SiteView(t.Nodes), t.Bridge, cut.transmitters)
				if err != nil {
					return err
				}
				cuts[key] = slices.Concat(ring, fences, pumps)
			}
			record.Tiers[i].Buildings = defenseResolveRemoval(t, cuts[key], taken, wanted, building)
		}
	}
	return nil
}

// defenseWanted are the perimeter buildings the standing tiers want, by key,
// and the cells they build on.
func defenseWanted(tiers []store.DefenseTierRecord) (wanted map[string]bool, building map[domain.Cell]bool) {
	wanted, building = map[string]bool{}, map[domain.Cell]bool{}
	for _, t := range tiers {
		if !policy.IsPerimeterTier(t.Name) || t.Remove {
			continue
		}
		for _, b := range t.Buildings {
			wanted[defenseBuildingKey(b)], building[b.Cell] = true, true
		}
	}
	return wanted, building
}

// defenseResolveRemoval is a removal tier of plan nodes' buildings: what the
// nodes' sections (cut with the tier's bridge) built that the standing tiers do
// not want, of the tier's kind (foundations or walls and doors) and side of the
// new sections (on a cell they build on, or not), less the cells a killbox
// tier builds on.
func defenseResolveRemoval(tier store.DefenseTierRecord, sections []policy.PerimeterSection, taken map[domain.Cell]bool, wanted map[string]bool, building map[domain.Cell]bool) []store.DefenseBuilding {
	var out []store.DefenseBuilding
	for _, s := range sections {
		for _, b := range s.Buildings {
			sb := defenseStored(b)
			if !taken[sb.Cell] && !wanted[defenseBuildingKey(sb)] && defenseFoundation(sb.Definition) == tier.Foundation && building[sb.Cell] == tier.Before {
				out = append(out, sb)
			}
		}
	}
	return out
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
	// have are the cells of the pumps the record already holds: a pump kept
	// across a replacement costs no more power.
	have := map[domain.Cell]bool{}
	for _, t := range record.Tiers {
		if !policy.IsPerimeterTier(t.Name) {
			kept = append(kept, t)
			continue
		}
		old = append(old, t)
		if t.Remove {
			continue
		}
		for _, id := range t.Nodes {
			if site, ok := cut.plan.Site(id); ok && site.Kind == policy.NodePump {
				have[site.Origin] = true
			}
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
	lights, err := policy.LightSections(cut.plan, defenseRoomLamp, cut.lampReach)
	if err != nil {
		return false, err
	}
	// A later pump's run may ride an earlier one's, so the cut stops at the
	// first new pump spare power cannot carry.
	spare := cut.spare
	for i, s := range pumps {
		if site, ok := cut.plan.Site(s.Nodes[0]); ok && have[site.Origin] {
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
	for _, s := range slices.Concat(ring, pockets, lights, fences, pumps, cut.prison) {
		t := store.DefenseTierRecord{Name: s.Name, Nodes: s.Nodes}
		// A replaced ring re-cuts the tiers whatever it kept: they name its nodes.
		keys = append(keys, "nodes/"+strings.Join(s.Nodes, ","))
		for _, b := range s.Buildings {
			if taken[b.Cell()] {
				continue
			}
			rb := defenseStored(b)
			t.Buildings = append(t.Buildings, rb)
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
	if len(old) == 0 {
		record.PerimeterKey, record.PerimeterBridge = key, cut.bridge
		record.Tiers = append(kept, fresh...)
		return true, record.Validate()
	}
	previous := cmp.Or(record.PerimeterBridge, cut.bridge)
	record.PerimeterKey, record.PerimeterBridge = key, cut.bridge
	record.PerimeterRevision++
	prefix := fmt.Sprintf("%sr%d-", policy.TierPerimeterPrefix, record.PerimeterRevision)
	wanted, building := map[string]bool{}, map[domain.Cell]bool{}
	for _, t := range fresh {
		for _, b := range t.Buildings {
			wanted[defenseBuildingKey(b)], building[b.Cell] = true, true
		}
	}
	// Removals, in four stages: walls and doors before foundations (a wall
	// stands on its bridge), those on a cell the new sections build on ahead of
	// them, the rest after. A pending removal carries over (its buildings
	// resolve less what the new cut wants again); the old tiers become removals
	// of the nodes they were cut from, resolved from the plan, and of the
	// buildings they stored.
	var removals [2][2][]store.DefenseTierRecord // [after][foundation]
	stage := func(t store.DefenseTierRecord) {
		after, lift := 1, 0
		if t.Before {
			after = 0
		}
		if t.Foundation {
			lift = 1
		}
		removals[after][lift] = append(removals[after][lift], t)
	}
	n := 0
	name := func(letter string) policy.DefenseTierName {
		n++
		return policy.DefenseTierName(fmt.Sprintf("%s%s%02d", prefix, letter, n-1))
	}
	var ids []string
	for _, t := range old {
		switch {
		case t.Remove && t.NodeBacked():
			stage(t)
		case t.Remove:
			t.Buildings = slices.DeleteFunc(slices.Clone(t.Buildings), func(b store.DefenseBuilding) bool { return wanted[defenseBuildingKey(b)] })
			if len(t.Buildings) > 0 {
				stage(t)
			}
		case t.NodeBacked():
			for _, id := range t.Nodes {
				if !slices.Contains(ids, id) {
					ids = append(ids, id)
				}
			}
		default:
			var split [2][2][]store.DefenseBuilding // [after][foundation]
			for _, b := range t.Buildings {
				if wanted[defenseBuildingKey(b)] {
					continue
				}
				after, lift := 1, 0
				if building[b.Cell] {
					after = 0
				}
				if defenseFoundation(b.Definition) {
					lift = 1
				}
				split[after][lift] = append(split[after][lift], b)
			}
			for after := range 2 {
				for lift := range 2 {
					if len(split[after][lift]) > 0 {
						stage(store.DefenseTierRecord{Name: name(map[int]string{0: "x", 1: "u"}[lift]), Buildings: split[after][lift], Remove: true, Before: after == 0, Foundation: lift == 1})
					}
				}
			}
		}
	}
	if len(ids) > 0 {
		for after := range 2 {
			for lift := range 2 {
				stage(store.DefenseTierRecord{Name: name(map[int]string{0: "x", 1: "u"}[lift]), Nodes: ids, Bridge: previous, Remove: true, Before: after == 0, Foundation: lift == 1})
			}
		}
	}
	for i := range fresh {
		fresh[i].Name = policy.DefenseTierName(prefix + strings.TrimPrefix(string(fresh[i].Name), policy.TierPerimeterPrefix))
	}
	record.Tiers = slices.Concat(kept, removals[0][0], removals[0][1], fresh, removals[1][0], removals[1][1])
	record.Complete = false
	if err = defenseHydrate(record, cut); err != nil {
		return false, err
	}
	// A removal of nodes that resolves to nothing has nothing to demolish.
	record.Tiers = slices.DeleteFunc(record.Tiers, func(t store.DefenseTierRecord) bool {
		return t.Remove && t.NodeBacked() && len(t.Buildings) == 0
	})
	return true, record.Validate()
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
