// Package defense holds the defensive-layout vertical (issue #5, B06c; the
// v2 perimeter, #789) end to end against a live game and a live rimgovernor
// "serve" service. On the tribal8 baseline with stone blocks stocked, the
// RoundsDefenseLayoutPlanner anchors its corridor on the layout plan's
// killbox opening, builds every tier natively (firing line, funnel, trap
// corridor) and the plan's 3-thick stone perimeter wall with its 3-door
// gates, section by section. The stored layout is re-verified by an
// independent native spatial-access read with every wall and barricade
// blocked (the gates' doors open for colonists, so every colonist still
// reaches the killbox entry and every colony door through them) and by a
// native inspection that no colonist stands on a trap. A real RaidEnemy
// edge assault is then raised at the opening and the RoundsDefensePlanner
// must open one fight (method "combat-…") formed as hold-the-line (#852). The service then
// holds the raid itself -- the scheduler admits bounded combat watch
// windows acknowledging the live hostiles while the ActiveCombat goal has
// an admitted plan (#69) -- and the run waits for the goal to recover, then
// for the aftermath (#72): the hold plan's drafts are released and the
// layout planner re-admits every tier the raid degraded (a sprung spike
// trap is destroyed; a breached wall is gone) until the stored record is
// verified standing again within a bounded number of ticks. The repair is
// the planner's own (#117): before the repair service starts, the fixture
// switches the game's auto-rebuild off, removes its pending trap blueprints
// and vanishes a surviving trap, so every missing trap can only be replaced
// by a re-admitted tier method. Natively the run then asserts that the
// raiders are dead or downed, at least one trap in the opening sprung, the
// trap and wall counts match the audited layout, the breached cell holds a
// trap again and no colonist is left drafted.
//
// The other threat responses and layout decisions (bypass, breach, siege,
// drop, predator, hostile buildings, turrets, cover, stocked projection)
// are go-test snapshot replays in
// internal/buildingruntime/rounds_defense_cases_test.go (#744).
//
// Only one GABP client may hold the game at a time: the case's fixture
// session and the service's session are used strictly in turn.
package defense

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/routinefamily"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
)

const (
	// layoutTimeout bounds the native layout build (the killbox's tiers and
	// every perimeter section), raidTimeout the raid response and
	// resolution, repairTimeout the post-raid draft release and layout
	// repair in wall time (raid injuries are tended first: CriticalMedical
	// suspends the layout goal) and repairTicks the same in game ticks from
	// the tick the raid resolved (one day).
	layoutTimeout = 150 * time.Minute
	raidTimeout   = 8 * time.Minute
	repairTimeout = 15 * time.Minute
	repairTicks   = int64(60000)
	// perimeterStone blocks of granite cover the wall: 3 cells thick round the
	// core, the yard and the fields, 5 blocks a wall and 25 a door.
	perimeterStone = 14000
	// woodLogs of wood are what the killbox funnel, trap
	// corridor, fences, doors and floors are built from (defenseDefinitions
	// stuffs them all with wood): the case serves no resource family, so
	// nothing fells trees and an unstocked colony leaves the funnel's
	// blueprints waiting for wood that never comes (#2134).
	woodLogs = 1500
)

// fixtureFunc calls one test/defense_setup op, refusing an unsuccessful one.
type fixtureFunc func(label string, args map[string]any) (map[string]any, error)

// variant is one case's scenario: the edge raid staged after the layout
// (its RaidStrategyDef/PawnsArrivalModeDef), and the hooks a case built on
// the perimeter campaign adds (#1211): gates stages extra layout gates
// before the layout is built, layoutBuilt audits the built layout against
// the native inspect after it, and raided compares that inspect with the
// one taken as soon as the raid resolved.
type variant struct {
	strategy, arrival, threat string
	gates                     func(fixture fixtureFunc, siteX, siteZ int) (map[string]any, error)
	layoutBuilt               func(layout store.DefenseLayoutRecord, inspect map[string]any) error
	raided                    func(afterLayout, afterRaid map[string]any) error
	// herd stages a roamer before the layout and asserts its barn-bound then
	// paddock phases around the layout build (defense/paddock, #2236).
	herd *paddockHerd
	// instantWalls has the fixture raise every wall, door and embrasure as
	// it is placed (the perimeter is ~1600 walls: hauling and building them
	// took 36 wall minutes of game time, the layout itself is what the case
	// proves, #2134).
	instantWalls bool
}

// perimeterFamilies are the families a perimeter campaign serves; see init.
//
// work staffs the layout's construction: every building method's builder
// check needs the work priorities only that family applies (#1248).
var perimeterFamilies = []routinefamily.Family{routinefamily.DefensiveLayout, routinefamily.Defense, routinefamily.Tend, routinefamily.Rescue, routinefamily.Fire, routinefamily.Supply, routinefamily.Work}

func init() {
	// The fire family belongs here: a raid can leave a home fire burning
	// on the corridor, and MaintainFireSafety is a priority-1 emergency
	// whose only method is a short native-firefighting window. Without the
	// family nothing admits one, so the fire holds every other goal
	// suspended while the clock is refused no_work and the tick never moves
	// -- the post-raid repair stall of #221. The supply family belongs here
	// too: a dead raider's drops lie in the trap lane, where the hauling
	// safety verdict flaps unsafe, and ManageSupplySafety is then a
	// priority-0 emergency that only the supplies planner clears by
	// forbidding the stack; without it every development goal, the layout's
	// cover clearance included, stays unselected after the raid (#620).
	spec := &cases.ServeSpec{Families: perimeterFamilies, Prefix: "defense"}
	// The baseline save keeps the site deterministic.
	baseline := cases.Save{Name: sustained.BaselineSave}
	v := variant{strategy: "ImmediateAttack", arrival: "EdgeWalkIn", threat: "raid"}
	cases.Register(cases.Case{
		Name: "defense/perimeter",
		Scope: "Layout v2 perimeter (#789) and the defensive layout vertical (#5 M4): the routine planner anchors the corridor on the layout plan's killbox " +
			"opening and builds every tier and the 3-thick stone wall with its 3-door gates natively; independent spatial-access and trap-cell reads " +
			"prove colonists pass the gates; a real RaidEnemy edge assault walks to the opening and is answered with hold-the-line, and afterwards " +
			"the defenders are undrafted and the layout is repaired to its audited state (#72).",
		Start: baseline, Serve: spec, Budget: 3 * time.Hour, Crew: cases.Crew{Size: 20},
		Reason: "the whole perimeter's build, the raid answer and the repair are one native campaign",
		Run:    func(ctx context.Context, s cases.Session) error { return run(ctx, s, v) },
	})
}

type apiFunc func(method, path string, body map[string]any, token string) (map[string]any, int, error)

// service is one launch of the rimgovernor serve subprocess against the
// shared game, with the harness's player authority acquired and kept alive.
type service struct {
	*na.ServiceProcess
	stopped   bool
	keepAlive *authorityKeepAlive
	stopKeep  context.CancelFunc
	keepWG    sync.WaitGroup
	store     *store.Store
}

func (s *service) stop() {
	if s.stopped {
		return
	}
	s.stopped = true
	if s.stopKeep != nil {
		s.stopKeep()
		s.keepWG.Wait()
	}
	// Hand native authority back to manual before the kill: a killed
	// service leaves the native side in auto mode under its dead session,
	// and the next service's acquire would resolve uncertain against it.
	if s.keepAlive != nil {
		_, _, _ = s.API("POST", "/api/player/control/pause", map[string]any{
			"requestId": fmt.Sprintf("defense-%s-pause-%d", s.keepAlive.name, time.Now().UnixNano()), "expected": s.keepAlive.identity,
		}, s.Token)
	}
	if s.store != nil {
		s.store.Close()
	}
	s.ServiceProcess.Stop()
}

// wait bounds a journal poll by budget, the shared stall budget and the
// service's own exit.
func (s *service) wait(budget time.Duration) na.Wait {
	return na.Wait{Ceiling: budget, Stall: na.StallBudget(), Terminal: s.Exited}
}

func run(ctx context.Context, s cases.Session, v variant) error {
	report, h, identity := s.Report(), s.Harness(), s.Identity()
	output := s.Config().Output
	report["raid"] = map[string]any{"strategy": v.strategy, "arrival": v.arrival, "threat": v.threat}
	// Releasing the harness before a launch is the service's own business:
	// na.Serve frees the sole GABP slot itself. closeClient marks the
	// hand-over points.
	closeClient := func() error { return nil }
	// The game frees the service's GABP slot shortly after the
	// service is killed, not synchronously: Reattach retries. The service
	// leaves the game running, and every fixture op needs a paused map, so
	// the reattached session pauses first.
	reopenHarness := func() (*na.Harness, error) {
		h, err := s.Reattach(ctx)
		if err != nil {
			return nil, err
		}
		if _, err := h.Call(ctx, "pause", "rimgovernor/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
			return nil, err
		}
		return h, nil
	}
	world := store.World{Colony: domain.ColonyID(na.AsString(identity["colonyId"])), Load: domain.LoadID(na.AsString(identity["loadToken"])), Map: domain.MapID(na.AsNumber(identity["mapId"]))}
	for _, want := range []string{"test/defense_setup", "test/guarded_construction_prepare", na.LabSpawnTool} {
		if !na.Contains(s.Names(), want) {
			return fmt.Errorf("missing %s in discovery; rebuild the native mod with -Fixture DefenseFixture,GuardedConstructionFixture", want)
		}
	}

	var fixture fixtureFunc = func(label string, args map[string]any) (map[string]any, error) {
		out, err := h.Call(ctx, label, "test/defense_setup", args)
		if err != nil {
			return nil, err
		}
		if success, _ := na.AsBool(out["success"]); !success {
			return nil, fmt.Errorf("defense_setup %s refused: %#v", args["op"], out)
		}
		return out, nil
	}
	var layout store.DefenseLayoutRecord
	// na.Serve keeps every service on this one state journal.
	statePath := filepath.Join(output, "service.sqlite")
	var svc *service
	launch := func(name string) (*service, error) {
		return launchService(ctx, s, name, identity, report)
	}
	// The band, the stock, the rifles and the construction site are
	// staged here, in the run body, so a resume from the ring (#249)
	// finds them in the save already: the resumed entry's state names
	// the site the fresh run chose, and the prep is skipped rather than
	// laid again on the colonists' shifted centre (#316).
	var staged bool
	if entry, ok := s.Resumed(); ok {
		if state, ok := na.AsMap(entry.State["fixture"]); ok {
			staged = true
			report["fixture_resumed"] = state
		}
	}
	if v.instantWalls {
		if _, err := fixture("instant-shells", map[string]any{"op": "instant-shells"}); err != nil {
			return err
		}
	}
	if !staged {
		if _, err := fixture("stock", map[string]any{"op": "stock"}); err != nil {
			return err
		}
		ranged, err := fixture("ranged", map[string]any{"op": "ranged", "rifles": 3})
		if err != nil {
			return err
		}
		report["ranged"] = ranged
		siteX, siteZ, err := prepareSite(ctx, h, identity, report)
		if err != nil {
			return err
		}
		if v.herd != nil {
			if err := v.herd.stage(ctx, h, siteX, siteZ, report); err != nil {
				return err
			}
		}
		if v.gates != nil {
			gates, err := v.gates(fixture, siteX, siteZ)
			if err != nil {
				return err
			}
			report["gates"] = gates
		}
		before, err := fixture("inspect-before", map[string]any{"op": "inspect"})
		if err != nil {
			return err
		}
		report["inspect_before"] = before
		if int(na.AsNumber(before["traps"])) != 0 {
			return fmt.Errorf("fresh colony already has traps: %#v", before)
		}
		na.SetCheckpointState("fixture", map[string]any{"siteX": siteX, "siteZ": siteZ})
	}
	// A resumed store may already hold the finished layout (the run
	// failed past it): then the layout service has nothing to build.
	if staged {
		stored, err := store.Open(ctx, statePath)
		if err != nil {
			return fmt.Errorf("open resumed store: %w", err)
		}
		r, ok, err := stored.LoadDefenseLayout(ctx, world)
		if closeErr := stored.Close(); err == nil {
			err = closeErr
		}
		if err != nil {
			return fmt.Errorf("resumed layout: %w", err)
		}
		if ok && r.Complete {
			layout = r
			data, _ := json.Marshal(layout)
			report["layout_record"] = json.RawMessage(data)
		}
	}
	if !layout.Complete {
		var err error
		if err = closeClient(); err != nil {
			return fmt.Errorf("close fixture-prep bridge session: %w", err)
		}

		// Scenario 1: the layout is planned on the constrained site and every
		// tier is built natively under the live rounder/planner.
		svc, err = launch("layout")
		if err != nil {
			return err
		}
		defer svc.stop()
		env := phaseEnv{ctx: ctx, world: world, statePath: statePath, identity: identity, report: report, reopen: reopenHarness, launch: launch}
		if v.herd != nil {
			// The roamer is barn-bound while the ring is open: the service
			// stops once it is, the game is read, and a fresh one continues.
			if svc, h, err = v.herd.barnBound(env, svc); err != nil {
				return fmt.Errorf("barn-bound: %w", err)
			}
			defer svc.stop()
		}
		layout, err = waitLayoutComplete(ctx, svc.store, world, svc.wait(layoutTimeout), report)
		if err != nil {
			return fmt.Errorf("layout: %w", err)
		}
		if v.herd != nil {
			if err = v.herd.awaitPaddock(env, svc); err != nil {
				return fmt.Errorf("paddock: %w", err)
			}
		}
		svc.stop()
		report["layout_authority"] = svc.keepAlive.snapshot()

		h, err = reopenHarness()
		if err != nil {
			return err
		}
		if v.herd != nil {
			if err = v.herd.paddocked(env, h); err != nil {
				return fmt.Errorf("paddock: %w", err)
			}
		}
	}
	// The audits below are cheap reads proving the layout stands.
	after, err := fixture("inspect-after-layout", map[string]any{"op": "inspect"})
	if err != nil {
		return err
	}
	report["inspect_after_layout"] = after
	trapTier, _, _ := layout.Tier(policy.TierTrapCorridor)
	wantTraps := 0
	for _, b := range trapTier.Buildings {
		if b.Definition == "TrapSpike" {
			wantTraps++
		}
	}
	if traps := int(na.AsNumber(after["traps"])); traps < wantTraps || traps == 0 {
		return fmt.Errorf("native trap count %d below the layout's %d", traps, wantTraps)
	}
	if on := na.AsSlice(after["colonistsOnTraps"]); len(on) != 0 {
		return fmt.Errorf("colonists standing on trap cells after layout: %v", on)
	}
	if v.layoutBuilt != nil {
		if err := v.layoutBuilt(layout, after); err != nil {
			return err
		}
	}
	// Independent audit: with every wall, fence and barricade of the layout
	// blocked (traps stay walkable for colonists, the safe lane's doors
	// open for them, and the shooter floors are terrain) every colonist
	// still reaches the entry and every colony door, and nobody lost a
	// cell.
	var impassable []domain.Cell
	seen := map[domain.Cell]bool{}
	// A Fence cell is PassThroughOnly: native pathing lets a colonist through,
	// but the audit projects over Walkable() cells, which exclude it, so a
	// fenced target is held to the native CanReach alone.
	fenced := map[domain.Cell]bool{}
	for _, tier := range layout.Tiers {
		for _, b := range tier.Buildings {
			// A Fence is PassThroughOnly (the killbox lane's, #2231): only
			// roamers are stopped, so colonists pass it.
			if b.Definition == "Fence" {
				fenced[b.Cell] = true
			}
			if b.Definition != "TrapSpike" && b.Definition != "Door" && b.Definition != "WoodPlankFloor" && b.Definition != "Fence" {
				if seen[b.Cell] {
					continue
				}
				seen[b.Cell] = true
				impassable = append(impassable, b.Cell)
			}
		}
	}
	access, err := h.Wire(ctx, "spatial-access", "observations_read_spatial_access", map[string]any{
		"scope": map[string]any{"expectedIdentity": identity}, "blockedCells": cellsJSON(impassable), "targetCells": cellsJSON(append([]domain.Cell{layout.Entry}, layout.Entrances...)),
	})
	if err != nil {
		return err
	}
	// The three audits below read one finished layout and do not depend on
	// each other, so each is recorded and the run goes on: one run lists every
	// stale or failing check.
	report.Expect("spatial access after layout", assertAccess(access, fenced))
	report["spatial_access_after_layout"] = access
	// Cover for the firing line: every firing cell sees some cell of the
	// trap lane raiders must walk, the chokepoint mouth is covered, and the
	// line has cover. Flank cells sit behind the funnel walls, so the mouth
	// itself is only visible from the centre.
	// The native read takes at most 64 cells a side, so a longer trap lane is
	// read in chunks and the lines merged.
	var lines []any
	const chunk = 64
	for i := 0; i < len(layout.TrapLane); i += chunk {
		fire, err := h.Wire(ctx, fmt.Sprintf("lines-of-fire-%d", i/chunk), "observations_read_lines_of_fire", map[string]any{
			"scope": map[string]any{"expectedIdentity": identity}, "firingCells": cellsJSON(layout.Firing), "approachCells": cellsJSON(layout.TrapLane[i:min(i+chunk, len(layout.TrapLane))]),
		})
		if err != nil {
			return err
		}
		_, observed, err := na.Outcome(fire, "observed")
		if err != nil {
			return fmt.Errorf("lines of fire after layout: %w", err)
		}
		lines = append(lines, na.AsSlice(observed["lines"])...)
	}
	report["lines_of_fire_after_layout"] = lines
	report.Expect("lines of fire after layout", assertCover(lines, layout))
	// The perimeter stood with its gates: the access audit above blocked
	// every wall of it, so colonists reached the entry through the doors.
	sections, walls, doors := 0, 0, 0
	for _, tier := range layout.Tiers {
		if !policy.IsPerimeterTier(tier.Name) || len(tier.Buildings) == 0 {
			continue
		}
		sections++
		if !tier.Built {
			report.Expect("perimeter built", fmt.Errorf("perimeter section %s not built", tier.Name))
		}
		for _, b := range tier.Buildings {
			if b.Definition == "Door" {
				doors++
			} else {
				walls++
			}
		}
	}
	report["perimeter"] = map[string]any{"sections": sections, "walls": walls, "gate_doors": doors}
	if sections == 0 || doors < 3 {
		report.Expect("perimeter gates", fmt.Errorf("layout has no perimeter wall with gates: %d sections, %d gate doors", sections, doors))
	}

	// Scenario 2/3: a real edge raid.
	// The colonists may be parked inside the corridor they had been
	// building. A hold plan drafts and moves
	// them one action per window, so a raider reaching the corridor
	// first bounced authority on a colonist-health stop and settled
	// the plan before dispatch (#222). The line is held from the
	// firing positions, so stand the defenders there before the raid;
	// the plan's own drafts and moves are still what the run asserts.
	cells := make([]string, 0, len(layout.Firing))
	for _, f := range layout.Firing {
		cells = append(cells, fmt.Sprintf("%d,%d", f.X, f.Z))
	}
	mustered, err := fixture("muster", map[string]any{"op": "muster", "cells": strings.Join(cells, ";")})
	if err != nil {
		return err
	}
	report["muster"] = mustered
	// Hold-the-line needs the raid to come through the corridor the cover
	// row faces: it walks in from the map edge nearest the corridor entry
	// rather than any edge the raid worker picks. A raider arriving behind
	// the cover row rightly gets no hold (#681). The nearest edge to the
	// entry can be a flank one cell closer than the edge the corridor
	// faces; a raider walking in from the side crosses the cover row far
	// off the line and rightly gets no hold (#714), so pin the edge the
	// corridor faces: Toward runs from that edge toward Home.
	raidArgs := map[string]any{"op": "raid", "strategy": v.strategy, "arrival": v.arrival,
		"x": int(layout.Entry.X), "z": int(layout.Entry.Z), "side": edgeSide(layout.Toward)}
	// The ring stops here: a resume replays the pre-raid audits above, and
	// a world captured after the raid has sprung traps the repair may not
	// have replaced yet, so every resume starts from a pre-raid entry and
	// stages the raid again (#330).
	na.CapCheckpoints("raid staged; a resume replays the pre-raid audits, so no entry is taken after this point (#330)")
	raid, err := fixture("raid", raidArgs)
	if err != nil {
		return err
	}
	report["raid_incident"] = raid
	if err := closeClient(); err != nil {
		return err
	}
	svc, err = launch("raid")
	if err != nil {
		return err
	}
	defer svc.stop()
	method, err := waitCombatMethod(ctx, svc.store, svc.wait(raidTimeout), report)
	if err != nil {
		return fmt.Errorf("combat response: %w", err)
	}
	if !strings.HasPrefix(string(method.Method), "combat-") {
		return fmt.Errorf("combat method %q, expected prefix %q", method.Method, "combat-")
	}
	fight, ok, err := svc.store.LoadCombatFight(ctx, method.Plan)
	if err != nil {
		return err
	}
	if !ok {
		return fmt.Errorf("combat plan %s has no fight record", method.Plan)
	}
	if err := assertHoldPlan(fight.Memory, layout); err != nil {
		return err
	}
	// The service holds the raid: with the hold plan admitted the
	// scheduler runs bounded combat windows that acknowledge the live
	// hostiles, re-planning between them, until the ActiveCombat goal
	// recovers. The fixture can only inspect from the harness session,
	// so the outcome is read natively once the service is gone.
	dispatched, err := waitHoldDispatched(ctx, svc.store, method.Plan, svc.wait(raidTimeout/2))
	report["hold_plan_dispatch"] = dispatched
	if err != nil {
		return fmt.Errorf("hold dispatch: %w", err)
	}
	resolved, err := waitRaidResolved(ctx, svc.store, method.Plan, svc.wait(raidTimeout))
	report["raid_resolution"] = resolved
	if err != nil {
		return fmt.Errorf("raid resolution: %w", err)
	}
	// Scenario 4: retreat and repair. The recovered goal no longer
	// authorizes the hold plan, so its drafts are released; the layout
	// planner re-verifies the record after the combat epoch, re-opens
	// every tier that lost a building and rebuilds it.
	resolvedTick, _ := resolved["resolved_tick"].(int64)
	released, err := waitDefendersReleased(ctx, svc.store, method.Plan, "combat-", svc.wait(repairTimeout))
	report["defenders_released"] = released
	if err != nil {
		return fmt.Errorf("draft release after raid: %w", err)
	}
	// The wounded are staged healed before the repair phase: a
	// CriticalMedical hold suspends every routine goal, the controller's
	// tend order has no native preview yet, and the fixture colony has no
	// bed for the game's own doctors to use. Fixture ops need the harness
	// session, so the repair phase runs under a fresh service on the
	// same journal.
	svc.stop()
	report["raid_authority"] = svc.keepAlive.snapshot()
	h, err = reopenHarness()
	if err != nil {
		return err
	}
	if v.raided != nil {
		raided, err := fixture("inspect-raid-resolved", map[string]any{"op": "inspect"})
		if err != nil {
			return err
		}
		report["inspect_raid_resolved"] = raided
		if err := v.raided(after, raided); err != nil {
			return err
		}
	}
	healed, err := fixture("heal-after-raid", map[string]any{"op": "heal"})
	if err != nil {
		return err
	}
	report["healed_after_raid"] = healed
	// The game's auto-rebuild must not repair the corridor for the
	// planner: its pending trap blueprints are removed and one more
	// trap vanishes (when any survived), so the traps missing now are
	// the planner's to replace.
	breach, err := fixture("breach-after-raid", map[string]any{"op": "breach"})
	if err != nil {
		return err
	}
	report["breach_after_raid"] = breach
	missingTraps := int(na.AsNumber(after["traps"])) - int(na.AsNumber(breach["standing"]))
	if missingTraps < 1 {
		return fmt.Errorf("breach left no trap missing: %#v", breach)
	}
	if err := closeClient(); err != nil {
		return err
	}
	svc, err = launch("repair")
	if err != nil {
		return err
	}
	defer svc.stop()
	repaired, err := waitLayoutRepaired(ctx, svc.store, world, resolvedTick, repairTicks, svc.wait(repairTimeout))
	report["layout_repair"] = repaired
	if err != nil {
		return fmt.Errorf("layout repair after raid: %w", err)
	}
	if rebuilt, _ := repaired["traps_rebuilt"].(int); rebuilt < missingTraps {
		return fmt.Errorf("planner rebuilt %d traps in its repair methods, %d were missing after the breach: %#v", rebuilt, missingTraps, repaired)
	}
	svc.stop()
	report["repair_authority"] = svc.keepAlive.snapshot()

	h, err = reopenHarness()
	if err != nil {
		return err
	}
	final, err := fixture("inspect-after-raid", map[string]any{"op": "inspect"})
	if err != nil {
		return err
	}
	report["inspect_after_raid"] = final
	if on := na.AsSlice(final["colonistsOnTraps"]); len(on) != 0 {
		return fmt.Errorf("colonists standing on trap cells after raid: %v", on)
	}
	// A spike trap is trapDestroyOnSpring: springing destroys it, so a
	// trap id from the layout audit that is gone now was sprung, and an
	// id that is new now is its replacement (the planner's re-admitted
	// tier: the breach removed the game's own auto-rearm blueprints);
	// the fixture's armed-state count only covers rearmable traps.
	sprung, rebuilt := trapIDDelta(after, final)
	report["traps_sprung"] = len(sprung) + int(na.AsNumber(final["sprung"]))
	report["traps_sprung_ids"] = sprung
	report["traps_rebuilt_ids"] = rebuilt
	if len(sprung)+int(na.AsNumber(final["sprung"])) == 0 {
		return fmt.Errorf("no trap sprung during the edge raid: %#v", final)
	}
	// The repaired layout matches the audited one natively (every
	// sprung trap replaced, no wall missing), and nobody is left
	// drafted once the raid is over.
	if len(rebuilt) < len(sprung) {
		return fmt.Errorf("%d traps sprung (%v) but %d rebuilt (%v)", len(sprung), sprung, len(rebuilt), rebuilt)
	}
	if traps, want := int(na.AsNumber(final["traps"])), int(na.AsNumber(after["traps"])); traps < want {
		return fmt.Errorf("native trap count %d after repair, %d after the layout audit", traps, want)
	}
	if breached, ok := na.AsMap(breach["breached"]); ok && !hasCell(na.AsSlice(final["trapCells"]), int(na.AsNumber(breached["x"])), int(na.AsNumber(breached["z"]))) {
		return fmt.Errorf("breached trap cell %v holds no trap after repair: %v", breached, final["trapCells"])
	}
	if walls, want := len(na.AsSlice(final["walls"])), len(na.AsSlice(after["walls"])); walls < want {
		return fmt.Errorf("native wall count %d after repair, %d after the layout audit", walls, want)
	}
	for _, raw := range na.AsSlice(final["colonists"]) {
		row, _ := na.AsMap(raw)
		if drafted, _ := na.AsBool(row["drafted"]); drafted {
			return fmt.Errorf("colonist %s still drafted after the raid resolved", na.AsString(row["id"]))
		}
	}
	neutralised := 0
	for _, raw := range na.AsSlice(final["hostiles"]) {
		row, _ := na.AsMap(raw)
		if dead, _ := na.AsBool(row["dead"]); dead {
			neutralised++
		} else if downed, _ := na.AsBool(row["downed"]); downed {
			neutralised++
		}
	}
	raidersLeft := len(na.AsSlice(final["hostiles"]))
	report["raid_outcome"] = map[string]any{"hostiles_on_map": raidersLeft, "dead_or_downed": neutralised}
	if neutralised == 0 && raidersLeft == len(na.AsSlice(raid["added"])) {
		return fmt.Errorf("raid did not resolve: no raider dead, downed or gone: %#v", final)
	}
	if v.herd != nil {
		return v.herd.survived(ctx, h, identity, report)
	}
	return nil
}

// prepareSite stages the colony's one guarded construction site, whose
// building plan holds the routine arbitration slot, and stocks the
// perimeter's stone blocks beside it so the wall's sections are admitted
// as fast as they are built. It returns the site's cell.
func prepareSite(ctx context.Context, h *na.Harness, identity map[string]any, report na.Report) (int, int, error) {
	construction, err := h.Call(ctx, "prepare-construction", "test/guarded_construction_prepare", map[string]any{"siteCount": 1})
	if err != nil {
		return 0, 0, err
	}
	if success, _ := na.AsBool(construction["success"]); !success || !na.MatchesIdentity(construction, identity) {
		return 0, 0, fmt.Errorf("guarded_construction_prepare refused or identity mismatch: %#v", construction)
	}
	sites := na.AsSlice(construction["sites"])
	if len(sites) != 1 {
		return 0, 0, fmt.Errorf("guarded_construction_prepare: expected exactly one site, got %#v", construction)
	}
	site0, _ := na.AsMap(sites[0])
	siteX, siteZ := int(na.AsNumber(site0["x"])), int(na.AsNumber(site0["z"]))
	for _, stock := range siteStock(siteX, siteZ) {
		if _, err := na.LabStock(ctx, h, stock); err != nil {
			return 0, 0, err
		}
	}
	report["stone_blocks"] = perimeterStone
	report["wood_logs"] = woodLogs
	return siteX, siteZ, nil
}

// siteStock is the resources prepareSite lays beside the site: the perimeter's
// granite blocks and the layout's wood.
func siteStock(x, z int) []na.Stock {
	return []na.Stock{
		{Def: "BlocksGranite", Total: perimeterStone, X: x, Z: z},
		{Def: "WoodLog", Total: woodLogs, X: x, Z: z},
	}
}

// edgeSide names the map side a corridor facing toward (the direction from
// the edge toward Home) opens onto, as the fixture's raid side (#714).
func edgeSide(toward domain.Rotation) string {
	switch toward {
	case domain.North:
		return "south"
	case domain.South:
		return "north"
	case domain.East:
		return "west"
	case domain.West:
		return "east"
	}
	return ""
}

// launchService starts rimgovernor serve against the shared game (the
// harness session must be closed), waits for it to attach to the fixture's
// identity, acquires authority and keeps it alive, and opens the journal.
func launchService(ctx context.Context, s cases.Session, name string, identity map[string]any, report na.Report) (*service, error) {
	// Every launch shares one profile and state journal (na.Serve): the
	// journal binds its clock inbox to the first profile path and refuses
	// any other. The declared spec carries the families (tend and rescue
	// ride along for the aftermath: raid injuries hold CriticalMedical in
	// deficit, which suspends every priority>=2 goal including the layout
	// repair until the wounded are treated, #72); each phase gets its own
	// requestId prefix.
	spec := s.Spec()
	spec.Prefix = "defense-" + name
	proc, err := s.Serve(ctx, spec)
	if err != nil {
		return nil, err
	}
	report["service_"+name] = proc.Entry()
	svc := &service{ServiceProcess: proc}
	fail := func(err error) (*service, error) { svc.stop(); return nil, err }
	keep := &authorityKeepAlive{apiCall: svc.API, identity: identity, token: svc.Token, name: name}
	if err := keep.start(); err != nil {
		return fail(err)
	}
	keepCtx, stopKeep := context.WithCancel(ctx)
	svc.keepAlive, svc.stopKeep = keep, stopKeep
	svc.keepWG.Add(1)
	go func() { defer svc.keepWG.Done(); keep.run(keepCtx) }()
	svc.store, err = na.OpenStoreWithRetry(ctx, proc.StatePath)
	if err != nil {
		return fail(fmt.Errorf("open verification store: %w", err))
	}
	return svc, nil
}

// layoutBuildTicks bounds how long one set of placed tier blueprints counts
// as progress while the clock advances: three game days.
const layoutBuildTicks = 3 * 60000

// buildProgress is the layout's construction as wait progress. A plan's
// stages all read completed once its blueprints are placed, and the
// colonists then build them over game hours with nothing in the journal
// changing; a raid mid-build parks the layout planner (an emergency vetoes
// it) while the fight itself takes minutes of wall time, and a built tier's
// plan is retired before the next tier is admitted (#2134). The advancing
// tick is that progress for layoutBuildTicks after the open plans or the
// record's built and attempted tiers last changed; a stopped clock or a
// layout that never moves again still stalls.
type buildProgress struct {
	key   string
	since domain.Tick
}

func (b *buildProgress) signature(open []string, record store.DefenseLayoutRecord, tick domain.Tick) string {
	built, attempts := 0, 0
	for _, t := range record.Tiers {
		if t.Built {
			built++
		}
		attempts += t.Attempts
	}
	key := fmt.Sprintf("%s|built=%d|attempts=%d", strings.Join(open, ","), built, attempts)
	if key != b.key {
		b.key, b.since = key, tick
	}
	if tick-b.since > layoutBuildTicks {
		return ""
	}
	return fmt.Sprint(tick)
}

// waitLayoutComplete polls the journal until the stored layout for world is
// Complete, recording each tier method's plan and its final stage. The
// progress signature is the tier methods' plan stages, the stored record
// and an open tier's construction (buildProgress).
func waitLayoutComplete(ctx context.Context, s *store.Store, world store.World, w na.Wait, report na.Report) (store.DefenseLayoutRecord, error) {
	var projectID domain.ProjectID
	var record store.DefenseLayoutRecord
	var stored bool
	var building buildProgress
	tiers := map[string]any{}
	err := na.WaitProgress(ctx, w, func(ctx context.Context) (string, bool, error) {
		review, err := s.LoadRounds(ctx)
		if err == nil && projectID == "" {
			if id, ok := review.ProjectFor(policy.EnsureDefensiveLayout); ok {
				projectID = id
				report["layout_project"] = string(projectID)
			}
		}
		var open []string
		if projectID != "" {
			if project, err := s.LoadProject(ctx, projectID); err == nil {
				for _, m := range project.Methods {
					entry := map[string]any{"plan": string(m.Plan)}
					if plan, err := s.LoadPlan(ctx, m.Plan); err == nil {
						entry["stages"] = stages(plan.Progress)
						if store.PlanOpen(plan) {
							open = append(open, string(m.Plan))
						}
					}
					tiers[string(m.Method)] = entry
				}
				report["layout_tier_methods"] = tiers
			}
		}
		r, ok, err := s.LoadDefenseLayout(ctx, world)
		if err == nil && ok {
			record, stored = r, true
			data, _ := json.Marshal(record)
			report["layout_record"] = json.RawMessage(data)
			if record.Complete {
				if len(record.TrapLane) == 0 || len(record.Firing) == 0 {
					return "", false, fmt.Errorf("complete layout without trap lane or firing cells: %+v", record)
				}
				return "", true, nil
			}
		}
		sort.Strings(open)
		return na.Signature(projectID, tiers, stored, record.Complete, building.signature(open, record, review.Tick)), false, nil
	})
	if err != nil {
		return record, fmt.Errorf("layout not complete (project=%q stored=%v): %w", projectID, stored, err)
	}
	return record, nil
}

// combatIncident is the review's ActiveCombat incident (#1020), if bound.
func combatIncident(ctx context.Context, s *store.Store, review store.Rounds) (store.IncidentState, store.RoundsIncident, bool, error) {
	binding, ok := review.Incident(policy.ActiveCombat)
	if !ok {
		return store.IncidentState{}, binding, false, nil
	}
	state, err := s.LoadIncident(ctx, binding.Incident)
	return state, binding, err == nil, err
}

// waitCombatMethod polls the journal for the ActiveCombat incident's first
// committed method after the raid.
func waitCombatMethod(ctx context.Context, s *store.Store, w na.Wait, report na.Report) (store.IncidentMethod, error) {
	var found store.IncidentMethod
	err := na.WaitProgress(ctx, w, func(ctx context.Context) (string, bool, error) {
		review, err := s.LoadRounds(ctx)
		if err != nil {
			return na.Signature("no-review"), false, nil
		}
		incident, binding, ok, err := combatIncident(ctx, s, review)
		if err != nil || !ok {
			return na.Signature("unbound", len(review.Incidents)), false, err
		}
		if len(incident.Methods) > 0 {
			report["combat_incident"] = string(binding.Incident)
			report["combat_method"] = string(incident.Methods[0].Method)
			found = incident.Methods[0]
			return "", true, nil
		}
		return na.Signature("bound", binding.Incident), false, nil
	})
	if err != nil {
		return store.IncidentMethod{}, fmt.Errorf("no ActiveCombat method: %w", err)
	}
	return found, nil
}

// waitHoldDispatched waits until every draft in the hold plan is completed
// and every move to a firing cell has been attempted natively: the furthest
// the plan can get before the first combat window lets ticks pass. A raid
// the first defender (or the turret tier) ends before the others reach
// their cells retires the plan with the rest cancelled; that settlement
// counts as dispatched when a defender reached its cell or fired and no
// stage failed: the third defender never drafted is the turrets' doing.
func waitHoldDispatched(ctx context.Context, s *store.Store, id domain.PlanID, w na.Wait) (map[string]any, error) {
	out := map[string]any{}
	err := na.WaitProgress(ctx, w, func(ctx context.Context) (string, bool, error) {
		evidence, err := s.CombatEvidence(ctx, id)
		if err != nil {
			return "", false, err
		}
		moves, attacks := 0, 0
		for _, stop := range evidence {
			for _, o := range stop.Orders {
				if !o.Applied {
					continue
				}
				switch o.Kind {
				case policy.OrderMove:
					moves++
				case policy.OrderAttack:
					attacks++
				}
			}
		}
		out["stops"], out["moves"], out["attacks"] = len(evidence), moves, attacks
		if moves > 0 {
			return "", true, nil
		}
		return fmt.Sprint(len(evidence)), false, nil
	})
	if err != nil {
		return out, fmt.Errorf("fight issued no applied move: %w", err)
	}
	return out, nil
}

// waitRaidResolved polls the journal while the service fights the raid: it
// counts the combat watch windows the scheduler applied and returns once the
// ActiveCombat goal has recovered (no live hostile) or is no longer bound.
// At least one combat window must have run, and at least one defender must
// have been observed arriving on its firing cell (a completed movement in
// the first hold plan or any hold plan the goal re-planned after it); the
// harness never steps the clock natively. The progress signature is the
// window counts, the goal's binding and the defenders on the line.
func waitRaidResolved(ctx context.Context, s *store.Store, first domain.PlanID, w na.Wait) (map[string]any, error) {
	out := map[string]any{}
	holdPlans := map[domain.PlanID]bool{first: true}
	onLine := map[domain.PawnID]bool{}
	err := na.WaitProgress(ctx, w, func(ctx context.Context) (string, bool, error) {
		attempts, err := s.LoadClockAttempts(ctx, 4096)
		if err != nil {
			return "", false, err
		}
		combatWindows, colonyWindows := 0, 0
		var lastPhase string
		var acknowledged []string
		for _, a := range attempts {
			start := a.Intent.Command.Start
			if start == nil || a.Phase != store.ClockApplied {
				continue
			}
			lastPhase = string(a.Phase)
			if start.Policy.GetMode() == k.WatchMode_WATCH_MODE_COMBAT {
				combatWindows++
				acknowledged = start.Policy.AcknowledgedHostileIds
			} else {
				colonyWindows++
			}
		}
		out["combat_windows"] = combatWindows
		out["colony_windows"] = colonyWindows
		out["last_acknowledged_hostiles"] = acknowledged
		out["last_applied_phase"] = lastPhase
		review, err := s.LoadRounds(ctx)
		if err != nil {
			return "", false, err
		}
		incident, binding, bound, err := combatIncident(ctx, s, review)
		if err != nil {
			return "", false, err
		}
		need := string(binding.Situation)
		for _, m := range incident.Methods {
			if strings.HasPrefix(string(m.Method), "combat-") {
				holdPlans[m.Plan] = true
			}
		}
		for id := range holdPlans {
			evidence, err := s.CombatEvidence(ctx, id)
			if err != nil {
				return "", false, err
			}
			for _, stop := range evidence {
				for _, o := range stop.Orders {
					if o.Kind == policy.OrderMove && o.Applied {
						onLine[o.Pawn] = true
					}
				}
			}
		}
		line := make([]string, 0, len(onLine))
		for id := range onLine {
			line = append(line, string(id))
		}
		sort.Strings(line)
		out["defenders_on_firing_cells"] = line
		out["combat_standard_bound"] = bound
		out["combat_standard_need"] = need
		out["resolved_tick"] = int64(review.Tick)
		if combatWindows > 0 && (!bound || need == string(domain.FindingMet)) {
			if len(line) == 0 {
				return "", false, fmt.Errorf("no defender completed its move to a firing cell during the raid: %#v", out)
			}
			return "", true, nil
		}
		return na.Signature(combatWindows, colonyWindows, bound, need, len(holdPlans), line), false, nil
	})
	if err != nil {
		return out, fmt.Errorf("raid not resolved under the service (%#v): %w", out, err)
	}
	return out, nil
}

// trapIDDelta compares the trap ids of two fixture inspects: ids only in
// before were destroyed (sprung), ids only in after are replacements.
func trapIDDelta(before, after map[string]any) (sprung, rebuilt []string) {
	ids := func(m map[string]any) map[string]bool {
		out := map[string]bool{}
		for _, raw := range na.AsSlice(m["trapIds"]) {
			out[na.AsString(raw)] = true
		}
		return out
	}
	was, is := ids(before), ids(after)
	for id := range was {
		if !is[id] {
			sprung = append(sprung, id)
		}
	}
	for id := range is {
		if !was[id] {
			rebuilt = append(rebuilt, id)
		}
	}
	sort.Strings(sprung)
	sort.Strings(rebuilt)
	return sprung, rebuilt
}

// waitDefendersReleased waits until every fight rostering a defender is
// closed and its defenders undrafted (#939): the recovered ActiveCombat goal closes the fight, so
// the undraft sweep returns each defender to colony work.
func waitDefendersReleased(ctx context.Context, s *store.Store, first domain.PlanID, prefix string, w na.Wait) (map[string]any, error) {
	out := map[string]any{}
	err := na.WaitProgress(ctx, w, func(ctx context.Context) (string, bool, error) {
		plans := map[domain.PlanID]bool{first: true}
		review, err := s.LoadRounds(ctx)
		if err != nil {
			return "", false, err
		}
		// The recovered incident closes once its fights settle, so read the
		// latest one, closed or not.
		world := store.World{Colony: review.Snapshot.Colony, Load: review.Snapshot.Load, Map: review.Snapshot.Map}
		incident, _, err := s.LatestIncident(ctx, world, policy.ActiveCombat)
		if err != nil {
			return "", false, err
		}
		for _, m := range incident.Methods {
			if strings.HasPrefix(string(m.Method), prefix) {
				plans[m.Plan] = true
			}
		}
		drafts := map[string]string{}
		pending := 0
		for id := range plans {
			fight, _, err := s.LoadCombatFight(ctx, id)
			if err != nil {
				return "", false, err
			}
			for pawn := range fight.Roster {
				drafts[string(id)+"/"+string(pawn)] = "roster"
				pending++
			}
		}
		out["drafts"] = drafts
		out["hold_plans"] = len(plans)
		if pending == 0 {
			return "", true, nil
		}
		return na.Signature(drafts), false, nil
	})
	if err != nil {
		return out, fmt.Errorf("defenders not released (%#v): %w", out, err)
	}
	return out, nil
}

// waitLayoutRepaired waits until the stored layout has been re-verified
// after the raid's combat epoch with every tier standing: the planner
// re-opens each tier that lost a building and admits a fresh tier method
// ("defense-<tier>-<attempt>") to rebuild it; traps_rebuilt counts the
// spike traps those methods placed. At least one tier must have been observed
// degraded (the edge raid springs at least one trap), and the verification
// must land within repairTicks of the tick the goal recovered.
func waitLayoutRepaired(ctx context.Context, s *store.Store, world store.World, resolvedTick, repairTicks int64, w na.Wait) (map[string]any, error) {
	out := map[string]any{"resolved_tick": resolvedTick}
	methods := map[string]any{}
	reopened := map[string]bool{}
	plans := map[string]domain.PlanID{}
	trapsRebuilt := 0
	err := na.WaitProgress(ctx, w, func(ctx context.Context) (string, bool, error) {
		review, err := s.LoadRounds(ctx)
		if err != nil {
			return "", false, err
		}
		combat := ""
		if latest, ok, err := s.LatestIncident(ctx, world, policy.ActiveCombat); err != nil {
			return "", false, err
		} else if ok {
			combat = string(latest.Incident.ID)
		}
		if id, ok := review.ProjectFor(policy.EnsureDefensiveLayout); ok {
			project, err := s.LoadProject(ctx, id)
			if err != nil && !errors.Is(err, store.ErrNotFound) {
				return "", false, err
			}
			for _, m := range project.Methods {
				if _, seen := methods[string(m.Method)]; seen {
					continue
				}
				plan, err := s.LoadPlan(ctx, m.Plan)
				if err != nil {
					return "", false, err
				}
				traps := 0
				for _, a := range plan.Spec.Actions() {
					if b, ok := a.Building(); ok && b.Definition() == "TrapSpike" {
						traps++
					}
				}
				trapsRebuilt += traps
				plans[string(m.Method)] = m.Plan
				methods[string(m.Method)] = map[string]any{"plan": string(m.Plan), "actions": len(plan.Spec.Actions()), "traps": traps}
			}
		}
		record, ok, err := s.LoadDefenseLayout(ctx, world)
		if err != nil {
			return "", false, err
		}
		if !ok {
			return "", false, fmt.Errorf("stored layout gone after the raid")
		}
		standing := true
		for _, tier := range record.Tiers {
			if len(tier.Buildings) > 0 && !tier.Built {
				standing = false
				reopened[string(tier.Name)] = true
			}
		}
		names := make([]string, 0, len(reopened))
		for name := range reopened {
			names = append(names, name)
		}
		sort.Strings(names)
		out["tiers_reopened"] = names
		out["repair_methods"] = methods
		out["traps_rebuilt"] = trapsRebuilt
		out["verified_tick"] = int64(record.VerifiedTick)
		out["verified_combat"] = record.VerifiedCombat
		out["complete"] = record.Complete
		// A checkpoint run re-observes the record under its new load
		// (Complete is cleared on reload), so Complete is required only
		// once the post-raid census has found every tier standing.
		verified := standing && record.Complete && record.VerifiedCombat == combat && int64(record.VerifiedTick) >= resolvedTick
		if verified && len(reopened) == 0 {
			return "", false, fmt.Errorf("layout verified standing after the raid without any tier observed degraded: %#v", out)
		}
		if verified {
			if elapsed := int64(record.VerifiedTick) - resolvedTick; elapsed > repairTicks {
				return "", false, fmt.Errorf("layout repaired %d ticks after the raid resolved, budget %d", elapsed, repairTicks)
			}
			return "", true, nil
		}
		// The rebuild is native work: while a repair plan's frames are
		// under construction nothing in the record moves, so the review
		// tick and each plan's stages carry the progress; repairTicks
		// bounds the wait in game time.
		var progress []string
		for _, name := range sortedKeys(plans) {
			state, err := s.LoadPlan(ctx, plans[name])
			if err != nil {
				return "", false, err
			}
			progress = append(progress, name+":"+strings.Join(stages(state.Progress), ","))
		}
		return na.Signature(standing, record.VerifiedCombat, record.VerifiedTick, len(methods), names, review.Tick, progress), false, nil
	})
	if err != nil {
		return out, fmt.Errorf("layout not repaired (%#v): %w", out, err)
	}
	return out, nil
}

func sortedKeys[V any](m map[string]V) []string {
	keys := make([]string, 0, len(m))
	for k := range m {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	return keys
}

// hasCell reports whether the fixture's {x, z} list contains the cell.
func hasCell(cells []any, x, z int) bool {
	for _, raw := range cells {
		row, _ := na.AsMap(raw)
		if int(na.AsNumber(row["x"])) == x && int(na.AsNumber(row["z"])) == z {
			return true
		}
	}
	return false
}

func stages(progress []domain.Progress) []string {
	out := make([]string, 0, len(progress))
	for _, p := range progress {
		out = append(out, string(p.View().Stage))
	}
	return out
}

// assertHoldPlan checks the fight formed hold-the-line: every positioned
// role stands on one of the layout's firing cells and never a trap cell.
func assertHoldPlan(memory policy.CombatMemory, layout store.DefenseLayoutRecord) error {
	if len(layout.Firing) == 0 {
		return errors.New("layout has no firing cells")
	}
	if memory.Tactic != policy.TacticHold {
		return fmt.Errorf("fight formed %q, want %q (hold refused: %q)", memory.Tactic, policy.TacticHold, memory.Refusal)
	}
	firing := map[domain.Cell]bool{}
	for _, c := range layout.Firing {
		firing[c] = true
	}
	traps := map[domain.Cell]bool{}
	for _, tier := range layout.Tiers {
		for _, b := range tier.Buildings {
			if b.Definition == "TrapSpike" {
				traps[b.Cell] = true
			}
		}
	}
	positioned := 0
	for _, role := range memory.Roles {
		if role.Cell == nil {
			continue
		}
		positioned++
		if traps[*role.Cell] {
			return fmt.Errorf("hold places %s on trap cell %+v", role.Pawn, *role.Cell)
		}
		if !firing[*role.Cell] {
			return fmt.Errorf("hold places %s on %+v, not a firing cell %v", role.Pawn, *role.Cell, layout.Firing)
		}
	}
	if positioned == 0 {
		return errors.New("hold places no defender")
	}
	return nil
}

func cellsJSON(cells []domain.Cell) []map[string]any {
	out := make([]map[string]any, 0, len(cells))
	for _, c := range cells {
		out = append(out, map[string]any{"x": c.X, "z": c.Z})
	}
	return out
}

// assertAccess mirrors bridge.SpatialAccess.Accepted on the ProtoJSON reply:
// no pawn lost a cell and every target is reachable natively and projected.
func assertAccess(reply map[string]any, fenced map[domain.Cell]bool) error {
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return err
	}
	pawns := na.AsSlice(observed["pawns"])
	if len(pawns) == 0 {
		return errors.New("no colonist audited")
	}
	for _, raw := range pawns {
		p, _ := na.AsMap(raw)
		if loses, _ := na.AsBool(p["losesAccess"]); loses {
			return fmt.Errorf("colonist %v loses cells with the layout blocked: %#v", p["pawn"], p)
		}
		for _, t := range na.AsSlice(p["targets"]) {
			target, _ := na.AsMap(t)
			native, _ := na.AsBool(target["nativeReachable"])
			projected, _ := na.AsBool(target["projectedReachable"])
			if cell, ok := na.AsMap(target["cell"]); ok && fenced[domain.Cell{X: int32(na.AsNumber(cell["x"])), Z: int32(na.AsNumber(cell["z"]))}] {
				projected = true
			}
			if !native || !projected {
				return fmt.Errorf("colonist %v cannot reach %v with the layout blocked", p["pawn"], target["cell"])
			}
		}
	}
	return nil
}

// assertCover requires every firing cell to see at least one trap-lane cell and
// a positive cover block chance on at least one line. The lane is a snake
// (#1544) whose walls hide the mouth from the firing line, so seeing the
// chokepoint is not asked.
func assertCover(lines []any, layout store.DefenseLayoutRecord) error {
	if len(lines) == 0 {
		return errors.New("no lines of fire observed")
	}
	sees := map[domain.Cell]bool{}
	covered := 0
	for _, raw := range lines {
		line, _ := na.AsMap(raw)
		seen, _ := na.AsBool(line["lineOfSight"])
		if !seen {
			continue
		}
		from, _ := na.AsMap(line["from"])
		sees[domain.Cell{X: int32(na.AsNumber(from["x"])), Z: int32(na.AsNumber(from["z"]))}] = true
		if na.AsNumber(line["shooterCover"]) > 0 {
			covered++
		}
	}
	for _, c := range layout.Firing {
		if !sees[c] {
			return fmt.Errorf("firing cell %+v has no line of sight to any trap-lane cell: %#v", c, lines)
		}
	}
	if covered == 0 {
		return fmt.Errorf("no firing cell has shooter cover toward the trap lane: %#v", lines)
	}
	return nil
}

// authorityKeepAlive resumes player control whenever the service has actually
// dropped it (a letter hold, generation exhaustion), acknowledging clock holds
// first. A stale observation alone leaves automate mode without disabling
// control; resuming then would bump the native generation and invalidate the
// in-flight routine goals (#65), so it is not a trigger.
type authorityKeepAlive struct {
	apiCall  apiFunc
	identity map[string]any
	token    string
	name     string

	mu                sync.Mutex
	attempts          int
	reacquired        int
	acknowledged      int
	acknowledgeFailed int
	lastError         string
}

// resume enters automate mode under the world's own root plan (#55); the
// state journal is shared by every service the harness launches, so a fresh
// requestId is used each time rather than replaying an earlier record.
func (k *authorityKeepAlive) resume(label string) (map[string]any, int, error) {
	return k.apiCall("POST", "/api/player/control/resume", map[string]any{
		"requestId": fmt.Sprintf("defense-%s-%s-%d", k.name, label, time.Now().UnixNano()), "expected": k.identity,
	}, k.token)
}

// start makes the first resume. A service launched into a world whose
// journal still carries the previous phase's clock events (the fixture's
// own pause between phases is an external stop) may review those events
// while the resume is in flight, disable authority and leave the record
// uncertain; the holds are acknowledged and the resume repeated, bounded,
// exactly as the keep-alive loop would after start.
func (k *authorityKeepAlive) start() error {
	var last error
	for attempt := 1; attempt <= 5; attempt++ {
		if attempt > 1 {
			k.acknowledgeHolds()
			time.Sleep(na.KeepAliveInterval)
		}
		resumed, status, err := k.resume(fmt.Sprintf("resume-%d", attempt))
		if err != nil {
			last = err
			continue
		}
		record, _ := na.AsMap(resumed["record"])
		if status == 200 && na.AsString(record["phase"]) == "running" {
			return nil
		}
		last = fmt.Errorf("resume was not running: status=%d body=%#v", status, resumed)
	}
	return last
}

// acknowledgeHolds acknowledges every outstanding clock hold; the count of
// successes and failures lands on the report through snapshot.
func (k *authorityKeepAlive) acknowledgeHolds() {
	clk, clkStatus, clkErr := k.apiCall("GET", "/api/player/clock", nil, "")
	if clkErr != nil || clkStatus != 200 {
		return
	}
	holds := na.AsSlice(clk["holds"])
	if len(holds) == 0 {
		return
	}
	ackBody := map[string]any{
		"requestId":        fmt.Sprintf("defense-%s-ack-%d", k.name, time.Now().UnixNano()),
		"expectedRevision": na.AsString(clk["revision"]), "throughCursor": na.AsString(clk["inboxCursor"]),
	}
	_, ackStatus, ackErr := k.apiCall("POST", "/api/player/clock/acknowledge", ackBody, k.token)
	k.mu.Lock()
	defer k.mu.Unlock()
	if ackErr != nil || ackStatus != 200 {
		k.acknowledgeFailed++
		k.lastError = fmt.Sprintf("acknowledge status=%d err=%v", ackStatus, ackErr)
	} else {
		k.acknowledged++
	}
}

func (k *authorityKeepAlive) snapshot() map[string]any {
	k.mu.Lock()
	defer k.mu.Unlock()
	out := map[string]any{"attempts": k.attempts, "reacquired": k.reacquired, "acknowledged": k.acknowledged, "acknowledge_failed": k.acknowledgeFailed}
	if k.lastError != "" {
		out["last_error"] = k.lastError
	}
	return out
}

func (k *authorityKeepAlive) run(ctx context.Context) {
	for {
		select {
		case <-ctx.Done():
			return
		case <-time.After(na.KeepAliveInterval):
		}
		state, status, err := k.apiCall("GET", "/api/state", nil, "")
		if err != nil || status != 200 || na.AsString(state["mode"]) == "automate" {
			continue
		}
		k.acknowledgeHolds()
		control, controlStatus, controlErr := k.apiCall("GET", "/api/player/control", nil, "")
		if controlErr == nil && controlStatus == 200 {
			live, _ := na.AsMap(control["state"])
			if enabled, _ := na.AsBool(live["enabled"]); enabled {
				continue
			}
		}
		k.mu.Lock()
		k.attempts++
		k.mu.Unlock()
		acquired, status, err := k.resume("reresume")
		if err != nil {
			k.mu.Lock()
			k.lastError = err.Error()
			k.mu.Unlock()
			continue
		}
		record, _ := na.AsMap(acquired["record"])
		k.mu.Lock()
		if status != 200 || na.AsString(record["phase"]) != "running" {
			k.lastError = fmt.Sprintf("reresume status=%d body=%#v", status, acquired)
		} else {
			k.reacquired++
			k.lastError = ""
		}
		k.mu.Unlock()
	}
}
