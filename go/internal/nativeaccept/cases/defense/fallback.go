package defense

import (
	"context"
	"errors"
	"fmt"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// The #118 fallback scenarios on the committed layout checkpoint. Each
// stages a real RaidEnemy incident the killbox cannot answer as a held
// line and asserts the RoutineDefensePlanner's decision and its native
// outcome:
//
//   - defense/raid-breach: an ordinary edge assault is held from the firing
//     line; once the hold is dispatched the fixture's breach observer
//     teleports one live raider behind the line. The planner must close
//     the hold (its in-flight orders cancelled: the hold_fallback path)
//     and answer with squad defense, and the raid must still resolve with
//     the drafts released and the intruder dead, downed or gone.
//   - defense/siege: a Siege raid (pirates with mortar blueprints) never
//     walks the corridor. The planner must answer it with squad defense --
//     a sortie on the besiegers -- never a line position, attacks must
//     reach native dispatch, and the fight must resolve under combat
//     windows or at least cost the besiegers a pawn.
//   - defense/drop: a CenterDrop assault lands its pods beside the colony.
//     The layout is irrelevant: squad defense at the threat, no defender
//     routed to the firing cells, the raid resolved and the drafts released.

// breachGraceTicks is how long a colonist must have stood drafted before
// the breach observer moves a raider: long enough for the hold plan's
// moves and attacks to dispatch after its drafts.
const breachGraceTicks = 600

// breachCell is where the intruder lands: three cells past the first firing
// cell along the corridor direction, on the colony side of the line.
func breachCell(layout store.DefenseLayoutRecord) domain.Cell {
	d := towardVector(layout.Toward)
	return domain.Cell{X: layout.Firing[0].X + 3*d.X, Z: layout.Firing[0].Z + 3*d.Z}
}

func towardVector(r domain.Rotation) domain.Cell {
	switch r {
	case domain.North:
		return domain.Cell{Z: 1}
	case domain.South:
		return domain.Cell{Z: -1}
	case domain.East:
		return domain.Cell{X: 1}
	default:
		return domain.Cell{X: -1}
	}
}

// dropCell is where a centre drop lands its pods: the first colony
// entrance the layout keeps access to, else the guarded construction site.
func dropCell(layout store.DefenseLayoutRecord, siteX, siteZ int) domain.Cell {
	if len(layout.Entrances) > 0 {
		return layout.Entrances[0]
	}
	return domain.Cell{X: int32(siteX), Z: int32(siteZ)}
}

// waitHoldFallback waits for the breach to be answered: the hold plan
// closed (its in-flight orders cancelled by the planner's hold_fallback, or
// already complete, and its drafts released by the worker once the plan no
// longer holds them) and a squad method admitted after it on the same
// goal. hold_cancelled counts the orders the fallback cut short.
func waitHoldFallback(ctx context.Context, s *store.Store, hold domain.PlanID, w na.Wait) (map[string]any, error) {
	out := map[string]any{}
	err := na.WaitProgress(ctx, w, func(ctx context.Context) (string, bool, error) {
		state, err := s.LoadPlan(ctx, hold)
		if err != nil {
			return "", false, err
		}
		st := stages(state.Progress)
		cancelled := 0
		for _, p := range state.Progress {
			if p.View().Stage == domain.Cancelled {
				cancelled++
			}
		}
		open := domain.GoalWorkOpen(state.Progress)
		out["hold_stages"] = st
		out["hold_cancelled"] = cancelled
		review, err := s.LoadRoutineReview(ctx)
		if err != nil {
			return "", false, err
		}
		var squad []string
		need := ""
		for _, binding := range review.Goals {
			if binding.Need != policy.ActiveCombat {
				continue
			}
			goal, err := s.LoadGoal(ctx, binding.Goal)
			if err != nil && !errors.Is(err, store.ErrNotFound) {
				return "", false, err
			}
			need = string(goal.Goal.Need)
			for _, m := range goal.Methods {
				if strings.HasPrefix(string(m.Method), "squad-") {
					squad = append(squad, string(m.Plan))
				}
			}
		}
		out["squad_plans"] = squad
		out["combat_goal_need"] = need
		if !open && len(squad) > 0 {
			return "", true, nil
		}
		if need == string(domain.NeedRecovered) && len(squad) == 0 {
			return "", false, fmt.Errorf("the raid resolved (%v) before the breach was answered by squad defense", st)
		}
		return na.Signature(st, open, squad, need), false, nil
	})
	if err != nil {
		return out, fmt.Errorf("breach not answered by squad defense (%#v): %w", out, err)
	}
	return out, nil
}

// waitSquadEngaged waits until an attack of some squad method on the
// ActiveCombat goal has reached native dispatch: the sortie left the
// journal, whatever the fight then brings.
func waitSquadEngaged(ctx context.Context, s *store.Store, w na.Wait) (map[string]any, error) {
	out := map[string]any{}
	err := na.WaitProgress(ctx, w, func(ctx context.Context) (string, bool, error) {
		review, err := s.LoadRoutineReview(ctx)
		if err != nil {
			return "", false, err
		}
		attacks := map[string]string{}
		dispatched := 0
		for _, binding := range review.Goals {
			if binding.Need != policy.ActiveCombat {
				continue
			}
			goal, err := s.LoadGoal(ctx, binding.Goal)
			if err != nil && !errors.Is(err, store.ErrNotFound) {
				return "", false, err
			}
			for _, m := range goal.Methods {
				if !strings.HasPrefix(string(m.Method), "squad-") {
					continue
				}
				state, err := s.LoadPlan(ctx, m.Plan)
				if err != nil {
					return "", false, err
				}
				for _, p := range state.Progress {
					kind := p.Action().Kind()
					if kind != domain.RangedAttackAction && kind != domain.MeleeAttackAction {
						continue
					}
					v := p.View()
					attacks[string(m.Plan)+"/"+string(v.Action)] = string(v.Stage)
					if v.Attempt > 0 {
						dispatched++
					}
				}
			}
		}
		out["attacks"] = attacks
		out["dispatched"] = dispatched
		if dispatched > 0 {
			return "", true, nil
		}
		return na.Signature(attacks), false, nil
	})
	if err != nil {
		return out, fmt.Errorf("no squad attack reached native dispatch (%#v): %w", out, err)
	}
	return out, nil
}

// runSquadRaid plays a raid the layout cannot hold (siege, centre drop)
// from the staged incident to the native outcome: squad defense admitted
// with no defender sent to a firing cell, an attack dispatched, the fight
// resolved under combat windows (the goal recovered or unbound) within the
// budget, the drafts released, and natively no colonist left drafted and
// the raiders dead, downed or gone. A siege the colony cannot finish
// within the budget still passes when its sortie cost the besiegers a pawn:
// the decision and the engagement are what the case proves, the killbox
// never being involved.
func runSquadRaid(ctx context.Context, closeClient func() error, reopenHarness func() error,
	fixture func(string, map[string]any) (map[string]any, error), launch func(string) (*service, error),
	layout store.DefenseLayoutRecord, v variant, raid map[string]any, report na.Report) error {
	if err := closeClient(); err != nil {
		return err
	}
	svc, err := launch("raid")
	if err != nil {
		return err
	}
	defer svc.stop()
	method, err := waitCombatMethod(ctx, svc.store, svc.wait(raidTimeout), report)
	if err != nil {
		return fmt.Errorf("combat response: %w", err)
	}
	if !strings.HasPrefix(string(method.Method), "squad-") {
		return fmt.Errorf("combat method %q, expected prefix %q", method.Method, "squad-")
	}
	plan, err := svc.store.LoadPlan(ctx, method.Plan)
	if err != nil {
		return err
	}
	if err := assertNoLinePosition(plan.Spec, layout); err != nil {
		return err
	}
	raiders := map[string]bool{}
	for _, raw := range na.AsSlice(raid["added"]) {
		row, _ := na.AsMap(raw)
		raiders[na.AsString(row["id"])] = true
	}
	for _, action := range plan.Spec.Actions() {
		target := ""
		if a, ok := action.RangedAttack(); ok {
			target = string(a.Target())
		} else if a, ok := action.MeleeAttack(); ok {
			target = string(a.Target())
		} else {
			continue
		}
		if !raiders[target] {
			return fmt.Errorf("squad plan %s attacks %s, not one of the raid's pawns %v", plan.Spec.ID(), target, sortedKeys(raiders))
		}
	}
	engaged, err := waitSquadEngaged(ctx, svc.store, svc.wait(raidTimeout))
	report["squad_engaged"] = engaged
	if err != nil {
		return err
	}
	resolved, resolveErr := waitHuntResolved(ctx, svc.store, svc.wait(raidTimeout), true)
	report["raid_resolution"] = resolved
	if resolveErr == nil {
		released, err := waitDefendersReleased(ctx, svc.store, method.Plan, "squad-", svc.wait(raidTimeout))
		report["defenders_released"] = released
		if err != nil {
			return fmt.Errorf("draft release after raid: %w", err)
		}
	} else if v.strategy != "Siege" {
		return fmt.Errorf("raid resolution: %w", resolveErr)
	}
	svc.stop()
	report["raid_authority"] = svc.keepAlive.snapshot()
	if err := reopenHarness(); err != nil {
		return err
	}
	final, err := fixture("inspect-after-raid", map[string]any{"op": "inspect"})
	if err != nil {
		return err
	}
	report["inspect_after_raid"] = final
	neutralised, left := raidOutcome(final, raiders)
	report["raid_outcome"] = map[string]any{"hostiles_on_map": left, "dead_or_downed": neutralised, "resolved": resolveErr == nil}
	if resolveErr != nil {
		if neutralised == 0 {
			return fmt.Errorf("siege neither resolved nor cost the besiegers a pawn: %w", resolveErr)
		}
		return nil
	}
	for _, raw := range na.AsSlice(final["colonists"]) {
		row, _ := na.AsMap(raw)
		if drafted, _ := na.AsBool(row["drafted"]); drafted {
			return fmt.Errorf("colonist %s still drafted after the raid resolved", na.AsString(row["id"]))
		}
	}
	if neutralised == 0 && left == len(raiders) {
		return fmt.Errorf("raid did not resolve natively: no raider dead, downed or gone: %#v", final)
	}
	return nil
}

// raidOutcome counts the raid's pawns the inspect still lists, and how many
// of those are dead or downed.
func raidOutcome(final map[string]any, raiders map[string]bool) (neutralised, left int) {
	for _, raw := range na.AsSlice(final["hostiles"]) {
		row, _ := na.AsMap(raw)
		if !raiders[na.AsString(row["id"])] {
			continue
		}
		left++
		if dead, _ := na.AsBool(row["dead"]); dead {
			neutralised++
		} else if downed, _ := na.AsBool(row["downed"]); downed {
			neutralised++
		}
	}
	return neutralised, left
}

// runBreach plays the edge raid on from the dispatched hold: the fixture's
// observer moves one raider behind the line, the hold must close for a
// squad method, the raid must resolve under combat windows with every
// draft released, and natively the observer must report the move, the
// intruder must be dead, downed or gone and no colonist left drafted.
func runBreach(ctx context.Context, svc *service, reopenHarness func() error,
	fixture func(string, map[string]any) (map[string]any, error), hold domain.PlanID, raid map[string]any, report na.Report) error {
	fallback, err := waitHoldFallback(ctx, svc.store, hold, svc.wait(raidTimeout))
	report["hold_fallback"] = fallback
	if err != nil {
		return err
	}
	resolved, err := waitHuntResolved(ctx, svc.store, svc.wait(raidTimeout), true)
	report["raid_resolution"] = resolved
	if err != nil {
		return fmt.Errorf("raid resolution: %w", err)
	}
	released, err := waitDefendersReleased(ctx, svc.store, hold, "", svc.wait(raidTimeout))
	report["defenders_released"] = released
	if err != nil {
		return fmt.Errorf("draft release after raid: %w", err)
	}
	svc.stop()
	report["raid_authority"] = svc.keepAlive.snapshot()
	if err := reopenHarness(); err != nil {
		return err
	}
	final, err := fixture("inspect-after-raid", map[string]any{"op": "inspect"})
	if err != nil {
		return err
	}
	report["inspect_after_raid"] = final
	breach, ok := na.AsMap(final["breach"])
	if !ok {
		return fmt.Errorf("inspect reports no breach observer (stale fixture build?): %#v", final)
	}
	intruder := na.AsString(breach["moved"])
	if intruder == "" {
		return fmt.Errorf("the breach observer never moved a raider: %#v", breach)
	}
	raiders := map[string]bool{}
	for _, raw := range na.AsSlice(raid["added"]) {
		row, _ := na.AsMap(raw)
		raiders[na.AsString(row["id"])] = true
	}
	if !raiders[intruder] {
		return fmt.Errorf("the breach observer moved %s, not one of the raid's pawns %v", intruder, sortedKeys(raiders))
	}
	for _, raw := range na.AsSlice(final["hostiles"]) {
		row, _ := na.AsMap(raw)
		if na.AsString(row["id"]) != intruder {
			continue
		}
		dead, _ := na.AsBool(row["dead"])
		downed, _ := na.AsBool(row["downed"])
		if !dead && !downed {
			return fmt.Errorf("the intruder %s is still up after the raid resolved: %#v", intruder, row)
		}
	}
	for _, raw := range na.AsSlice(final["colonists"]) {
		row, _ := na.AsMap(raw)
		if drafted, _ := na.AsBool(row["drafted"]); drafted {
			return fmt.Errorf("colonist %s still drafted after the raid resolved", na.AsString(row["id"]))
		}
	}
	neutralised, left := raidOutcome(final, raiders)
	report["raid_outcome"] = map[string]any{"hostiles_on_map": left, "dead_or_downed": neutralised, "intruder": intruder, "intruded_tick": breach["movedTick"]}
	if neutralised == 0 && left == len(raiders) {
		return fmt.Errorf("raid did not resolve natively: no raider dead, downed or gone: %#v", final)
	}
	return nil
}
