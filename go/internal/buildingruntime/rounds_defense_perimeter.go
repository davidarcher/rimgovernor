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

// Perimeter upgrades (#954): the record's perimeter tiers are re-cut from
// the layout plan whenever what the plan asks for changes: the ring
// straightened over ground a moisture pump dried, heavy bridges researched,
// moisture pumps researched or first powered. The buildings the new cut no
// longer wants are removed by removal tiers: walls, doors, pumps and
// conduits deconstructed (a wooden wall the ring now wants in stone
// included, the census being stuff-blind), then plain bridges lifted. The
// new sections are built before the removals, so the old stretch stands
// until its replacement does (#983); only a removal on a cell the new cut
// builds on (a wall re-stuffed, a bridge swapped for a heavy one, laid
// only where no foundation is) goes first.
const (
	defenseMoisturePump     = "MoisturePump"
	defenseMoistureResearch = "MoisturePump"
	// defenseMoisturePumpW is a moisture pump's draw (#983).
	defenseMoisturePumpW = 150.0
	// defenseRoomLamp lights the planned rooms against infestations
	// (#1067).
	defenseRoomLamp = "StandingLamp"
)

// defensePerimeterTiers anchors a fresh record on the layout plan's
// perimeter (#789) and re-cuts a stored one's when the plan changed.
func defensePerimeterTiers(record *store.DefenseLayoutRecord, read observation.RoundsReading) (bool, error) {
	record.Anchored = true
	projection := read.Projection
	plan, known := projection.LayoutPlan.Value()
	if !known {
		return false, nil
	}
	var transmitters []domain.Cell
	spare := 0.0
	q := defenseTurretRequest(read).Turret
	if defenseResearched(projection, defenseMoistureResearch) {
		transmitters = q.Transmitters
		spare, _ = q.SpareW.Value()
	}
	return defenseRecutPerimeter(record, plan, projection.Bounds, defensePerimeterBridge(projection), transmitters, spare, defensePrisonTurrets(plan, q)...)
}

// defensePrisonTurrets are the mini-turrets outside the prison doors
// (#1081), planned only while the turret is available and the network's
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

// defenseRecutPerimeter replaces the record's perimeter tiers with the
// plan's, pumps included when transmitters are given, behind removal tiers
// for what the old ones built that the new ones do not want. Unchanged
// (same key) it does nothing. A killbox the plan has moved off the
// record's entry un-anchors the record, so the layout is proposed afresh
// on the new one (#983). A pump not already in the record is planned only
// while spare watts cover it.
func defenseRecutPerimeter(record *store.DefenseLayoutRecord, plan policy.LayoutPlan, bounds policy.Bounds, bridge string, transmitters []domain.Cell, spare float64, prison ...policy.PerimeterSection) (bool, error) {
	var kept, old []store.DefenseTierRecord
	standing := map[string]bool{}
	for _, t := range record.Tiers {
		if policy.IsPerimeterTier(t.Name) {
			old = append(old, t)
			for _, b := range t.Buildings {
				standing[defenseBuildingKey(b)] = standing[defenseBuildingKey(b)] || !t.Remove
			}
		} else {
			kept = append(kept, t)
		}
	}
	if len(old) > 0 {
		if k, _, _, ok := policy.LayoutKillbox(plan, bounds); ok && k.Entry != record.Entry {
			record.Anchored = false
			return true, nil
		} else if !ok {
			return false, nil
		}
	}
	sections, err := policy.PerimeterSections(plan, defenseDefinitions.Wall, defenseDefinitions.Door, bridge)
	if err != nil {
		return false, err
	}
	pumps, err := policy.PerimeterPumps(plan, defenseMoisturePump, defenseConduitDefinition, transmitters)
	if err != nil {
		return false, err
	}
	// Infestation prevention (#1067): small overhead-mountain pockets
	// walled solid and every planned room lit, with the wall.
	pockets, err := policy.PocketSections(plan, defenseDefinitions.Wall)
	if err != nil {
		return false, err
	}
	lights, err := policy.LightSections(plan, defenseRoomLamp)
	if err != nil {
		return false, err
	}
	// The fence across the killbox opening (#2231) stops roamers only.
	fences, err := policy.FenceSections(plan, defenseDefinitions.Fence, defenseDefinitions.FenceStuff)
	if err != nil {
		return false, err
	}
	// The dark bait room away from the base (#1069): stools around an
	// incendiary IED; jelly (spike traps) is not wanted yet.
	bait, err := policy.BaitRoomSections(plan, defenseDefinitions.Wall, defenseDefinitions.Door, defenseDefinitions.Bait, false)
	if err != nil {
		return false, err
	}
	sections = slices.Concat(sections, pockets, lights, bait, fences)
	// A later pump's run may ride an earlier one's, so the cut stops at the
	// first new pump spare power cannot carry.
	for i, s := range pumps {
		b := s.Buildings[0]
		key := defenseBuildingKey(store.DefenseBuilding{Definition: b.Definition(), Cell: b.Cell(), Rotation: b.Rotation(), Stuff: b.Stuff()})
		if standing[key] {
			continue
		}
		if spare < defenseMoisturePumpW {
			pumps = pumps[:i]
			break
		}
		spare -= defenseMoisturePumpW
	}
	// A cell a killbox tier already builds on (a firing-line embrasure in
	// the wall, #868) is that tier's, not the perimeter's.
	taken := map[domain.Cell]bool{}
	for _, t := range kept {
		for _, b := range t.Buildings {
			taken[b.Cell] = true
		}
	}
	// The key leaves out the conduit runs: a network growing a conduit
	// re-routes nothing already standing.
	digest := sha256.New()
	fmt.Fprintf(digest, "sections/%s/%v\n", bridge, len(pumps) > 0)
	// Desired cells key the cut; construction progress does not re-cut it.
	var keys []string
	var fresh []store.DefenseTierRecord
	wanted := map[string]bool{}
	for _, s := range slices.Concat(sections, pumps, prison) {
		t := store.DefenseTierRecord{Name: s.Name}
		for _, b := range s.Buildings {
			if taken[b.Cell()] {
				continue
			}
			rb := store.DefenseBuilding{Definition: b.Definition(), Cell: b.Cell(), Rotation: b.Rotation(), Stuff: b.Stuff()}
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
		return false, nil
	}
	record.PerimeterKey = key
	if len(old) == 0 {
		record.Tiers = append(kept, fresh...)
		return true, record.Validate()
	}
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
	for _, t := range old {
		var d, l [2][]store.DefenseBuilding
		for _, b := range t.Buildings {
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
				decon[i] = append(decon[i], store.DefenseTierRecord{Name: policy.DefenseTierName(fmt.Sprintf("%sx%02d", prefix, edifices)), Buildings: d[i], Remove: true})
				edifices++
			}
			if len(l[i]) > 0 {
				lift[i] = append(lift[i], store.DefenseTierRecord{Name: policy.DefenseTierName(fmt.Sprintf("%su%02d", prefix, foundations)), Buildings: l[i], Remove: true})
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
func (r *RoundsDefenseLayoutPlanner) remove(call, epoch context.Context, goal store.ProjectState, state ControlState, read observation.RoundsReading, record store.DefenseLayoutRecord, tier store.DefenseTierRecord, census *defenseCensus) (RoundsDefenseLayoutResult, error) {
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
	if _, err = p.journal.CommitProjectMethod(call, goal.Project.ID, goal.Revision, key, "", plan); err != nil {
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
