package defense

import (
	"context"
	"errors"
	"fmt"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// The powered turret tier (#61) on the committed layout checkpoint: the
// fixture opens every observed gate (research finished natively, a fuelled
// generator with a conduit stub outside connector reach of the firing row,
// steel and components in stock) and the layout planner adds the tier to the
// stored record, routes its own conduit chain and builds it natively; the
// turrets are then observed powered, an edge raid is answered with the hold
// and a turret is observed to have taken aim at the raiders; afterwards one
// tier conduit vanishes and a turret is damaged, and routine upkeep (the
// layout's own missing-building rebuild or a power-family route, and
// MaintainEssentialRepairs) restores it to powered and full hit points;
// finally a turret's barrel is emptied with the game's auto-refuel off and
// the layout's own rearm order (#205) is observed to refill it.
const (
	turretTimeout      = 15 * time.Minute
	turretDefinition   = "Turret_MiniTurret"
	conduitDefinition  = "PowerConduit"
	generatorDistance  = 28
	turretTierMinCount = 1
)

// turretGeneratorAnchor is where the fixture stands the generator: the
// corridor entry pushed generatorDistance cells into the colony, well past
// the firing row (entry+9) and the turret row behind it (entry+12), with
// the three-cell conduit stub the fixture adds north of the generator still
// outside native connector reach (six cells) of every turret candidate, so
// the planner has to route a conduit chain.
func turretGeneratorAnchor(layout store.DefenseLayoutRecord) domain.Cell {
	d := domain.Cell{Z: -1}
	switch layout.Toward {
	case domain.North:
		d = domain.Cell{Z: 1}
	case domain.East:
		d = domain.Cell{X: 1}
	case domain.West:
		d = domain.Cell{X: -1}
	}
	return domain.Cell{X: layout.Entry.X + d.X*generatorDistance, Z: layout.Entry.Z + d.Z*generatorDistance}
}

// turretTierCells splits the stored turret tier into turret and conduit cells.
func turretTierCells(layout store.DefenseLayoutRecord) (turrets, conduits []domain.Cell, ok bool) {
	tier, _, ok := layout.Tier(policy.TierTurrets)
	if !ok {
		return nil, nil, false
	}
	for _, b := range tier.Buildings {
		switch b.Definition {
		case turretDefinition:
			turrets = append(turrets, b.Cell)
		case conduitDefinition:
			conduits = append(conduits, b.Cell)
		}
	}
	return turrets, conduits, true
}

// waitTurretTier polls the journal until the stored layout carries a turret
// tier with at least one turret observed built, the record is complete
// again and no tier method still has open work (the census marks a turret
// built as soon as its edifice stands, a tick before the power net has
// connected it; the settled plan puts the native check after that tick),
// recording the tier methods' plans and stages. The goal's need stays a
// deficit while the operator opts in (the planner reports no work once
// every tier stands), so power is checked natively afterwards.
func waitTurretTier(ctx context.Context, s *store.Store, world store.World, w na.Wait, report na.Report) (store.DefenseLayoutRecord, error) {
	var record store.DefenseLayoutRecord
	methods := map[string]any{}
	err := na.WaitProgress(ctx, w, func(ctx context.Context) (string, bool, error) {
		review, err := s.LoadRoutineReview(ctx)
		if err != nil {
			return "", false, err
		}
		need := ""
		open := false
		for _, binding := range review.Goals {
			if binding.Need != policy.EnsureDefensiveLayout {
				continue
			}
			goal, err := s.LoadGoal(ctx, binding.Goal)
			if err != nil && !errors.Is(err, store.ErrNotFound) {
				return "", false, err
			}
			need = string(goal.Goal.Need)
			for _, m := range goal.Methods {
				entry := map[string]any{"plan": string(m.Plan), "epoch": m.Epoch}
				if plan, err := s.LoadPlan(ctx, m.Plan); err == nil {
					entry["stages"] = stages(plan.Progress)
					entry["actions"] = len(plan.Spec.Actions())
					open = open || domain.GoalWorkOpen(plan.Progress)
				}
				methods[string(m.Method)] = entry
			}
		}
		report["turret_tier_methods"] = methods
		r, ok, err := s.LoadDefenseLayout(ctx, world)
		if err != nil {
			return "", false, err
		}
		if !ok {
			return "", false, errors.New("stored layout gone")
		}
		record = r
		tier, _, hasTier := record.Tier(policy.TierTurrets)
		turrets, conduits, _ := turretTierCells(record)
		report["turret_tier"] = map[string]any{"proposed": hasTier, "turrets": cellsJSON(turrets), "conduits": cellsJSON(conduits),
			"built": tier.Built, "attempts": tier.Attempts, "probed_tick": int64(record.TurretsProbedTick), "complete": record.Complete, "goal_need": need}
		if hasTier && len(turrets) >= turretTierMinCount && tier.Built && record.Complete && !open {
			return "", true, nil
		}
		var progress []string
		for _, name := range sortedKeys(methods) {
			entry, _ := methods[name].(map[string]any)
			stages, _ := entry["stages"].([]string)
			progress = append(progress, name+":"+strings.Join(stages, ","))
		}
		return na.Signature(hasTier, len(turrets), len(conduits), tier.Built, tier.Attempts, record.Complete, open, need, record.TurretsProbedTick, review.Tick, progress), false, nil
	})
	if err != nil {
		return record, fmt.Errorf("turret tier not built (%#v): %w", report["turret_tier"], err)
	}
	return record, nil
}

// turretRows indexes the fixture's turret rows by cell.
func turretRows(inspect map[string]any) map[domain.Cell]map[string]any {
	rows := map[domain.Cell]map[string]any{}
	for _, raw := range na.AsSlice(inspect["turrets"]) {
		row, _ := na.AsMap(raw)
		rows[domain.Cell{X: int32(na.AsNumber(row["x"])), Z: int32(na.AsNumber(row["z"]))}] = row
	}
	return rows
}

// assertTurretsPowered checks every turret of the stored tier stands
// natively on its cell, connected to a network and powered.
func assertTurretsPowered(inspect map[string]any, layout store.DefenseLayoutRecord) error {
	turrets, _, ok := turretTierCells(layout)
	if !ok || len(turrets) < turretTierMinCount {
		return fmt.Errorf("stored layout has no turret tier: %+v", layout.Tiers)
	}
	rows := turretRows(inspect)
	for _, cell := range turrets {
		row, ok := rows[cell]
		if !ok {
			return fmt.Errorf("no native turret on tier cell %v: %v", cell, inspect["turrets"])
		}
		if powered, _ := na.AsBool(row["powered"]); !powered {
			return fmt.Errorf("turret %s at %v is not powered: %v", na.AsString(row["id"]), cell, row)
		}
		if home, _ := na.AsBool(row["home"]); !home {
			return fmt.Errorf("turret %s at %v is outside the home area, repairs would never reach it", na.AsString(row["id"]), cell)
		}
	}
	return nil
}

// assertTurretFired checks at least one tier turret fired after the raid was
// staged: the battle log records each burst with the turret as the
// initiator, and Building_TurretGun records the tick it last took aim.
func assertTurretFired(inspect map[string]any, layout store.DefenseLayoutRecord, raidTick int64) (map[string]any, error) {
	turrets, _, _ := turretTierCells(layout)
	rows := turretRows(inspect)
	out := map[string]any{"raid_tick": raidTick}
	fired := 0
	for _, cell := range turrets {
		row, ok := rows[cell]
		if !ok {
			continue
		}
		last := int64(na.AsNumber(row["lastAttackTick"]))
		shot := int64(na.AsNumber(row["lastShotTick"]))
		out[fmt.Sprintf("%d,%d", cell.X, cell.Z)] = map[string]any{"id": na.AsString(row["id"]), "last_attack_tick": last, "last_shot_tick": shot, "shots": row["shots"], "powered": row["powered"], "hp": row["hp"]}
		if last > raidTick || shot > raidTick {
			fired++
		}
	}
	out["fired"] = fired
	if fired == 0 {
		return out, fmt.Errorf("no tier turret took aim after the raid at tick %d: %v", raidTick, inspect["turrets"])
	}
	return out, nil
}

// waitTurretRestored polls the journal until MaintainEssentialRepairs has
// reported the damaged turret and recovered, and the stored layout stands
// verified complete after the conduit vanished (its missing conduit
// re-placed and observed). The native state is checked afterwards.
func waitTurretRestored(ctx context.Context, s *store.Store, world store.World, depowerTick int64, w na.Wait) (map[string]any, error) {
	out := map[string]any{"depower_tick": depowerTick}
	repairDeficit, repairRecovered := false, false
	methods := map[string]any{}
	err := na.WaitProgress(ctx, w, func(ctx context.Context) (string, bool, error) {
		review, err := s.LoadRoutineReview(ctx)
		if err != nil {
			return "", false, err
		}
		var needs []string
		for _, binding := range review.Goals {
			goal, err := s.LoadGoal(ctx, binding.Goal)
			if err != nil {
				if errors.Is(err, store.ErrNotFound) {
					continue
				}
				return "", false, err
			}
			switch binding.Need {
			case policy.MaintainEssentialRepairs:
				needs = append(needs, "repairs:"+string(goal.Goal.Need))
				switch goal.Goal.Need {
				case domain.NeedDeficit:
					repairDeficit = true
				case domain.NeedRecovered:
					repairRecovered = repairRecovered || repairDeficit
				}
				for _, m := range goal.Methods {
					entry := map[string]any{"plan": string(m.Plan)}
					if plan, err := s.LoadPlan(ctx, m.Plan); err == nil {
						entry["stages"] = stages(plan.Progress)
					}
					methods["repair/"+string(m.Method)] = entry
				}
			case policy.EnsureBasicPower, policy.EnsureDefensiveLayout:
				needs = append(needs, string(binding.Need)+":"+string(goal.Goal.Need))
				for _, m := range goal.Methods {
					entry := map[string]any{"plan": string(m.Plan)}
					if plan, err := s.LoadPlan(ctx, m.Plan); err == nil {
						entry["stages"] = stages(plan.Progress)
					}
					methods[string(binding.Need)+"/"+string(m.Method)] = entry
				}
			}
		}
		record, ok, err := s.LoadDefenseLayout(ctx, world)
		if err != nil {
			return "", false, err
		}
		if !ok {
			return "", false, errors.New("stored layout gone after the raid")
		}
		tier, _, _ := record.Tier(policy.TierTurrets)
		standing := record.Standing() && int64(record.VerifiedTick) >= depowerTick
		out["repair_deficit_seen"], out["repair_recovered"], out["layout_standing"] = repairDeficit, repairRecovered, standing
		out["turret_tier_built"], out["turret_tier_attempts"], out["verified_tick"] = tier.Built, tier.Attempts, int64(record.VerifiedTick)
		out["methods"], out["needs"] = methods, needs
		if repairRecovered && standing {
			return "", true, nil
		}
		var progress []string
		for _, name := range sortedKeys(methods) {
			entry, _ := methods[name].(map[string]any)
			stages, _ := entry["stages"].([]string)
			progress = append(progress, name+":"+strings.Join(stages, ","))
		}
		return na.Signature(repairDeficit, repairRecovered, standing, tier.Built, tier.Attempts, record.VerifiedTick, review.Tick, needs, progress), false, nil
	})
	if err != nil {
		return out, fmt.Errorf("turret not restored (%#v): %w", out, err)
	}
	return out, nil
}

// runTurretUpkeep is the turret case's aftermath once the hold released its
// drafts: the turrets' firing evidence, the staged loss (a tier conduit
// vanished, a turret at half hit points) and the routine restoration.
func runTurretUpkeep(ctx context.Context, closeClient func() error, reopenHarness func() error, fixture func(string, map[string]any) (map[string]any, error),
	launch func(string) (*service, error), layout store.DefenseLayoutRecord, world store.World, raidTick int64, report na.Report) error {
	if err := reopenHarness(); err != nil {
		return err
	}
	afterRaid, err := fixture("inspect-after-raid", map[string]any{"op": "inspect"})
	if err != nil {
		return err
	}
	report["inspect_after_raid"] = afterRaid
	fired, err := assertTurretFired(afterRaid, layout, raidTick)
	report["turret_fire"] = fired
	if err != nil {
		return err
	}
	healed, err := fixture("heal-after-raid", map[string]any{"op": "heal"})
	if err != nil {
		return err
	}
	report["healed_after_raid"] = healed
	turrets, conduits, _ := turretTierCells(layout)
	if len(conduits) == 0 {
		return fmt.Errorf("turret tier routed no conduit chain; the generator stub must stand outside connector reach: %v", cellsJSON(turrets))
	}
	// The chain cell nearest the turret: losing it disconnects the turret
	// while the generator's own stub stays; and the tier turret itself.
	lost := conduits[0]
	depower, err := fixture("depower", map[string]any{"op": "depower", "x": int(lost.X), "z": int(lost.Z)})
	if err != nil {
		return err
	}
	report["depower"] = depower
	rows := turretRows(afterRaid)
	target, ok := rows[turrets[0]]
	if !ok {
		return fmt.Errorf("tier turret %v missing natively after the raid: %v", turrets[0], afterRaid["turrets"])
	}
	damage, err := fixture("damage-turret", map[string]any{"op": "damage", "wall": na.AsString(target["id"])})
	if err != nil {
		return err
	}
	report["damage_turret"] = damage
	depowerTick := int64(na.AsNumber(depower["tick"]))
	if err := closeClient(); err != nil {
		return err
	}
	svc, err := launch("restore")
	if err != nil {
		return err
	}
	defer svc.stop()
	restored, err := waitTurretRestored(ctx, svc.store, world, depowerTick, svc.wait(repairTimeout))
	report["turret_restore"] = restored
	if err != nil {
		return err
	}
	svc.stop()
	report["restore_authority"] = svc.keepAlive.snapshot()
	if err := reopenHarness(); err != nil {
		return err
	}
	final, err := fixture("inspect-after-restore", map[string]any{"op": "inspect"})
	if err != nil {
		return err
	}
	report["inspect_after_restore"] = final
	if !hasCell(na.AsSlice(final["conduits"]), int(lost.X), int(lost.Z)) {
		return fmt.Errorf("conduit cell %v holds no conduit after the restore: %v", lost, final["conduits"])
	}
	row, ok := turretRows(final)[turrets[0]]
	if !ok {
		return fmt.Errorf("turret %v gone after the restore: %v", turrets[0], final["turrets"])
	}
	if powered, _ := na.AsBool(row["powered"]); !powered {
		return fmt.Errorf("turret %s still unpowered after the restore: %v", na.AsString(row["id"]), row)
	}
	if hp, max := na.AsNumber(row["hp"]), na.AsNumber(row["max"]); hp < max {
		return fmt.Errorf("turret %s at %v/%v hit points after the restore", na.AsString(row["id"]), hp, max)
	}
	if err := assertTurretsPowered(final, layout); err != nil {
		return fmt.Errorf("after the restore: %w", err)
	}
	for _, raw := range na.AsSlice(final["colonists"]) {
		row, _ := na.AsMap(raw)
		if drafted, _ := na.AsBool(row["drafted"]); drafted {
			return fmt.Errorf("colonist %s still drafted after the restore", na.AsString(row["id"]))
		}
	}
	return runTurretRearm(ctx, closeClient, reopenHarness, fixture, launch, layout, world, turrets[0], report)
}

// runTurretRearm empties one tier turret's barrel natively with the game's
// own auto-refuel switched off for it, so the only rearm possible is the
// layout goal's forced refuel order, and observes that order issued,
// completed and the barrel refilled.
func runTurretRearm(ctx context.Context, closeClient func() error, reopenHarness func() error, fixture func(string, map[string]any) (map[string]any, error),
	launch func(string) (*service, error), layout store.DefenseLayoutRecord, world store.World, turret domain.Cell, report na.Report) error {
	emptied, err := fixture("empty-barrel", map[string]any{"op": "empty", "x": int(turret.X), "z": int(turret.Z)})
	if err != nil {
		return err
	}
	report["empty_barrel"] = emptied
	emptiedTick := int64(na.AsNumber(emptied["tick"]))
	if err := closeClient(); err != nil {
		return err
	}
	svc, err := launch("rearm")
	if err != nil {
		return err
	}
	defer svc.stop()
	// A freshly launched service has no observed tick rate, so its first
	// colony window is sized at the nominal Ultrafast rate (60000 ticks);
	// under peer load that window runs for minutes with no planner step,
	// and the layout's re-verification (which reads the barrel) only comes
	// with the step after it. The journal is quiet meanwhile, so this wait
	// tolerates two stall budgets (#239).
	wait := svc.wait(repairTimeout)
	wait.Stall *= 2
	rearmed, err := waitTurretRearmed(ctx, svc.store, world, na.AsString(emptied["id"]), emptiedTick, wait)
	report["turret_rearm"] = rearmed
	if err != nil {
		return err
	}
	svc.stop()
	if err := reopenHarness(); err != nil {
		return err
	}
	final, err := fixture("inspect-after-rearm", map[string]any{"op": "inspect"})
	if err != nil {
		return err
	}
	report["inspect_after_rearm"] = final
	row, ok := turretRows(final)[turret]
	if !ok {
		return fmt.Errorf("turret %v gone after the rearm: %v", turret, final["turrets"])
	}
	if auto, _ := na.AsBool(row["autoRefuel"]); auto {
		return fmt.Errorf("turret %s auto-refuel came back on; the refill is not the rearm order's: %v", na.AsString(row["id"]), row)
	}
	if has, _ := na.AsBool(row["hasFuel"]); !has || na.AsNumber(row["fuel"]) <= 0 {
		return fmt.Errorf("turret %s barrel still empty after the rearm: %v", na.AsString(row["id"]), row)
	}
	return nil
}

// waitTurretRearmed polls the journal until the layout goal has issued a
// rearm order for the emptied turret and its plan completed; the record's
// fuel shortage must stay clear (steel is in stock), and the layout goal
// must not have re-opened a tier for it. The barrel is checked natively
// afterwards.
func waitTurretRearmed(ctx context.Context, s *store.Store, world store.World, turretID string, emptiedTick int64, w na.Wait) (map[string]any, error) {
	out := map[string]any{"emptied_tick": emptiedTick, "turret": turretID}
	prefix := "defense-rearm-" + turretID + "-"
	methods := map[string]any{}
	err := na.WaitProgress(ctx, w, func(ctx context.Context) (string, bool, error) {
		review, err := s.LoadRoutineReview(ctx)
		if err != nil {
			return "", false, err
		}
		completed, issued := false, 0
		need := ""
		for _, binding := range review.Goals {
			if binding.Need != policy.EnsureDefensiveLayout {
				continue
			}
			goal, err := s.LoadGoal(ctx, binding.Goal)
			if err != nil {
				if errors.Is(err, store.ErrNotFound) {
					continue
				}
				return "", false, err
			}
			need = string(goal.Goal.Need)
			// Settled plans retire out of goal.Methods; the epoch's method
			// history keeps them.
			history, err := s.LoadGoalMethods(ctx, goal.Goal.ID, goal.Goal.Epoch)
			if err != nil {
				return "", false, err
			}
			for _, m := range history {
				if !strings.HasPrefix(string(m.Method), prefix) {
					continue
				}
				issued++
				entry := map[string]any{"plan": string(m.Plan)}
				if plan, err := s.LoadPlan(ctx, m.Plan); err == nil {
					st := stages(plan.Progress)
					entry["stages"] = st
					for _, stage := range st {
						completed = completed || stage == string(domain.Completed)
					}
				}
				methods[string(m.Method)] = entry
			}
		}
		record, ok, err := s.LoadDefenseLayout(ctx, world)
		if err != nil {
			return "", false, err
		}
		if !ok {
			return "", false, errors.New("stored layout gone before the rearm")
		}
		out["methods"], out["issued"], out["completed"], out["goal_need"] = methods, issued, completed, need
		out["fuel_shortage"], out["verified_tick"], out["standing"] = record.FuelShortage, int64(record.VerifiedTick), record.Standing()
		if len(record.FuelShortage) > 0 {
			return "", false, fmt.Errorf("layout reports a fuel shortage with steel in stock: %+v", record.FuelShortage)
		}
		if completed {
			return "", true, nil
		}
		var progress []string
		for _, name := range sortedKeys(methods) {
			entry, _ := methods[name].(map[string]any)
			st, _ := entry["stages"].([]string)
			progress = append(progress, name+":"+strings.Join(st, ","))
		}
		return na.Signature(issued, completed, need, record.VerifiedTick, record.Standing(), review.Tick, progress), false, nil
	})
	if err != nil {
		return out, fmt.Errorf("turret not rearmed (%#v): %w", out, err)
	}
	return out, nil
}
