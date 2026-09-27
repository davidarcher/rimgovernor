package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"
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
// longer wants are removed first, by removal tiers ahead of the new
// sections: walls and doors deconstructed (a wooden wall the ring now
// wants in stone included, the census being stuff-blind), then plain
// bridges lifted, a heavy bridge being laid only where no foundation is.
// Pumps and conduits the cut drops are left standing: the ground a pump
// dried stays dry.
const (
	defenseMoisturePump     = "MoisturePump"
	defenseMoistureResearch = "MoisturePump"
)

// defensePerimeterTiers anchors a fresh record on the layout plan's
// perimeter (#789) and re-cuts a stored one's when the plan changed.
func defensePerimeterTiers(record *store.DefenseLayoutRecord, read observation.RoutineReading) (bool, error) {
	record.Anchored = true
	projection := read.Projection
	plan, known := projection.LayoutPlan.Value()
	if !known {
		return false, nil
	}
	var transmitters []domain.Cell
	if defenseResearched(projection, defenseMoistureResearch) {
		transmitters = defenseTurretRequest(read).Turret.Transmitters
	}
	return defenseRecutPerimeter(record, plan, projection.Bounds, defensePerimeterBridge(projection), transmitters)
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
// (same key) it does nothing; a killbox the plan has moved off the
// record's entry leaves the record as it is.
func defenseRecutPerimeter(record *store.DefenseLayoutRecord, plan policy.LayoutPlan, bounds policy.Bounds, bridge string, transmitters []domain.Cell) (bool, error) {
	var kept, old []store.DefenseTierRecord
	for _, t := range record.Tiers {
		if policy.IsPerimeterTier(t.Name) {
			old = append(old, t)
		} else {
			kept = append(kept, t)
		}
	}
	if len(old) > 0 {
		if k, _, _, ok := policy.LayoutKillbox(plan, bounds); !ok || k.Entry != record.Entry {
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
	fmt.Fprintf(digest, "%s/%v\n", bridge, len(pumps) > 0)
	var fresh []store.DefenseTierRecord
	wanted := map[string]bool{}
	for _, s := range append(sections, pumps...) {
		t := store.DefenseTierRecord{Name: s.Name}
		for _, b := range s.Buildings {
			if taken[b.Cell()] {
				continue
			}
			rb := store.DefenseBuilding{Definition: b.Definition(), Cell: b.Cell(), Rotation: b.Rotation(), Stuff: b.Stuff()}
			t.Buildings = append(t.Buildings, rb)
			wanted[defenseBuildingKey(rb)] = true
			if rb.Definition != defenseConduitDefinition {
				fmt.Fprintln(digest, defenseBuildingKey(rb))
			}
		}
		if len(t.Buildings) > 0 {
			fresh = append(fresh, t)
		}
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
	// before foundation lifts, a wall standing on its bridge.
	var edifice, foundation []store.DefenseTierRecord
	for _, t := range old {
		var decon, lift []store.DefenseBuilding
		for _, b := range t.Buildings {
			switch {
			case wanted[defenseBuildingKey(b)] || !t.Remove && (b.Definition == defenseConduitDefinition || b.Definition == defenseMoisturePump):
			case defenseFoundation(b.Definition):
				lift = append(lift, b)
			default:
				decon = append(decon, b)
			}
		}
		if len(decon) > 0 {
			edifice = append(edifice, store.DefenseTierRecord{Name: policy.DefenseTierName(fmt.Sprintf("%sx%02d", prefix, len(edifice))), Buildings: decon, Remove: true})
		}
		if len(lift) > 0 {
			foundation = append(foundation, store.DefenseTierRecord{Name: policy.DefenseTierName(fmt.Sprintf("%su%02d", prefix, len(foundation))), Buildings: lift, Remove: true})
		}
	}
	for i := range fresh {
		fresh[i].Name = policy.DefenseTierName(prefix + strings.TrimPrefix(string(fresh[i].Name), policy.TierPerimeterPrefix))
	}
	record.Tiers = slices.Concat(kept, edifice, foundation, fresh)
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
func (r *RoutineDefenseLayoutPlanner) remove(call, epoch context.Context, goal store.GoalState, state ControlState, read observation.RoutineReading, record store.DefenseLayoutRecord, tier store.DefenseTierRecord, census *defenseCensus) (RoutineDefenseLayoutResult, error) {
	p := r.reviewer.player
	key := defenseTierMethodID(tier)
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, key)))
	id := domain.PlanID(fmt.Sprintf("routine-defense-layout-%x", digest[:16]))
	actions, pending, lost, err := defenseRemovalActions(id, tier, census)
	if err != nil {
		return RoutineDefenseLayoutResult{}, err
	}
	if len(lost) > 0 {
		tier.Buildings = slices.DeleteFunc(tier.Buildings, func(b store.DefenseBuilding) bool { return lost[b.Cell] })
		record.SetTier(tier)
		clockSchedulerLog("defense-layout.remove: tier=%s no removable thing on %v; left out of the tier", tier.Name, lost)
		if err = p.journal.SaveDefenseLayout(call, record); err != nil {
			return RoutineDefenseLayoutResult{}, err
		}
	}
	if len(actions) == 0 {
		if pending {
			return RoutineDefenseLayoutResult{Reason: BuildingMethodUnknown, Tier: tier.Name, NativeWorkTicks: defenseNativeWorkTicks}, nil
		}
		return RoutineDefenseLayoutResult{Reason: BuildingMethodUnknown, Tier: tier.Name}, nil
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoutineDefenseLayoutResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineDefenseLayoutResult{}, err
	}
	now := r.reviewer.clock.Now()
	if p.session.State() != state || now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge {
		return RoutineDefenseLayoutResult{}, defenseControlErr(1)
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, key, plan); err != nil {
		return RoutineDefenseLayoutResult{}, err
	}
	tier.Attempts++
	record.SetTier(tier)
	if err = p.journal.SaveDefenseLayout(call, record); err != nil {
		return RoutineDefenseLayoutResult{}, err
	}
	clockSchedulerLog("defense-layout.remove: tier=%s ordered %d removals (%s)", tier.Name, len(actions), key)
	return RoutineDefenseLayoutResult{Reason: BuildingMethodAdmitted, Plan: id, Tier: tier.Name}, nil
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
