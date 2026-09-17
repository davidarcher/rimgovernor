// Command defenselayoutaccept exercises the defensive-layout vertical (issue
// #5, B06c) end to end against a live game and a live rimgovernor "serve"
// service: on a constrained site (a granite band with one corridor gap, laid
// by the private test/defense_setup fixture) the RoutineDefenseLayoutPlanner
// chooses the chokepoint, builds every tier natively (firing line, funnel,
// trap corridor), and the stored layout is re-verified by an independent
// native spatial-access read with its walls and barricades blocked and by a native
// inspection that no colonist stands on a trap. A real RaidEnemy incident
// (chosen strategy and arrival) is then raised and the RoutineDefensePlanner
// must respond with hold-the-line (method "hold-…") for an ordinary edge
// assault, or with squad defense ("squad-…") when the raid bypasses the line
// (-strategy ImmediateAttackSappers or -arrival CenterDrop). For the edge
// raid the service then holds the raid itself -- the scheduler admits
// bounded combat watch windows acknowledging the live hostiles while the
// ActiveCombat goal has an admitted plan (#69) -- and the run waits for the
// goal to recover, then asserts natively that the raiders are dead or downed
// and at least one trap sprung.
//
// Like routinehaulaccept, only one GABP client may hold the game at a time:
// the harness's fixture session and the service's session are used strictly
// in turn, and games_stop is called exactly once at the end.
package main

import (
	"bufio"
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
)

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-defense-layout-acceptance)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	save := flag.String("save", "RimGovernor-tribal8-baseline", "save under profile/Saves to load (empty starts a random debug colony)")
	rimgovernorBinary := flag.String("rimgovernor", "", "absolute path to a prebuilt rimgovernor binary (go build ./go/cmd/rimgovernor)")
	strategy := flag.String("strategy", "ImmediateAttack", "RaidStrategyDef for the raid (ImmediateAttack, ImmediateAttackSappers, ...)")
	arrival := flag.String("arrival", "EdgeWalkIn", "PawnsArrivalModeDef for the raid (EdgeWalkIn, CenterDrop, ...)")
	bypass := flag.Bool("bypass", false, "the raid bypasses the line: expect squad defense, not hold-the-line, and skip the trap-trigger wait")
	layoutTimeout := flag.Duration("layout-timeout", 20*time.Minute, "budget for the layout to be built natively")
	raidTimeout := flag.Duration("raid-timeout", 8*time.Minute, "budget for the raid response and resolution")
	timeout := flag.Duration("timeout", 45*time.Minute, "overall run timeout")
	checkpoint := flag.String("checkpoint", "", "after the layout is built and audited, save the game under this name (root/profile/Saves/<name>.rws plus <name>.checkpoint.json) so later runs can start at the raid")
	fromCheckpoint := flag.String("from-checkpoint", "", "load this checkpoint instead of building the layout: re-audits the saved layout, then stages the raid")
	checkpoints := flag.String("checkpoints", "scripts/fixtures/saves", "committed checkpoint directory: -checkpoint also writes the save and its .checkpoint.json here, -from-checkpoint stages them from here into root/profile/Saves when the root lacks them")
	flag.Parse()
	if *root == "" || *rimgovernorBinary == "" || !filepath.IsAbs(*rimgovernorBinary) {
		fmt.Fprintln(os.Stderr, "-root and an absolute -rimgovernor are required")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-defense-layout-acceptance"
	}
	if err := os.MkdirAll(*output, 0755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	if entries, _ := os.ReadDir(*output); len(entries) > 0 {
		fmt.Fprintln(os.Stderr, "-output must be a fresh, empty directory")
		os.Exit(2)
	}
	report := na.NewReport("Native defensive layout vertical (#5 M4): the routine planner chooses the corridor chokepoint on a "+
		"fixture-constrained site, builds every tier natively, the layout is re-verified by independent spatial-access and "+
		"trap-cell reads, and a real RaidEnemy incident is answered with hold-the-line (edge assault) or squad defense (bypass).", !*rendered)
	ctx, cancel := context.WithTimeout(context.Background(), *timeout)
	defer cancel()
	if *checkpoint != "" && *fromCheckpoint != "" {
		fmt.Fprintln(os.Stderr, "-checkpoint and -from-checkpoint are exclusive")
		os.Exit(2)
	}
	if *fromCheckpoint != "" {
		*save = *fromCheckpoint
	}
	opts := options{strategy: *strategy, arrival: *arrival, save: *save, bypass: *bypass, layoutTimeout: *layoutTimeout, raidTimeout: *raidTimeout, checkpoint: *checkpoint, fromCheckpoint: *fromCheckpoint, checkpoints: *checkpoints}
	if opts.fromCheckpoint != "" {
		if err := stageCheckpoint(*root, opts.checkpoints, opts.fromCheckpoint); err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(2)
		}
	}
	if err := run(ctx, *root, *output, *game, !*rendered, *rimgovernorBinary, opts, report); err != nil {
		report["error"] = err.Error()
	} else {
		report["passed"] = true
	}
	os.Exit(report.Finalize(*output))
}

type options struct {
	strategy, arrival, save    string
	bypass                     bool
	layoutTimeout, raidTimeout time.Duration
	// checkpoint names the save written once the layout is built and
	// audited; fromCheckpoint names one to resume from, skipping the
	// fixture setup and the layout scenario (AGENTS.md: stage the slow
	// precondition, then advance only the ticks the assertion needs).
	checkpoint, fromCheckpoint string
	// checkpoints is the committed directory the checkpoint files also
	// live in (scripts/fixtures/saves), so a fresh checkout can resume.
	checkpoints string
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

// stageCheckpoint copies a committed checkpoint into root/profile/Saves when
// the root does not already hold both files; Prepare then copies them into
// the headless profile like any other save.
func stageCheckpoint(root, checkpoints, name string) error {
	for _, file := range []string{name + ".rws", name + ".checkpoint.json"} {
		target := filepath.Join(root, "profile", "Saves", file)
		if _, err := os.Stat(target); err == nil {
			continue
		}
		source := filepath.Join(checkpoints, file)
		if _, err := os.Stat(source); err != nil {
			return fmt.Errorf("checkpoint %s: %s missing from both %s and %s (run once with -checkpoint %s)", name, file, filepath.Join(root, "profile", "Saves"), checkpoints, name)
		}
		if err := copyFile(source, target); err != nil {
			return err
		}
	}
	return nil
}

type apiFunc func(method, path string, body map[string]any, token string) (map[string]any, int, error)

// service is one launch of the rimgovernor serve subprocess against the
// shared game, with the harness's player authority acquired and kept alive.
type service struct {
	cmd       *exec.Cmd
	done      chan error
	stopped   bool
	exited    bool
	exit      error
	api       apiFunc
	token     string
	url       string
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
	if s.keepAlive != nil && s.api != nil {
		_, _, _ = s.api("POST", "/api/player/control/pause", map[string]any{
			"requestId": fmt.Sprintf("defense-%s-pause-%d", s.keepAlive.name, time.Now().UnixNano()), "expected": s.keepAlive.identity,
		}, s.token)
	}
	if s.store != nil {
		s.store.Close()
	}
	if s.exited {
		return
	}
	if s.cmd.ProcessState == nil {
		_ = s.cmd.Process.Kill()
	}
	<-s.done
}

// exitedErr is the journal waits' na.Wait.Terminal: a service that died on
// its own ends a wait at once.
func (s *service) exitedErr() error {
	if !s.exited {
		select {
		case s.exit = <-s.done:
			s.exited = true
		default:
			return nil
		}
	}
	if s.exit == nil {
		return fmt.Errorf("service %d exited with status 0", s.cmd.Process.Pid)
	}
	return fmt.Errorf("service %d exited: %w", s.cmd.Process.Pid, s.exit)
}

// wait bounds a journal poll by budget, the shared stall budget and the
// service's own exit.
func (s *service) wait(budget time.Duration) na.Wait {
	return na.Wait{Ceiling: budget, Stall: na.StallBudget(), Terminal: s.exitedErr}
}

func run(ctx context.Context, root, output, gameID string, headless bool, rimgovernorBinary string, opts options, report na.Report) error {
	if abs, err := filepath.Abs(root); err == nil {
		root = abs
	}
	if abs, err := filepath.Abs(output); err == nil {
		output = abs
	}
	cfg := &na.Config{Root: root, Output: output, Headless: headless, GameID: gameID}
	// The save carries its own expansion list; a Core-only profile would refuse it.
	if err := cfg.UseSaveExpansions(opts.save); err != nil {
		return err
	}
	if err := cfg.PrepareConfig(); err != nil {
		return fmt.Errorf("prepare profile: %w", err)
	}
	game, err := cfg.GameSection()
	if err != nil {
		return err
	}
	files, err := na.PackageFiles(fmt.Sprint(game["workingDir"]))
	if err != nil {
		return err
	}
	report["package_files"] = files
	gabsExecutable, err := na.GABSExecutable(root, cfg.Configuration)
	if err != nil {
		return err
	}
	binarySHA, err := sha256File(rimgovernorBinary)
	if err != nil {
		return fmt.Errorf("hash rimgovernor binary: %w", err)
	}
	report["rimgovernor_binary"] = map[string]string{"path": rimgovernorBinary, "sha256": binarySHA}
	report["raid"] = map[string]any{"strategy": opts.strategy, "arrival": opts.arrival, "bypass": opts.bypass}

	openHarness := func() (*bridge.Client, *na.Harness, error) {
		c, err := na.OpenSession(ctx, gabsExecutable, cfg.Configuration, gameID, 60*time.Second)
		if err != nil {
			return nil, nil, err
		}
		return c, na.NewHarness(c, output), nil
	}
	// The service's GABS subprocess releases the game slot shortly after the
	// service is killed, not synchronously: retry the reopen. The service
	// leaves the game running, and every fixture op needs a paused map, so
	// the reopened session pauses first.
	reopenHarness := func() (*bridge.Client, *na.Harness, error) {
		deadline := time.Now().Add(45 * time.Second)
		for {
			c, h, err := openHarness()
			if err == nil {
				if _, err := h.Call(ctx, "pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
					return nil, nil, err
				}
				return c, h, nil
			}
			if time.Now().After(deadline) {
				return nil, nil, fmt.Errorf("reopen bridge session: %w", err)
			}
			select {
			case <-ctx.Done():
				return nil, nil, ctx.Err()
			case <-time.After(time.Second):
			}
		}
	}
	stopGame := func(c *bridge.Client) {
		stopCtx, stopCancel := context.WithTimeout(context.Background(), 60*time.Second)
		defer stopCancel()
		if stopped, err := c.GamesStop(stopCtx); err == nil {
			report["stop"] = string(stopped.Envelope)
		} else {
			report["stop_error"] = err.Error()
		}
	}

	client, h, err := openHarness()
	if err != nil {
		return err
	}
	// Whatever happens after the game starts, stop it exactly once at the
	// end so a failed run never leaves a stray RimWorld holding the slot;
	// the service (deferred later, so run first) is already stopped by then.
	var open *bridge.Client = client
	closeClient := func() error {
		err := open.Close()
		open = nil
		return err
	}
	defer func() {
		if open == nil {
			c, _, err := reopenHarness()
			if err != nil {
				report["stop_error"] = err.Error()
				return
			}
			open = c
		}
		stopGame(open)
		_ = open.Close()
	}()
	// The baseline save keeps the site deterministic; a random debug colony
	// can spawn beside ruins the rock band cannot close.
	if opts.save != "" {
		if _, err := h.Call(ctx, "load-save", "rimworld/load_game_ready", map[string]any{"saveName": opts.save, "readiness": "visual", "timeoutMs": 120000, "ignoreModCompatibility": false}); err != nil {
			return err
		}
	} else if _, err := na.StartDebugGame(ctx, h, nil, na.Loud); err != nil {
		return err
	}
	if _, err := h.Call(ctx, "pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	// ConfirmColonyNames (priority 0) blocks every other goal until the
	// naming dialog is dismissed; do so from the harness's own session.
	facts, err := h.Call(ctx, "colony-facts", "home/colony_facts", map[string]any{})
	if err != nil {
		return err
	}
	if naming, ok := na.AsMap(facts["colonyNaming"]); ok && naming != nil {
		confirmed, err := h.Call(ctx, "confirm-colony-names", "home/confirm_colony_names", map[string]any{
			"windowId": int(na.AsNumber(naming["windowId"])), "factionName": na.AsString(naming["factionName"]),
			"settlementName": na.AsString(naming["settlementName"]), "dryRun": false,
		})
		if err != nil {
			return err
		}
		if success, _ := na.AsBool(confirmed["success"]); !success {
			return fmt.Errorf("confirm_colony_names refused: %#v", confirmed)
		}
		report["confirmed_colony_names"] = confirmed
	}

	identityReply, err := h.Wire(ctx, "identity", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return err
	}
	_, loaded, err := na.Outcome(identityReply, "loaded")
	if err != nil {
		return err
	}
	loadedContext, _ := na.AsMap(loaded["context"])
	identity, _ := na.AsMap(loadedContext["identity"])
	world := store.World{Colony: domain.ColonyID(na.AsString(identity["colonyId"])), Load: domain.LoadID(na.AsString(identity["loadToken"])), Map: domain.MapID(na.AsNumber(identity["mapId"]))}
	matchesIdentity := func(v map[string]any) bool {
		return na.AsString(v["colonyId"]) == na.AsString(identity["colonyId"]) &&
			na.AsString(v["loadToken"]) == na.AsString(identity["loadToken"]) &&
			na.AsNumber(v["mapId"]) == na.AsNumber(identity["mapId"])
	}

	names, err := h.Discovery(ctx)
	if err != nil {
		return err
	}
	report["discovery"] = names
	for _, want := range []string{"test/defense_setup", "test/guarded_construction_prepare"} {
		if !na.Contains(names, want) {
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
	statePath := filepath.Join(output, "service.sqlite")
	var svc *service
	launch := func(name string) (*service, error) {
		return launchService(ctx, output, name, rimgovernorBinary, gabsExecutable, cfg.Configuration, gameID, statePath, identity, matchesIdentity, siteX, siteZ, report)
	}
	if opts.fromCheckpoint != "" {
		data, err := os.ReadFile(checkpointPath(root, opts.fromCheckpoint))
		if err != nil {
			return fmt.Errorf("read checkpoint: %w", err)
		}
		var cp layoutCheckpoint
		if err := json.Unmarshal(data, &cp); err != nil {
			return fmt.Errorf("decode checkpoint: %w", err)
		}
		layout, siteX, siteZ = cp.Layout, cp.SiteX, cp.SiteZ
		report["checkpoint"] = map[string]any{"name": opts.fromCheckpoint, "savedAtTick": cp.SavedAtTick}
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
		if success, _ := na.AsBool(construction["success"]); !success || !matchesIdentity(construction) {
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
		layout, err = waitLayoutComplete(ctx, svc.store, world, svc.wait(opts.layoutTimeout), report)
		if err != nil {
			return fmt.Errorf("layout: %w", err)
		}
		svc.stop()
		report["layout_authority"] = svc.keepAlive.snapshot()

		client, h, err = reopenHarness()
		if err != nil {
			return err
		}
		open = client
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
	if opts.checkpoint != "" {
		if err := writeCheckpoint(ctx, h, root, opts.checkpoints, opts.checkpoint, layoutCheckpoint{Layout: layout, SiteX: siteX, SiteZ: siteZ, SavedAtTick: int64(na.AsNumber(after["tick"]))}); err != nil {
			return fmt.Errorf("checkpoint: %w", err)
		}
		report["checkpoint"] = map[string]any{"name": opts.checkpoint, "path": checkpointPath(root, opts.checkpoint), "committed": opts.checkpoints}
	}

	// Scenario 2/3: a real raid.
	raid, err := fixture("raid", map[string]any{"op": "raid", "strategy": opts.strategy, "arrival": opts.arrival})
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
	if opts.bypass {
		wantPrefix = "squad-"
	}
	method, err := waitCombatMethod(ctx, svc.store, svc.wait(opts.raidTimeout), report)
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
	if !opts.bypass {
		// The service holds the raid: with the hold plan admitted the
		// scheduler runs bounded combat windows that acknowledge the live
		// hostiles, re-planning between them, until the ActiveCombat goal
		// recovers. The fixture can only inspect from the harness session,
		// so the outcome is read natively once the service is gone.
		dispatched, err := waitHoldDispatched(ctx, svc.store, method.Plan, svc.wait(opts.raidTimeout/2))
		report["hold_plan_dispatch"] = dispatched
		if err != nil {
			return fmt.Errorf("hold dispatch: %w", err)
		}
		resolved, err := waitRaidResolved(ctx, svc.store, method.Plan, svc.wait(opts.raidTimeout))
		report["raid_resolution"] = resolved
		if err != nil {
			return fmt.Errorf("raid resolution: %w", err)
		}
	}
	svc.stop()
	report["raid_authority"] = svc.keepAlive.snapshot()

	client, h, err = reopenHarness()
	if err != nil {
		return err
	}
	open = client
	final, err := fixture("inspect-after-raid", map[string]any{"op": "inspect"})
	if err != nil {
		return err
	}
	report["inspect_after_raid"] = final
	if on := na.AsSlice(final["colonistsOnTraps"]); len(on) != 0 {
		return fmt.Errorf("colonists standing on trap cells after raid: %v", on)
	}
	if !opts.bypass {
		// A spike trap is trapDestroyOnSpring: springing destroys it (its
		// auto-rebuild blueprint is not a colonist building), so a trap
		// missing since the layout audit is a sprung trap; the fixture's
		// armed-state count only covers rearmable traps.
		sprung := int(na.AsNumber(final["sprung"]))
		if lost := int(na.AsNumber(after["traps"])) - int(na.AsNumber(final["traps"])); lost > 0 {
			sprung += lost
		}
		report["traps_sprung"] = sprung
		if sprung == 0 {
			return fmt.Errorf("no trap sprung during the edge raid: %#v", final)
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
	logData, err := os.ReadFile(cfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), headless)
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

func launchService(ctx context.Context, output, name, binary, gabs, config, gameID, statePath string, identity map[string]any, matchesIdentity func(map[string]any) bool, siteX, siteZ int, report na.Report) (*service, error) {
	// One profile for every service: the state journal binds its clock
	// inbox to the first profile path and refuses any other.
	profileDir := filepath.Join(output, "service-profile")
	serviceDir := filepath.Join(output, "service-"+name)
	for _, dir := range []string{profileDir, serviceDir} {
		if err := os.MkdirAll(dir, 0755); err != nil {
			return nil, err
		}
	}
	argv := []string{"serve", "--profile", profileDir, "--gabs", gabs, "--config", config, "--game", gameID, "--state", statePath, "--listen", "127.0.0.1:0", "--refresh", "1s", "--timeout", "15s", "--flight-recorder", filepath.Join(serviceDir, "flight.jsonl")}
	report["service_argv_"+name] = append([]string{binary}, argv...)
	cmd := exec.CommandContext(ctx, binary, argv...)
	cmd.Env = append(os.Environ(), "RIMGOVERNOR_CLOCK_DEBUG=1", "RIMGOVERNOR_ROUTINE_FAMILIES=defensive-layout,defense")
	stdoutPipe, err := cmd.StdoutPipe()
	if err != nil {
		return nil, err
	}
	stderrFile, err := os.Create(filepath.Join(serviceDir, "stderr.log"))
	if err != nil {
		return nil, err
	}
	cmd.Stderr = stderrFile
	if err := cmd.Start(); err != nil {
		return nil, fmt.Errorf("start rimgovernor serve: %w", err)
	}
	svc := &service{cmd: cmd, done: make(chan error, 1)}
	go func() { svc.done <- cmd.Wait(); stderrFile.Close() }()
	reader := bufio.NewReader(stdoutPipe)
	firstLine, err := reader.ReadString('\n')
	if err != nil {
		svc.stop()
		return nil, fmt.Errorf("read service startup line: %w", err)
	}
	const prefix = "RimGovernor Go player service: "
	firstLine = strings.TrimSpace(firstLine)
	if !strings.HasPrefix(firstLine, prefix) {
		svc.stop()
		return nil, fmt.Errorf("unexpected service startup line: %q", firstLine)
	}
	svc.url = strings.TrimPrefix(firstLine, prefix)
	report["service_url_"+name] = svc.url
	stdoutLogFile, err := os.Create(filepath.Join(serviceDir, "stdout.log"))
	if err != nil {
		svc.stop()
		return nil, err
	}
	go func() { _, _ = io.Copy(stdoutLogFile, reader); stdoutLogFile.Close() }()

	httpClient := &http.Client{Timeout: 20 * time.Second}
	requestCounter := 0
	var counterMu sync.Mutex
	svc.api = func(method, path string, body map[string]any, token string) (map[string]any, int, error) {
		var reqBody io.Reader
		if body != nil {
			data, err := json.Marshal(body)
			if err != nil {
				return nil, 0, err
			}
			reqBody = bytes.NewReader(data)
		}
		req, err := http.NewRequestWithContext(ctx, method, svc.url+path, reqBody)
		if err != nil {
			return nil, 0, err
		}
		if body != nil {
			req.Header.Set("Content-Type", "application/json")
		}
		if token != "" {
			req.Header.Set("X-RimGovernor-Player", token)
		}
		req.Header.Set("Origin", svc.url)
		resp, err := httpClient.Do(req)
		if err != nil {
			return nil, 0, err
		}
		defer resp.Body.Close()
		data, err := io.ReadAll(resp.Body)
		if err != nil {
			return nil, 0, err
		}
		counterMu.Lock()
		requestCounter++
		n := requestCounter
		counterMu.Unlock()
		_ = os.WriteFile(filepath.Join(serviceDir, fmt.Sprintf("http-%04d.json", n)), mustJSON(map[string]any{
			"method": method, "path": path, "request": body, "status": resp.StatusCode, "response": json.RawMessage(data),
		}), 0644)
		var out map[string]any
		if len(data) > 0 {
			if err := json.Unmarshal(data, &out); err != nil {
				return nil, resp.StatusCode, fmt.Errorf("decode %s %s: %w", method, path, err)
			}
		}
		return out, resp.StatusCode, nil
	}
	fail := func(err error) (*service, error) { svc.stop(); return nil, err }

	health, status, err := svc.api("GET", "/api/health", nil, "")
	if err != nil {
		return fail(err)
	}
	if status != 200 || na.AsString(health["service"]) != "rimgovernor" || int(na.AsNumber(health["pid"])) != cmd.Process.Pid {
		return fail(fmt.Errorf("unexpected /api/health: status=%d body=%#v", status, health))
	}
	session, status, err := svc.api("GET", "/api/player/session", nil, "")
	if err != nil {
		return fail(err)
	}
	if status != 200 || na.AsString(session["token"]) == "" {
		return fail(fmt.Errorf("unexpected /api/player/session: status=%d body=%#v", status, session))
	}
	svc.token = na.AsString(session["token"])
	deadline := time.Now().Add(90 * time.Second)
	for {
		state, status, err := svc.api("GET", "/api/state", nil, "")
		if err != nil {
			return fail(err)
		}
		if status != 200 {
			return fail(fmt.Errorf("unexpected /api/state status=%d body=%#v", status, state))
		}
		if connected, _ := na.AsBool(state["connected"]); connected {
			if svcIdentity, ok := na.AsMap(state["identity"]); ok && matchesIdentity(svcIdentity) {
				break
			}
		}
		if time.Now().After(deadline) {
			return fail(fmt.Errorf("service did not attach to the fixture's identity in time: %#v", state))
		}
		select {
		case <-ctx.Done():
			return fail(ctx.Err())
		case <-time.After(500 * time.Millisecond):
		}
	}
	// The player's accepted building project occupies the routine
	// arbitration slot (policy.RankDevelopment); it is submitted once per
	// journal and replayed idempotently on a relaunch.
	submission, status, err := svc.api("POST", "/api/buildings/plans", map[string]any{
		"requestId": "defense-layout-construction-1", "expected": identity,
		"building": map[string]any{"defName": "Wall", "x": siteX, "z": siteZ, "rotation": "north", "stuff": "WoodLog"},
	}, svc.token)
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
	keep := &authorityKeepAlive{apiCall: svc.api, identity: identity, token: svc.token, name: name}
	if err := keep.start(); err != nil {
		return fail(err)
	}
	keepCtx, stopKeep := context.WithCancel(ctx)
	svc.keepAlive, svc.stopKeep = keep, stopKeep
	svc.keepWG.Add(1)
	go func() { defer svc.keepWG.Done(); keep.run(keepCtx) }()
	svc.store, err = openStoreWithRetry(ctx, statePath)
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
func (k *authorityKeepAlive) start() error {
	resumed, status, err := k.resume("resume")
	if err != nil {
		return err
	}
	record, _ := na.AsMap(resumed["record"])
	if status != 200 || na.AsString(record["phase"]) != "running" {
		return fmt.Errorf("resume was not running: status=%d body=%#v", status, resumed)
	}
	return nil
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
		if clk, clkStatus, clkErr := k.apiCall("GET", "/api/player/clock", nil, ""); clkErr == nil && clkStatus == 200 {
			if holds := na.AsSlice(clk["holds"]); len(holds) > 0 {
				ackBody := map[string]any{
					"requestId":        fmt.Sprintf("defense-%s-ack-%d", k.name, time.Now().UnixNano()),
					"expectedRevision": na.AsString(clk["revision"]), "throughCursor": na.AsString(clk["inboxCursor"]),
				}
				_, ackStatus, ackErr := k.apiCall("POST", "/api/player/clock/acknowledge", ackBody, k.token)
				k.mu.Lock()
				if ackErr != nil || ackStatus != 200 {
					k.acknowledgeFailed++
					k.lastError = fmt.Sprintf("acknowledge status=%d err=%v", ackStatus, ackErr)
				} else {
					k.acknowledged++
				}
				k.mu.Unlock()
			}
		}
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

func openStoreWithRetry(ctx context.Context, path string) (*store.Store, error) {
	deadline := time.Now().Add(60 * time.Second)
	for {
		s, err := store.Open(ctx, path)
		if err == nil {
			return s, nil
		}
		if time.Now().After(deadline) {
			return nil, err
		}
		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		case <-time.After(500 * time.Millisecond):
		}
	}
}

func sha256File(path string) (string, error) {
	f, err := os.Open(path)
	if err != nil {
		return "", err
	}
	defer f.Close()
	h := sha256.New()
	if _, err := io.Copy(h, f); err != nil {
		return "", err
	}
	return hex.EncodeToString(h.Sum(nil)), nil
}

func mustJSON(v any) []byte {
	data, err := json.MarshalIndent(v, "", "  ")
	if err != nil {
		return []byte("{}")
	}
	return data
}
