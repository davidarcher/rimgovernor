// Package defense holds the defensive-layout vertical (issue #5, B06c) end to end against a live game and a live rimgovernor "serve"
// service: on a constrained site (a granite band with one corridor gap, laid
// by the private test/defense_setup fixture) the RoutineDefenseLayoutPlanner
// chooses the chokepoint, builds every tier natively (firing line, funnel,
// trap corridor), and the stored layout is re-verified by an independent
// native spatial-access read with its walls and barricades blocked and by a native
// inspection that no colonist stands on a trap. A real RaidEnemy incident
// (chosen strategy and arrival) is then raised and the RoutineDefensePlanner
// must respond with hold-the-line (method "hold-…") for an ordinary edge
// assault, or with squad defense ("squad-…") when the raid bypasses the line
// (defense/raid-bypass: ImmediateAttackSappers). In
// defense/predator the incident is instead a wild predator the fixture
// spawns inside the band already hunting a colonist (#157): the clock stops
// with predator_hunt, the emergency holds the hunt as an unsafe threat, and
// the RoutineDefensePlanner must answer it with squad defense; the run then
// waits for the hunt to resolve under the service (the predator dead,
// downed or gone, the ActiveCombat goal recovered, the drafts released) and
// for the clock to resume colony windows afterwards. For the edge
// raid the service then holds the raid itself -- the scheduler admits
// bounded combat watch windows acknowledging the live hostiles while the
// ActiveCombat goal has an admitted plan (#69) -- and the run waits for the
// goal to recover, then for the aftermath (#72): the hold plan's drafts are
// released and the layout planner re-admits every tier the raid degraded
// (a sprung spike trap is destroyed; a breached wall is gone) until the
// stored record is verified standing again within a bounded number of
// ticks. The repair is the planner's own (#117): before the repair
// service starts, the fixture switches the game's auto-rebuild off,
// removes its pending trap blueprints and vanishes a surviving trap,
// so every missing trap can only be replaced by a re-admitted tier
// method. Natively the run then asserts that the raiders are dead or
// downed, at least one trap sprung, the trap and wall counts match the
// audited layout, the breached cell holds a trap again and no colonist is
// left drafted.
//
// Only one GABP client may hold the game at a time: the case's fixture
// session and the service's session are used strictly in turn.
//
// defense/layout builds the layout on the tribal8 baseline and plays the
// edge raid through to the repair; tools/defense-checkpoint builds and
// audits it, then saves the game as the committed checkpoint
// (scripts/fixtures/saves/RimGovernor-defense-layout.rws plus its
// .checkpoint.json: the layout record and the guarded-construction site).
// defense/raid, defense/raid-bypass and defense/predator resume from that
// checkpoint: they re-run the cheap layout audits and go straight to the
// incident. The checkpoint is fixture-mod state, so regenerate it after
// fixture or save-format changes.
package defense

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"os"
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
	// checkpointName is the committed layout checkpoint the raid cases
	// resume from; checkpointDir is where it lives, relative to the repo.
	checkpointName = "RimGovernor-defense-layout"
	checkpointDir  = "scripts/fixtures/saves"
	// layoutTimeout bounds the native layout build, raidTimeout the raid
	// response and resolution, repairTimeout the post-raid draft release and
	// layout repair in wall time (raid injuries are tended first:
	// CriticalMedical suspends the layout goal) and repairTicks the same in
	// game ticks from the tick the raid resolved (one day).
	layoutTimeout = 20 * time.Minute
	raidTimeout   = 8 * time.Minute
	repairTimeout = 15 * time.Minute
	repairTicks   = int64(60000)
)

// variant is one case's scenario: the incident staged after the layout
// (threat "raid" with its RaidStrategyDef/PawnsArrivalModeDef, bypass when
// the raid bypasses the line; or "predator" with its PawnKindDef), whether
// the layout comes from the committed checkpoint, and whether the run ends
// by writing that checkpoint instead of staging an incident.
type variant struct {
	strategy, arrival    string
	bypass               bool
	threat, predatorKind string
	fromCheckpoint       bool
	writeCheckpoint      bool
	// turrets adds the powered turret tier (#61) before the raid and
	// replaces the trap breach with a conduit loss and turret damage.
	turrets bool
}

func init() {
	spec := &cases.ServeSpec{Families: []string{"defensive-layout", "defense", "tend", "rescue"}, Env: []string{"RIMGOVERNOR_CLOCK_DEBUG=1"}, Prefix: "defense"}
	// The baseline save keeps the site deterministic; a random debug colony
	// can spawn beside ruins the rock band cannot close.
	baseline := cases.Save{Name: sustained.BaselineSave}
	checkpoint := cases.Save{Name: checkpointName, From: committedCheckpoints()}
	edge := variant{strategy: "ImmediateAttack", arrival: "EdgeWalkIn", threat: "raid", predatorKind: "Cougar"}
	register := func(name, scope string, start cases.Start, budget time.Duration, v variant) {
		var reason string
		if budget > cases.MaxBudget {
			reason = "layout audit, raid answer and repair to the audited state are one native campaign; the bypass and predator variants open on the committed layout within MaxBudget"
		}
		serve := spec
		if v.turrets {
			// The turret aftermath is routine upkeep: the repair family
			// mends the damaged turret and the power family may route the
			// lost connection before the layout re-places its conduit.
			serve = &cases.ServeSpec{Families: append(append([]string{}, spec.Families...), "repair", "power"), Env: spec.Env, Prefix: spec.Prefix}
			reason = "turret build, raid answer and routine restoration of a depowered, damaged turret are one native campaign on the committed layout"
		}
		cases.Register(cases.Case{
			Name: name, Scope: scope, Start: start, Serve: serve, Budget: budget, Reason: reason,
			Run: func(ctx context.Context, s cases.Session) error { return run(ctx, s, v) },
		})
	}
	const audit = "the routine planner chooses the corridor chokepoint on a fixture-constrained site, builds every tier natively, " +
		"the layout is re-verified by independent spatial-access and trap-cell reads"
	register("defense/layout", "Native defensive layout vertical (#5 M4): "+audit+", a real RaidEnemy edge assault is answered with hold-the-line, "+
		"and afterwards the defenders are undrafted and the layout is repaired to its audited state (#72).", baseline, 45*time.Minute, edge)
	register("tools/defense-checkpoint", "Checkpoint generation: "+audit+", then the game is saved as the committed "+checkpointName+
		" checkpoint the defense/raid* and defense/predator cases resume from.", baseline, 30*time.Minute,
		variant{threat: "raid", writeCheckpoint: true})
	fromCheckpoint := edge
	fromCheckpoint.fromCheckpoint = true
	register("defense/raid", "Native defensive layout vertical (#5 M4) from the committed layout checkpoint: the saved layout is re-audited, "+
		"a real RaidEnemy edge assault is answered with hold-the-line, and afterwards the defenders are undrafted and the layout is repaired to its audited state (#72).",
		checkpoint, 30*time.Minute, fromCheckpoint)
	bypass := fromCheckpoint
	bypass.strategy, bypass.bypass = "ImmediateAttackSappers", true
	register("defense/raid-bypass", "Native defensive layout vertical (#5 M4) from the committed layout checkpoint: a sapper raid that bypasses the line "+
		"is answered with squad defense, never a line position.", checkpoint, 15*time.Minute, bypass)
	turrets := fromCheckpoint
	turrets.turrets = true
	register("defense/turrets", "Powered turret tier (#61) on the committed layout checkpoint: with turret research, a fuelled network and steel observed, "+
		"the planner adds turrets behind the firing line with their own conduit chain and builds them natively; a powered turret is observed firing on an edge raid "+
		"entering the kill zone, and afterwards a depowered, damaged turret is restored through routine upkeep.",
		checkpoint, 45*time.Minute, turrets)
	predator := fromCheckpoint
	predator.threat = "predator"
	register("defense/predator", "A wild predator hunting a colonist during supervised play (#157) is answered with squad defense from the committed layout checkpoint: "+
		"the hunt resolves under the service through combat windows, the drafts are released and colony windows resume.",
		checkpoint, 15*time.Minute, predator)
}

// layoutCheckpoint is what a raid-only run needs besides the save itself:
// the layout the service built (the raid assertions compare the hold plan
// against it) and the guarded-construction site the service is pointed at.
type layoutCheckpoint struct {
	Layout      store.DefenseLayoutRecord `json:"layout"`
	SiteX       int                       `json:"siteX"`
	SiteZ       int                       `json:"siteZ"`
	SavedAtTick int64                     `json:"savedAtTick"`
}

func checkpointPath(root, name string) string {
	return filepath.Join(root, "profile", "Saves", name+".checkpoint.json")
}

// committedCheckpoints is the committed checkpoint directory, found from
// the working directory upwards (the runner runs from go/ or the repo root).
func committedCheckpoints() string {
	dir, err := os.Getwd()
	if err != nil {
		return checkpointDir
	}
	for {
		candidate := filepath.Join(dir, checkpointDir)
		if _, err := os.Stat(candidate); err == nil {
			return candidate
		}
		parent := filepath.Dir(dir)
		if parent == dir {
			return checkpointDir
		}
		dir = parent
	}
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
	root, output := s.Config().Root, s.Config().Output
	report["raid"] = map[string]any{"strategy": v.strategy, "arrival": v.arrival, "bypass": v.bypass, "threat": v.threat}
	// Releasing the harness before a launch is the service's own business:
	// Session.Serve first answers any colony-naming dialog through the
	// harness (a released one reads "bridge closed"), then na.Serve frees
	// the sole GABP slot itself. closeClient marks the hand-over points.
	closeClient := func() error { return nil }
	// The service's GABS subprocess releases the game slot shortly after the
	// service is killed, not synchronously: Reattach retries. The service
	// leaves the game running, and every fixture op needs a paused map, so
	// the reattached session pauses first.
	reopenHarness := func() (*na.Harness, error) {
		h, err := s.Reattach(ctx)
		if err != nil {
			return nil, err
		}
		if _, err := h.Call(ctx, "pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
			return nil, err
		}
		return h, nil
	}
	world := store.World{Colony: domain.ColonyID(na.AsString(identity["colonyId"])), Load: domain.LoadID(na.AsString(identity["loadToken"])), Map: domain.MapID(na.AsNumber(identity["mapId"]))}
	for _, want := range []string{"test/defense_setup", "test/guarded_construction_prepare"} {
		if !na.Contains(s.Names(), want) {
			return fmt.Errorf("missing %s in discovery; rebuild the native mod with -Fixture DefenseFixture,GuardedConstructionFixture", want)
		}
	}

	fixture := func(label string, args map[string]any) (map[string]any, error) {
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
	var siteX, siteZ int
	// na.Serve keeps every service on this one state journal.
	statePath := filepath.Join(output, "service.sqlite")
	var svc *service
	launch := func(name string) (*service, error) {
		return launchService(ctx, s, name, identity, siteX, siteZ, report)
	}
	if v.fromCheckpoint {
		data, err := os.ReadFile(checkpointPath(root, checkpointName))
		if err != nil {
			return fmt.Errorf("read checkpoint: %w", err)
		}
		var cp layoutCheckpoint
		if err := json.Unmarshal(data, &cp); err != nil {
			return fmt.Errorf("decode checkpoint: %w", err)
		}
		layout, siteX, siteZ = cp.Layout, cp.SiteX, cp.SiteZ
		report["checkpoint"] = map[string]any{"name": checkpointName, "savedAtTick": cp.SavedAtTick}
		// The raid service starts on a fresh state file; seed it with the
		// record exactly as the layout session stored it, under the load it
		// was built in. The reload minted a new load token, so the layout
		// review must adopt the record and re-observe its tiers before combat
		// holds the line -- the same path a player's save/reload takes.
		seed, err := store.Open(ctx, statePath)
		if err != nil {
			return fmt.Errorf("seed layout store: %w", err)
		}
		err = seed.SaveDefenseLayout(ctx, layout)
		if closeErr := seed.Close(); err == nil {
			err = closeErr
		}
		if err != nil {
			return fmt.Errorf("seed layout: %w", err)
		}
		// Storyteller comps come back with the load; silence them again.
		if _, err := fixture("quiet", map[string]any{"op": "quiet"}); err != nil {
			return err
		}
	} else {
		terrain, err := fixture("terrain", map[string]any{"op": "terrain"})
		if err != nil {
			return err
		}
		report["terrain"] = terrain
		if _, err = fixture("stock", map[string]any{"op": "stock"}); err != nil {
			return err
		}
		ranged, err := fixture("ranged", map[string]any{"op": "ranged", "rifles": 3})
		if err != nil {
			return err
		}
		report["ranged"] = ranged
		construction, err := h.Call(ctx, "prepare-construction", "test/guarded_construction_prepare", map[string]any{"siteCount": 1})
		if err != nil {
			return err
		}
		if success, _ := na.AsBool(construction["success"]); !success || !na.MatchesIdentity(construction, identity) {
			return fmt.Errorf("guarded_construction_prepare refused or identity mismatch: %#v", construction)
		}
		sites := na.AsSlice(construction["sites"])
		if len(sites) != 1 {
			return fmt.Errorf("guarded_construction_prepare: expected exactly one site, got %#v", construction)
		}
		site0, _ := na.AsMap(sites[0])
		siteX, siteZ = int(na.AsNumber(site0["x"])), int(na.AsNumber(site0["z"]))
		before, err := fixture("inspect-before", map[string]any{"op": "inspect"})
		if err != nil {
			return err
		}
		report["inspect_before"] = before
		if int(na.AsNumber(before["traps"])) != 0 {
			return fmt.Errorf("fresh colony already has traps: %#v", before)
		}
		if err := closeClient(); err != nil {
			return fmt.Errorf("close fixture-prep bridge session: %w", err)
		}

		// Scenario 1: the layout is planned on the constrained site and every
		// tier is built natively under the live routine reviewer/planner.
		svc, err = launch("layout")
		if err != nil {
			return err
		}
		defer svc.stop()
		layout, err = waitLayoutComplete(ctx, svc.store, world, svc.wait(layoutTimeout), report)
		if err != nil {
			return fmt.Errorf("layout: %w", err)
		}
		svc.stop()
		report["layout_authority"] = svc.keepAlive.snapshot()

		h, err = reopenHarness()
		if err != nil {
			return err
		}
	}
	// The audits below run on a resumed checkpoint too: they are cheap
	// reads, and they prove the save still carries the layout it claims.
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
	// Independent audit: with every wall and barricade of the layout
	// blocked (traps and fences stay walkable for colonists, as natively:
	// the fence lane is the safe lane) every colonist still reaches the
	// entry and every colony door, and nobody lost a cell.
	var impassable []domain.Cell
	for _, tier := range layout.Tiers {
		for _, b := range tier.Buildings {
			if b.Definition != "TrapSpike" && b.Definition != "Fence" {
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
	if err := assertAccess(access); err != nil {
		return fmt.Errorf("spatial access after layout: %w", err)
	}
	report["spatial_access_after_layout"] = access
	// Cover for the firing line: every firing cell sees some cell of the
	// trap lane raiders must walk, the chokepoint mouth is covered, and the
	// line has cover. Flank cells sit behind the funnel walls, so the mouth
	// itself is only visible from the centre.
	fire, err := h.Wire(ctx, "lines-of-fire", "observations_read_lines_of_fire", map[string]any{
		"scope": map[string]any{"expectedIdentity": identity}, "firingCells": cellsJSON(layout.Firing), "approachCells": cellsJSON(layout.TrapLane),
	})
	if err != nil {
		return err
	}
	report["lines_of_fire_after_layout"] = fire
	if err := assertCover(fire, layout); err != nil {
		return fmt.Errorf("lines of fire after layout: %w", err)
	}
	if v.writeCheckpoint {
		committed := committedCheckpoints()
		if err := writeCheckpoint(ctx, h, root, committed, checkpointName, layoutCheckpoint{Layout: layout, SiteX: siteX, SiteZ: siteZ, SavedAtTick: int64(na.AsNumber(after["tick"]))}); err != nil {
			return fmt.Errorf("checkpoint: %w", err)
		}
		report["checkpoint"] = map[string]any{"name": checkpointName, "path": checkpointPath(root, checkpointName), "committed": committed}
		return nil
	}

	// The fixture closure reads h; reopening must rebind it here.
	reopenFixture := func() error {
		var err error
		h, err = reopenHarness()
		return err
	}
	if v.threat == "predator" {
		return runPredator(ctx, closeClient, reopenFixture, fixture, launch, layout, v, report)
	}
	if v.turrets {
		// Scenario T1: the observed gates open and the planner adds the
		// turret tier to the stored layout and builds it natively.
		anchor := turretGeneratorAnchor(layout)
		power, err := fixture("power", map[string]any{"op": "power", "x": int(anchor.X), "z": int(anchor.Z)})
		if err != nil {
			return err
		}
		report["power"] = power
		if err := closeClient(); err != nil {
			return err
		}
		svc, err = launch("turrets")
		if err != nil {
			return err
		}
		defer svc.stop()
		layout, err = waitTurretTier(ctx, svc.store, world, svc.wait(turretTimeout), report)
		if err != nil {
			return fmt.Errorf("turret tier: %w", err)
		}
		svc.stop()
		report["turret_authority"] = svc.keepAlive.snapshot()
		if err := reopenFixture(); err != nil {
			return err
		}
		built, err := fixture("inspect-after-turrets", map[string]any{"op": "inspect"})
		if err != nil {
			return err
		}
		report["inspect_after_turrets"] = built
		if err := assertTurretsPowered(built, layout); err != nil {
			return fmt.Errorf("after the turret tier: %w", err)
		}
	}
	// Scenario 2/3: a real raid.
	raidArgs := map[string]any{"op": "raid", "strategy": v.strategy, "arrival": v.arrival}
	if v.turrets {
		// The turret scenario needs the raid to come through the corridor
		// the turrets cover: it walks in from the map edge nearest the
		// corridor entry rather than any edge the raid worker picks.
		raidArgs["x"], raidArgs["z"] = int(layout.Entry.X), int(layout.Entry.Z)
	}
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
	wantPrefix := "hold-"
	if v.bypass {
		wantPrefix = "squad-"
	}
	method, err := waitCombatMethod(ctx, svc.store, svc.wait(raidTimeout), report)
	if err != nil {
		return fmt.Errorf("combat response: %w", err)
	}
	if !strings.HasPrefix(string(method.Method), wantPrefix) {
		return fmt.Errorf("combat method %q, expected prefix %q", method.Method, wantPrefix)
	}
	plan, err := svc.store.LoadPlan(ctx, method.Plan)
	if err != nil {
		return err
	}
	if wantPrefix == "hold-" {
		if err := assertHoldPlan(plan.Spec, layout); err != nil {
			return err
		}
	} else if err := assertNoLinePosition(plan.Spec, layout); err != nil {
		return err
	}
	if !v.bypass {
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
		released, err := waitDefendersReleased(ctx, svc.store, method.Plan, "hold-", svc.wait(repairTimeout))
		report["defenders_released"] = released
		if err != nil {
			return fmt.Errorf("draft release after raid: %w", err)
		}
		if v.turrets {
			// Scenario T2/T3: firing evidence, then the staged loss and the
			// routine restoration under a fresh service with the upkeep
			// families.
			svc.stop()
			report["raid_authority"] = svc.keepAlive.snapshot()
			return runTurretUpkeep(ctx, closeClient, reopenFixture, fixture, launch, layout, world, int64(na.AsNumber(raid["tick"])), report)
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
	}
	svc.stop()
	if v.bypass {
		report["raid_authority"] = svc.keepAlive.snapshot()
	} else {
		report["repair_authority"] = svc.keepAlive.snapshot()
	}

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
	if !v.bypass {
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
		breachReport, _ := na.AsMap(report["breach_after_raid"])
		if breached, ok := na.AsMap(breachReport["breached"]); ok && !hasCell(na.AsSlice(final["trapCells"]), int(na.AsNumber(breached["x"])), int(na.AsNumber(breached["z"]))) {
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
	}
	return nil
}

// launchService starts rimgovernor serve against the shared game (the
// harness session must be closed), waits for it to attach to the fixture's
// identity, submits the one player building plan the routine arbitration
// slot needs, acquires authority and keeps it alive, and opens the journal.
// writeCheckpoint saves the running game and stages the save beside the
// baseline under root/profile/Saves, where Prepare copies every save into the
// headless profile, so a later -from-checkpoint run loads it like any other;
// the same two files go to the committed checkpoint directory when set.
// The game writes into the active profile's Saves directory: headless runs
// use root/headless-profile, rendered runs root/profile itself.
func writeCheckpoint(ctx context.Context, h *na.Harness, root, checkpoints, name string, cp layoutCheckpoint) error {
	started := time.Now()
	if _, err := h.Call(ctx, "save-checkpoint", "rimworld/save_game", map[string]any{"saveName": name}); err != nil {
		return err
	}
	staged := filepath.Join(root, "profile", "Saves", name+".rws")
	deadline := started.Add(60 * time.Second)
	for {
		for _, profile := range []string{"headless-profile", "profile"} {
			candidate := filepath.Join(root, profile, "Saves", name+".rws")
			info, err := os.Stat(candidate)
			if err != nil || info.Size() == 0 || info.ModTime().Before(started.Add(-time.Second)) {
				continue
			}
			if candidate != staged {
				if err := copyFile(candidate, staged); err != nil {
					return err
				}
			}
			data, err := json.MarshalIndent(cp, "", "  ")
			if err != nil {
				return err
			}
			if err := os.WriteFile(checkpointPath(root, name), data, 0644); err != nil {
				return err
			}
			if checkpoints == "" {
				return nil
			}
			if err := os.MkdirAll(checkpoints, 0755); err != nil {
				return err
			}
			if err := copyFile(staged, filepath.Join(checkpoints, name+".rws")); err != nil {
				return err
			}
			return os.WriteFile(filepath.Join(checkpoints, name+".checkpoint.json"), data, 0644)
		}
		if time.Now().After(deadline) {
			return fmt.Errorf("save %s.rws did not appear under root/headless-profile/Saves or root/profile/Saves", name)
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(time.Second):
		}
	}
}

func copyFile(src, dst string) error {
	data, err := os.ReadFile(src)
	if err != nil {
		return err
	}
	return os.WriteFile(dst, data, 0644)
}

func launchService(ctx context.Context, s cases.Session, name string, identity map[string]any, siteX, siteZ int, report na.Report) (*service, error) {
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
	// The player's accepted building project occupies the routine
	// arbitration slot (policy.RankDevelopment); it is submitted once per
	// journal and replayed idempotently on a relaunch.
	submission, status, err := svc.API("POST", "/api/buildings/plans", map[string]any{
		"requestId": "defense-layout-construction-1", "expected": identity,
		"building": map[string]any{"defName": "Wall", "x": siteX, "z": siteZ, "rotation": "north", "stuff": "WoodLog"},
	}, svc.Token)
	if err != nil {
		return fail(err)
	}
	if status != 200 && status != 201 {
		return fail(fmt.Errorf("unexpected building submission status=%d body=%#v", status, submission))
	}
	if na.AsString(submission["planId"]) == "" || na.AsString(submission["revision"]) == "" {
		return fail(fmt.Errorf("unexpected building submission: %#v", submission))
	}
	report["submission_"+name] = submission
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

// waitLayoutComplete polls the journal until the stored layout for world is
// Complete, recording each tier method's plan and its final stage. The
// progress signature is the tier methods' plan stages and the stored record.
func waitLayoutComplete(ctx context.Context, s *store.Store, world store.World, w na.Wait, report na.Report) (store.DefenseLayoutRecord, error) {
	var goalID domain.GoalID
	var record store.DefenseLayoutRecord
	var stored bool
	tiers := map[string]any{}
	err := na.WaitProgress(ctx, w, func(ctx context.Context) (string, bool, error) {
		review, err := s.LoadRoutineReview(ctx)
		if err == nil && goalID == "" {
			for _, binding := range review.Goals {
				if binding.Need == policy.EnsureDefensiveLayout {
					goalID = binding.Goal
					report["layout_goal"] = string(goalID)
				}
			}
		}
		if goalID != "" {
			if goal, err := s.LoadGoal(ctx, goalID); err == nil {
				for _, m := range goal.Methods {
					entry := map[string]any{"plan": string(m.Plan), "epoch": m.Epoch}
					if plan, err := s.LoadPlan(ctx, m.Plan); err == nil {
						entry["stages"] = stages(plan.Progress)
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
		return na.Signature(goalID, tiers, stored, record.Complete), false, nil
	})
	if err != nil {
		return record, fmt.Errorf("layout not complete (goal=%q stored=%v): %w", goalID, stored, err)
	}
	return record, nil
}

// waitCombatMethod polls the journal for the ActiveCombat goal's first
// committed method after the raid.
func waitCombatMethod(ctx context.Context, s *store.Store, w na.Wait, report na.Report) (domain.GoalMethod, error) {
	var found domain.GoalMethod
	err := na.WaitProgress(ctx, w, func(ctx context.Context) (string, bool, error) {
		review, err := s.LoadRoutineReview(ctx)
		if err != nil {
			return na.Signature("no-review"), false, nil
		}
		for _, binding := range review.Goals {
			if binding.Need != policy.ActiveCombat {
				continue
			}
			goal, err := s.LoadGoal(ctx, binding.Goal)
			if err != nil && !errors.Is(err, store.ErrNotFound) {
				return "", false, err
			}
			if len(goal.Methods) > 0 {
				report["combat_goal"] = string(binding.Goal)
				report["combat_method"] = string(goal.Methods[0].Method)
				found = goal.Methods[0]
				return "", true, nil
			}
			return na.Signature("bound", binding.Goal, goal.Goal.Epoch), false, nil
		}
		return na.Signature("unbound", len(review.Goals)), false, nil
	})
	if err != nil {
		return domain.GoalMethod{}, fmt.Errorf("no ActiveCombat method: %w", err)
	}
	return found, nil
}

// waitHoldDispatched waits until every draft in the hold plan is completed
// and every move to a firing cell has been attempted natively: the furthest
// the plan can get before the first combat window lets ticks pass.
func waitHoldDispatched(ctx context.Context, s *store.Store, id domain.PlanID, w na.Wait) (map[string]any, error) {
	var out map[string]any
	_, err := na.WaitPlan(ctx, s, w, id, func(state store.PlanState) (string, bool, error) {
		st := stages(state.Progress)
		drafts, moves, attacks, ready := 0, 0, 0, true
		for _, p := range state.Progress {
			v := p.View()
			switch p.Action().Kind() {
			case domain.OwnedDraftAction:
				drafts++
				ready = ready && v.Stage == domain.Completed
			case domain.MovementAction:
				moves++
				ready = ready && (v.Attempt > 0 || v.Stage == domain.Completed)
			case domain.RangedAttackAction:
				attacks++
			}
		}
		out = map[string]any{"stages": st}
		if drafts == 0 || moves == 0 || attacks == 0 {
			return "", false, fmt.Errorf("hold plan has %d drafts, %d moves and %d attacks: %v", drafts, moves, attacks, st)
		}
		if ready {
			out = map[string]any{"stages": st, "drafts": drafts, "moves": moves, "attacks": attacks}
			return "", true, nil
		}
		if !domain.GoalWorkOpen(state.Progress) {
			return "", false, fmt.Errorf("hold plan settled before dispatch: %v", st)
		}
		return na.PlanSignature(state), false, nil
	})
	if err != nil {
		return out, fmt.Errorf("hold plan not dispatched: %w", err)
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
		review, err := s.LoadRoutineReview(ctx)
		if err != nil {
			return "", false, err
		}
		bound := false
		need := ""
		for _, binding := range review.Goals {
			if binding.Need != policy.ActiveCombat {
				continue
			}
			bound = true
			goal, err := s.LoadGoal(ctx, binding.Goal)
			if err != nil && !errors.Is(err, store.ErrNotFound) {
				return "", false, err
			}
			need = string(goal.Goal.Need)
			for _, m := range goal.Methods {
				if strings.HasPrefix(string(m.Method), "hold-") {
					holdPlans[m.Plan] = true
				}
			}
		}
		for id := range holdPlans {
			state, err := s.LoadPlan(ctx, id)
			if err != nil {
				return "", false, err
			}
			for _, p := range state.Progress {
				if m, ok := p.Action().Movement(); ok && p.View().Stage == domain.Completed {
					onLine[m.Pawn()] = true
				}
			}
		}
		line := make([]string, 0, len(onLine))
		for id := range onLine {
			line = append(line, string(id))
		}
		sort.Strings(line)
		out["defenders_on_firing_cells"] = line
		out["combat_goal_bound"] = bound
		out["combat_goal_need"] = need
		out["resolved_tick"] = int64(review.Tick)
		if combatWindows > 0 && (!bound || need == string(domain.NeedRecovered)) {
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

// runPredator is the -threat predator scenario (#157) after the layout is in
// place: the fixture spawns a wild predator inside the band already hunting a
// colonist, the service must answer it with squad defense (the census lists
// it under huntingPredators, not hostiles: the planner has to admit a
// hunting predator on its own), fight it through bounded combat windows
// until the ActiveCombat goal recovers, release the drafts and resume
// colony windows. Natively the predator must be dead, downed or gone and no
// colonist dead or drafted.
func runPredator(ctx context.Context, closeClient func() error, reopenHarness func() error,
	fixture func(string, map[string]any) (map[string]any, error), launch func(string) (*service, error),
	layout store.DefenseLayoutRecord, v variant, report na.Report) error {
	staged, err := fixture("predator", map[string]any{"op": "predator", "kind": v.predatorKind})
	if err != nil {
		return err
	}
	report["predator_incident"] = staged
	predatorID := na.AsString(staged["predator"])
	if predatorID == "" || na.AsString(staged["job"]) != "PredatorHunt" {
		return fmt.Errorf("predator fixture did not stage a hunt: %#v", staged)
	}
	if err := closeClient(); err != nil {
		return err
	}
	svc, err := launch("predator")
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
	if err := assertSquadTargets(plan.Spec, predatorID); err != nil {
		return err
	}
	resolved, err := waitHuntResolved(ctx, svc.store, svc.wait(raidTimeout))
	report["hunt_resolution"] = resolved
	if err != nil {
		return fmt.Errorf("hunt resolution: %w", err)
	}
	released, err := waitDefendersReleased(ctx, svc.store, method.Plan, "squad-", svc.wait(raidTimeout))
	report["defenders_released"] = released
	if err != nil {
		return fmt.Errorf("draft release after hunt: %w", err)
	}
	resumed, err := waitClockResumed(ctx, svc.store, svc.wait(raidTimeout))
	report["clock_resumed"] = resumed
	if err != nil {
		return fmt.Errorf("clock after hunt: %w", err)
	}
	svc.stop()
	report["raid_authority"] = svc.keepAlive.snapshot()
	if err := reopenHarness(); err != nil {
		return err
	}
	final, err := fixture("inspect-after-hunt", map[string]any{"op": "inspect"})
	if err != nil {
		return err
	}
	report["inspect_after_hunt"] = final
	predator, _ := na.AsMap(final["predator"])
	if na.AsString(predator["id"]) != predatorID {
		return fmt.Errorf("inspect lost the fixture predator: %#v", final)
	}
	spawned, _ := na.AsBool(predator["spawned"])
	dead, _ := na.AsBool(predator["dead"])
	downed, _ := na.AsBool(predator["downed"])
	if spawned && !dead && !downed {
		return fmt.Errorf("predator still up after the hunt resolved: %#v", predator)
	}
	for _, raw := range na.AsSlice(final["colonists"]) {
		row, _ := na.AsMap(raw)
		if dead, _ := na.AsBool(row["dead"]); dead {
			return fmt.Errorf("colonist %s died to the predator", na.AsString(row["id"]))
		}
		if drafted, _ := na.AsBool(row["drafted"]); drafted {
			return fmt.Errorf("colonist %s still drafted after the hunt resolved", na.AsString(row["id"]))
		}
	}
	return nil
}

// assertSquadTargets requires every attack in the squad plan to target the
// fixture predator: the only threat on the map is the hunting predator.
func assertSquadTargets(spec domain.PlanSpec, predatorID string) error {
	attacks := 0
	for _, action := range spec.Actions() {
		target := ""
		if a, ok := action.RangedAttack(); ok {
			target = string(a.Target())
		} else if a, ok := action.MeleeAttack(); ok {
			target = string(a.Target())
		} else {
			continue
		}
		attacks++
		if target != predatorID {
			return fmt.Errorf("squad plan %s attacks %s, not the hunting predator %s", spec.ID(), target, predatorID)
		}
	}
	if attacks == 0 {
		return fmt.Errorf("squad plan %s has no attack action", spec.ID())
	}
	return nil
}

// waitHuntResolved polls the journal while the service fights the predator:
// at least one combat window must have run, and the ActiveCombat goal must
// have recovered or be unbound.
func waitHuntResolved(ctx context.Context, s *store.Store, w na.Wait) (map[string]any, error) {
	out := map[string]any{}
	err := na.WaitProgress(ctx, w, func(ctx context.Context) (string, bool, error) {
		combatWindows, colonyWindows, acknowledged, err := clockWindows(ctx, s)
		if err != nil {
			return "", false, err
		}
		out["combat_windows"] = combatWindows
		out["colony_windows"] = colonyWindows
		out["last_acknowledged_hostiles"] = acknowledged
		review, err := s.LoadRoutineReview(ctx)
		if err != nil {
			return "", false, err
		}
		bound, need := false, ""
		for _, binding := range review.Goals {
			if binding.Need != policy.ActiveCombat {
				continue
			}
			bound = true
			goal, err := s.LoadGoal(ctx, binding.Goal)
			if err != nil && !errors.Is(err, store.ErrNotFound) {
				return "", false, err
			}
			need = string(goal.Goal.Need)
		}
		out["combat_goal_bound"] = bound
		out["combat_goal_need"] = need
		out["resolved_tick"] = int64(review.Tick)
		if combatWindows > 0 && (!bound || need == string(domain.NeedRecovered)) {
			return "", true, nil
		}
		return na.Signature(combatWindows, colonyWindows, bound, need), false, nil
	})
	if err != nil {
		return out, fmt.Errorf("hunt not resolved under the service (%#v): %w", out, err)
	}
	return out, nil
}

// waitClockResumed waits for a colony (non-combat) window applied after the
// last combat window: the clock is back to routine play, not parked on the
// hunt's hold.
func waitClockResumed(ctx context.Context, s *store.Store, w na.Wait) (map[string]any, error) {
	out := map[string]any{}
	err := na.WaitProgress(ctx, w, func(ctx context.Context) (string, bool, error) {
		attempts, err := s.LoadClockAttempts(ctx, 4096)
		if err != nil {
			return "", false, err
		}
		lastCombat, lastColony := -1, -1
		for i, a := range attempts {
			start := a.Intent.Command.Start
			if start == nil || a.Phase != store.ClockApplied {
				continue
			}
			if start.Policy.GetMode() == k.WatchMode_WATCH_MODE_COMBAT {
				lastCombat = i
			} else {
				lastColony = i
			}
		}
		out["last_combat_window"] = lastCombat
		out["last_colony_window"] = lastColony
		if lastCombat >= 0 && lastColony > lastCombat {
			return "", true, nil
		}
		return na.Signature(lastCombat, lastColony), false, nil
	})
	if err != nil {
		return out, fmt.Errorf("no colony window after the combat windows (%#v): %w", out, err)
	}
	return out, nil
}

// clockWindows counts the applied combat and colony windows in the journal
// and returns the hostiles the last combat window acknowledged.
func clockWindows(ctx context.Context, s *store.Store) (combat, colony int, acknowledged []string, err error) {
	attempts, err := s.LoadClockAttempts(ctx, 4096)
	if err != nil {
		return 0, 0, nil, err
	}
	for _, a := range attempts {
		start := a.Intent.Command.Start
		if start == nil || a.Phase != store.ClockApplied {
			continue
		}
		if start.Policy.GetMode() == k.WatchMode_WATCH_MODE_COMBAT {
			combat++
			acknowledged = start.Policy.AcknowledgedHostileIds
		} else {
			colony++
		}
	}
	return combat, colony, acknowledged, nil
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

// waitDefendersReleased waits until every draft the hold plans acquired is
// released or superseded: the recovered ActiveCombat goal no longer
// authorizes the plan, so the worker's draft cleanup returns each defender
// to colony work. Drafts never dispatched have nothing to release.
func waitDefendersReleased(ctx context.Context, s *store.Store, first domain.PlanID, prefix string, w na.Wait) (map[string]any, error) {
	out := map[string]any{}
	err := na.WaitProgress(ctx, w, func(ctx context.Context) (string, bool, error) {
		plans := map[domain.PlanID]bool{first: true}
		review, err := s.LoadRoutineReview(ctx)
		if err != nil {
			return "", false, err
		}
		for _, binding := range review.Goals {
			if binding.Need != policy.ActiveCombat {
				continue
			}
			goal, err := s.LoadGoal(ctx, binding.Goal)
			if err != nil && !errors.Is(err, store.ErrNotFound) {
				return "", false, err
			}
			for _, m := range goal.Methods {
				if strings.HasPrefix(string(m.Method), prefix) {
					plans[m.Plan] = true
				}
			}
		}
		drafts := map[string]string{}
		pending := 0
		for id := range plans {
			state, err := s.LoadPlan(ctx, id)
			if err != nil {
				return "", false, err
			}
			for _, p := range state.Progress {
				draft, ok := p.Action().OwnedDraft()
				if !ok {
					continue
				}
				v := p.View()
				stage := "not-dispatched"
				if v.Attempt > 0 {
					cleanup, known := v.DraftCleanup.Value()
					stage = "unknown"
					if known {
						stage = string(cleanup.Stage)
					}
					if !known || cleanup.Stage != domain.DraftReleased && cleanup.Stage != domain.DraftSuperseded {
						pending++
					}
				}
				drafts[string(id)+"/"+string(draft.Pawn())] = stage
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
		review, err := s.LoadRoutineReview(ctx)
		if err != nil {
			return "", false, err
		}
		combat := ""
		for _, binding := range review.Goals {
			goal, err := s.LoadGoal(ctx, binding.Goal)
			if err != nil && !errors.Is(err, store.ErrNotFound) {
				return "", false, err
			}
			switch binding.Need {
			case policy.ActiveCombat:
				combat = fmt.Sprintf("%s/%d", goal.Goal.ID, goal.Goal.Epoch)
			case policy.EnsureDefensiveLayout:
				for _, m := range goal.Methods {
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

// assertHoldPlan checks a hold-the-line plan carries draft, move and ranged
// attack per defender: every move targets one of the layout's firing cells
// and never a trap cell, and every attack depends on that defender's move.
func assertHoldPlan(spec domain.PlanSpec, layout store.DefenseLayoutRecord) error {
	if len(layout.Firing) == 0 {
		return errors.New("layout has no firing cells")
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
	drafts, attacks := 0, 0
	moves := map[domain.PawnID]domain.ActionID{}
	for _, a := range spec.Actions() {
		switch a.Kind() {
		case domain.OwnedDraftAction:
			drafts++
		case domain.MovementAction:
			m, _ := a.Movement()
			if traps[m.Destination()] {
				return fmt.Errorf("hold plan moves %s onto trap cell %+v", m.Pawn(), m.Destination())
			}
			if !firing[m.Destination()] {
				return fmt.Errorf("hold plan moves %s to %+v, not a firing cell %v", m.Pawn(), m.Destination(), layout.Firing)
			}
			moves[m.Pawn()] = a.ID()
		case domain.RangedAttackAction:
			attacks++
		}
	}
	if drafts == 0 || len(moves) == 0 || attacks == 0 {
		return fmt.Errorf("hold plan has %d drafts, %d moves and %d attacks", drafts, len(moves), attacks)
	}
	requires := map[domain.ActionID]map[domain.ActionID]bool{}
	for _, d := range spec.Dependencies() {
		if requires[d.Action] == nil {
			requires[d.Action] = map[domain.ActionID]bool{}
		}
		requires[d.Action][d.Requires] = true
	}
	for _, a := range spec.Actions() {
		attack, ok := a.RangedAttack()
		if !ok {
			continue
		}
		move, positioned := moves[attack.Pawn()]
		if !positioned {
			return fmt.Errorf("hold plan attacks with %s without a move to a firing cell", attack.Pawn())
		}
		if !requires[a.ID()][move] {
			return fmt.Errorf("hold plan attack %s does not depend on move %s", a.ID(), move)
		}
	}
	return nil
}

// assertNoLinePosition checks a bypass response is not a hold-the-line plan.
func assertNoLinePosition(spec domain.PlanSpec, layout store.DefenseLayoutRecord) error {
	if strings.HasPrefix(string(spec.ID()), "routine-defense-") && !strings.Contains(string(spec.ID()), "bypass") {
		for _, a := range spec.Actions() {
			if a.Kind() == domain.RangedAttackAction {
				return fmt.Errorf("bypass raid response used the hold-the-line plan %s", spec.ID())
			}
		}
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
func assertAccess(reply map[string]any) error {
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
		if int(na.AsNumber(p["lostCellCount"])) != 0 {
			return fmt.Errorf("colonist %v loses cells with the layout blocked: %#v", p["pawn"], p)
		}
		for _, t := range na.AsSlice(p["targets"]) {
			target, _ := na.AsMap(t)
			native, _ := na.AsBool(target["nativeReachable"])
			projected, _ := na.AsBool(target["projectedReachable"])
			if !native || !projected {
				return fmt.Errorf("colonist %v cannot reach %v with the layout blocked", p["pawn"], target["cell"])
			}
		}
	}
	return nil
}

// assertCover requires every firing cell to see at least one trap-lane cell,
// at least one firing cell to see the chokepoint mouth, and a positive cover
// block chance on at least one line.
func assertCover(reply map[string]any, layout store.DefenseLayoutRecord) error {
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return err
	}
	lines := na.AsSlice(observed["lines"])
	if len(lines) == 0 {
		return errors.New("no lines of fire observed")
	}
	sees := map[domain.Cell]bool{}
	mouth, covered := false, 0
	for _, raw := range lines {
		line, _ := na.AsMap(raw)
		seen, _ := na.AsBool(line["lineOfSight"])
		if !seen {
			continue
		}
		from, _ := na.AsMap(line["from"])
		to, _ := na.AsMap(line["to"])
		sees[domain.Cell{X: int32(na.AsNumber(from["x"])), Z: int32(na.AsNumber(from["z"]))}] = true
		if (domain.Cell{X: int32(na.AsNumber(to["x"])), Z: int32(na.AsNumber(to["z"]))}) == layout.Chokepoint {
			mouth = true
		}
		if na.AsNumber(line["shooterCover"]) > 0 {
			covered++
		}
	}
	for _, c := range layout.Firing {
		if !sees[c] {
			return fmt.Errorf("firing cell %+v has no line of sight to any trap-lane cell: %#v", c, lines)
		}
	}
	if !mouth {
		return fmt.Errorf("no firing cell sees the chokepoint %+v: %#v", layout.Chokepoint, lines)
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
			time.Sleep(time.Second)
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
		case <-time.After(2 * time.Second):
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
