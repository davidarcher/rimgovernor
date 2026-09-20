package upkeep

import (
	"context"
	"fmt"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// upkeep/stone-shell (issue #293): MaintainStoneShell replaces one owned
// flammable wall through a routine bundle the Worker dispatches end to end,
// its demolition steps being WallRemovalActions (the kind
// routineExecutableKind used to omit, so every admitted bundle sat at
// pending with its stone backups standing). Ownership is the journal's own
// construction lineage, so, unlike the rest of this package, the case
// opens on the power fixture's rain scenario (a debug colony with an
// unroofed battery, PowerFixture.cs): stage one serves the power family,
// whose shelter method walls the battery into a wood room the game roofs
// (power/rain proves that enclosure; here it is only the fixture for the
// walls the controller then owns). Stage two stocks the granite blocks a
// replacement costs and serves the stone-shell family over the same
// journal: the goal must open on the deficit and its first bundle (three
// stone backups, the original's demolition, the stone replacement, the
// backups' removal) must complete by controller order. The independent
// native reads afterwards find the wood wall gone, a stone wall standing
// on its cell, the backup cells clear again, the room still enclosed and
// roofed, and the battery it shelters undamaged.
//
// No stage bundle (#329): a hit reloads the world and construction claims
// are scoped to the load token, so a cached enclosure would own nothing.
const (
	// stoneShellBudget is three times the measured Ultrafast pass (3m00s:
	// about 2m30s to a roofed battery, 15 s from the stone-shell deficit
	// to the audited bundle).
	stoneShellBudget = 10 * time.Minute
	// stoneShellBlocks is the stone stock the fixture drops beside a
	// colonist: a bundle costs four stone walls (three backups and the
	// replacement) at five blocks each.
	stoneShellBlocks = 60
)

func init() {
	cases.Register(cases.Case{
		Name: "upkeep/stone-shell",
		Scope: "Stone shell upkeep (issue #293): the power family walls an exposed battery into a wood room the controller then owns, " +
			"and the service composed with the stone-shell family follows the journal from deficit through one complete typed bundle " +
			"(stone backups, RemoveWall demolition, stone replacement, backup removal) that the Worker dispatches end to end, audited by " +
			"independent native wall, enclosure and power reads after the service releases the game.",
		Start:       cases.Fixture{Op: "test/power_prepare", Args: map[string]any{"scenario": "rain"}},
		RequiredOps: []string{"test/power_observe", "test/wall_material_loss", "test/wall_enclosure"},
		Serve:       &cases.ServeSpec{Families: []string{"stone-shell", "work"}, Prefix: "stone-shell"},
		Budget:      stoneShellBudget,
		Stall:       2 * time.Minute,
		Reason: "Two served stages over one journal: the controller first builds the wood room it will own (the power shelter), " +
			"then a second service replaces one of its walls; a stone wall under construction holds its stage past the default stall.",
		Run: runStoneShell,
	})
}

// stoneShellSite is the eligible replacement site the case records for one
// owned wall before the stone-shell service takes over.
type stoneShellSite struct {
	wall, stuff  string
	x, z, nx, nz int
	backups      []domain.Cell
}

func runStoneShell(ctx context.Context, s cases.Session) error {
	report, identity := s.Report(), s.Identity()
	for _, name := range []string{"test/power_observe", "test/wall_material_loss", "test/wall_enclosure", "rimgovernor/observations_list_wall_upgrade_sites"} {
		if !na.Contains(s.Names(), name) {
			return fmt.Errorf("missing %s in discovery; rebuild the native mod with -Fixture PowerFixture,WallUpgradeFixture", name)
		}
	}
	h := s.Harness()
	if _, err := na.ConfirmColonyNames(ctx, h, report); err != nil {
		return err
	}
	battery := na.AsString(s.Prepared()["battery"])
	if battery == "" {
		return fmt.Errorf("the rain fixture reported no battery: %#v", s.Prepared())
	}
	before, err := observePower(ctx, h, battery, "power-before")
	if err != nil {
		return err
	}
	report["power_before"] = before
	if roofed, _ := na.AsBool(before["roofed"]); roofed {
		return fmt.Errorf("power-before: the fixture battery is already roofed: %#v", before)
	}

	// Stage one: the power shelter, a wood room the controller owns.
	shelterReport := na.Report{}
	report["stage_shelter"] = shelterReport
	spec := s.Spec()
	spec.Families = []string{"power", "work", "naming"}
	spec.Prefix = "stone-shell-power"
	service, err := s.Serve(ctx, spec)
	if err != nil {
		return err
	}
	journal, err := serveStage(ctx, service, shelterReport)
	if err != nil {
		service.Stop()
		return err
	}
	owned, shelterErr := watchPowerShelter(ctx, journal, shelterReport)
	shelterReport["authority_reacquisitions"] = service.Stop()
	afterCtx := ctx
	if ctx.Err() != nil {
		var cancel context.CancelFunc
		afterCtx, cancel = context.WithTimeout(context.Background(), 90*time.Second)
		defer cancel()
	}
	if h, err = reattachPaused(afterCtx, s); err != nil {
		return fmt.Errorf("stage shelter: %w", err)
	}
	if shelterErr != nil {
		return fmt.Errorf("stage shelter: %w", shelterErr)
	}
	shelterReport["owned_walls"] = owned
	sheltered, err := observePower(ctx, h, battery, "power-sheltered")
	if err != nil {
		return err
	}
	shelterReport["power_after"] = sheltered
	shellReport := na.Report{"power_sheltered": sheltered}
	report["stage_shell"] = shellReport
	if roofed, _ := na.AsBool(sheltered["roofed"]); !roofed {
		return fmt.Errorf("stage shelter: the battery is not roofed after the power shelter completed: %#v", sheltered)
	}
	sites, err := readStoneShellSites(ctx, h, identity, owned)
	if err != nil {
		return err
	}
	shelterReport["eligible_sites"] = len(sites)
	if len(sites) == 0 {
		return fmt.Errorf("stage shelter: none of the %d owned walls offers a straight replacement site with three open backup cells", len(owned))
	}
	stuff := sites[0].stuff
	stocked, err := callFixture(ctx, h, identity, "test/wall_material_loss", map[string]any{"material": stuff, "restore": stoneShellBlocks})
	if err != nil {
		return err
	}
	shelterReport["stocked_blocks"] = stocked

	// Stage two: the stone-shell bundle over the same journal.
	service, err = s.Serve(ctx, s.Spec())
	if err != nil {
		return err
	}
	journal, err = serveStage(ctx, service, shellReport)
	if err != nil {
		service.Stop()
		return err
	}
	bundle, shellErr := watchStoneShell(ctx, journal, owned, stuff, shellReport)
	if shellErr == nil {
		shellErr = na.AssertRoutineRunning(service.Get)
	}
	shellReport["authority_reacquisitions"] = service.Stop()
	afterCtx = ctx
	if ctx.Err() != nil {
		var cancel context.CancelFunc
		afterCtx, cancel = context.WithTimeout(context.Background(), 90*time.Second)
		defer cancel()
	}
	if h, err = reattachPaused(afterCtx, s); err != nil {
		return fmt.Errorf("stage shell: %w", err)
	}
	if shellErr != nil {
		return fmt.Errorf("stage shell: %w", shellErr)
	}
	return verifyStoneShell(afterCtx, h, identity, battery, bundle, shellReport)
}

// stoneShellBundle is what the followed bundle replaced: the original wall
// and its site, the replacement's stuff, and the backup cells the bundle
// must have cleared again.
type stoneShellBundle struct {
	site  stoneShellSite
	stuff string
}

// watchPowerShelter follows EnsureBasicPower until its shelter method (a
// plan of Wall and Door builds) has every action completed and the goal
// reports recovered, then returns the construction identities of the walls
// the controller built: the flammable walls MaintainStoneShell may own.
func watchPowerShelter(ctx context.Context, journal *store.Store, report na.Report) ([]string, error) {
	deficitCtx, deficitCancel := context.WithTimeout(ctx, 4*time.Minute)
	defer deficitCancel()
	if _, err := waitNeed(deficitCtx, journal, policy.EnsureBasicPower, domain.NeedDeficit); err != nil {
		return nil, err
	}
	// Measured at Ultrafast: fixture to roofed battery in about two
	// minutes of wall-clock; a room still open at five is a failure.
	deadline := time.Now().Add(5 * time.Minute)
	// Recovery retires the goal's methods, so the plans are remembered
	// across polls and reloaded by id rather than read off the live goal.
	plans := map[domain.PlanID]bool{}
	for {
		review, err := journal.LoadRoutineReview(ctx)
		if err != nil {
			return nil, err
		}
		recovered := false
		for _, binding := range review.Goals {
			if binding.Need != policy.EnsureBasicPower {
				continue
			}
			g, err := journal.LoadGoal(ctx, binding.Goal)
			if err != nil {
				return nil, err
			}
			recovered = g.Goal.Need == domain.NeedRecovered
			methods := g.Methods
			if history, err := journal.LoadGoalMethods(ctx, g.Goal.ID, g.Goal.Epoch); err == nil && len(history) > len(methods) {
				methods = history
			}
			for _, m := range methods {
				plans[m.Plan] = true
			}
		}
		var walls []string
		shelter, open := false, false
		for id := range plans {
			p, err := journal.LoadPlan(ctx, id)
			if err != nil {
				return nil, err
			}
			for _, progress := range p.Progress {
				v := progress.View()
				b, ok := progress.Action().Building()
				if ok && b.Definition() == "Wall" {
					shelter = true
					if identity, known := v.Construction.Value(); known && v.Stage == domain.Completed {
						walls = append(walls, identity.Current)
					}
				}
				open = open || v.Stage != domain.Completed
			}
		}
		report["shelter_walls_completed"] = len(walls)
		if shelter && !open && recovered {
			if len(walls) == 0 {
				return nil, fmt.Errorf("the power shelter completed without a wall the journal owns")
			}
			return walls, nil
		}
		if time.Now().After(deadline) {
			return nil, fmt.Errorf("the power shelter did not complete: shelter=%v open=%v recovered=%v plans=%d", shelter, open, recovered, len(plans))
		}
		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		case <-time.After(2 * time.Second):
		}
	}
}

// readStoneShellSites lists, for every owned wall, the straight replacement
// sites the planner would accept: eligible, with three open backup cells
// and a stone material catalog; the first material is the planner's choice.
func readStoneShellSites(ctx context.Context, h *na.Harness, identity map[string]any, owned []string) ([]stoneShellSite, error) {
	var sites []stoneShellSite
	for _, wall := range owned {
		reply, err := h.Wire(ctx, "sites-"+wall, "observations_list_wall_upgrade_sites", map[string]any{
			"scope": map[string]any{"expectedIdentity": identity}, "targetId": wall, "page": map[string]any{"limit": 256}})
		if err != nil {
			return nil, err
		}
		_, observed, err := na.Outcome(reply, "observed")
		if err != nil {
			return nil, fmt.Errorf("sites for %s: %w", wall, err)
		}
		for _, raw := range na.AsSlice(observed["sites"]) {
			row, _ := na.AsMap(raw)
			normal, _ := na.AsMap(row["normal"])
			nx, nz := int(na.AsNumber(normal["x"])), int(na.AsNumber(normal["z"]))
			cells := na.AsSlice(row["backupCells"])
			materials := na.AsSlice(row["replacementMaterials"])
			if nx != 0 && nz != 0 || na.AsString(row["blocker"]) != "" || len(cells) != 3 || len(na.AsSlice(row["completedBackups"])) != 0 || len(materials) == 0 {
				continue
			}
			material, _ := na.AsMap(materials[0])
			original, _ := na.AsMap(row["original"])
			building, _ := na.AsMap(original["building"])
			position, _ := na.AsMap(building["position"])
			site := stoneShellSite{wall: wall, stuff: na.AsString(material["stuff"]), x: int(na.AsNumber(position["x"])), z: int(na.AsNumber(position["z"])), nx: nx, nz: nz}
			for _, rawCell := range cells {
				cell, _ := na.AsMap(rawCell)
				site.backups = append(site.backups, domain.Cell{X: int32(na.AsNumber(cell["x"])), Z: int32(na.AsNumber(cell["z"]))})
			}
			sites = append(sites, site)
		}
	}
	return sites, nil
}

// watchStoneShell follows MaintainStoneShell from deficit through one
// complete bundle for an owned wall: every Wall build in stone, every
// demolition a WallRemovalAction, the original one of the walls stage one
// built, and every step Completed by the Worker (controller_order).
func watchStoneShell(ctx context.Context, journal *store.Store, owned []string, stuff string, report na.Report) (stoneShellBundle, error) {
	deficitCtx, deficitCancel := context.WithTimeout(ctx, 4*time.Minute)
	defer deficitCancel()
	goal, err := waitNeed(deficitCtx, journal, policy.MaintainStoneShell, domain.NeedDeficit)
	if err != nil {
		return stoneShellBundle{}, err
	}
	report["deficit_tick"] = int64(goal.Goal.Tick)
	state, err := followMethods(ctx, journal, policy.MaintainStoneShell, "shell", func(a domain.Action) error {
		switch a.Kind() {
		case domain.BuildingAction:
			b, _ := a.Building()
			if b.Definition() != "Wall" || b.Stuff() != stuff {
				return fmt.Errorf("bundle builds %s of %s, not a %s Wall", b.Definition(), b.Stuff(), stuff)
			}
		case domain.WallRemovalAction:
			removal, _ := a.WallRemoval()
			if removal.Original() != "" && !na.Contains(owned, removal.Original()) {
				return fmt.Errorf("bundle demolishes %s, not a wall the controller built (%s)", removal.Original(), strings.Join(owned, ","))
			}
		default:
			return fmt.Errorf("unexpected %s action in a stone-shell bundle", a.Kind())
		}
		return nil
	}, report)
	if err != nil {
		return stoneShellBundle{}, err
	}
	if report["shell_recovered_by"] != "controller_order" {
		return stoneShellBundle{}, fmt.Errorf("the bundle was not completed by the controller's own orders (%v)", report["shell_recovered_by"])
	}
	bundle := stoneShellBundle{stuff: stuff}
	removals, builds := 0, 0
	for _, progress := range state.Progress {
		v := progress.View()
		if v.Stage != domain.Completed {
			return stoneShellBundle{}, fmt.Errorf("bundle step %s ended %s, not completed: %#v", v.Action, v.Stage, v)
		}
		switch progress.Action().Kind() {
		case domain.WallRemovalAction:
			removals++
			if removal, _ := progress.Action().WallRemoval(); removal.Original() != "" {
				bundle.site = stoneShellSite{wall: removal.Original(), stuff: stuff, x: int(removal.X()), z: int(removal.Z()), nx: int(removal.NX()), nz: int(removal.NZ())}
			}
		case domain.BuildingAction:
			builds++
		}
	}
	// Every Wall build but the one on the original's cell is a backup the
	// bundle's last steps must have removed again.
	for _, progress := range state.Progress {
		b, ok := progress.Action().Building()
		if !ok || int(b.Cell().X) == bundle.site.x && int(b.Cell().Z) == bundle.site.z {
			continue
		}
		bundle.site.backups = append(bundle.site.backups, b.Cell())
	}
	report["shell_removals"] = removals
	report["shell_builds"] = builds
	if bundle.site.wall == "" || removals == 0 {
		return stoneShellBundle{}, fmt.Errorf("the completed bundle carried no demolition of an owned wall (%d removals, %d builds)", removals, builds)
	}
	return bundle, nil
}

// verifyStoneShell is the independent native read behind the bundle: the
// wood wall is gone, a stone wall of the bundle's stuff stands on its
// cell, no colonist wall stands on a backup cell, the room the wall
// bounded is still enclosed and fully roofed with no collapse pending, and
// the battery inside is roofed and undamaged.
func verifyStoneShell(ctx context.Context, h *na.Harness, identity map[string]any, battery string, bundle stoneShellBundle, report na.Report) error {
	site := bundle.site
	walls, err := readColonistWalls(ctx, h, identity, "walls-after")
	if err != nil {
		return err
	}
	if _, present := walls[site.wall]; present {
		return fmt.Errorf("the demolished wall %s still stands", site.wall)
	}
	var replacement string
	for id, w := range walls {
		if w.x == site.x && w.z == site.z {
			replacement = id
			if w.stuff != bundle.stuff {
				return fmt.Errorf("the wall on %d,%d is %s, not %s", site.x, site.z, w.stuff, bundle.stuff)
			}
		}
		for _, backup := range site.backups {
			if w.x == int(backup.X) && w.z == int(backup.Z) {
				return fmt.Errorf("backup wall %s still stands on %d,%d", id, w.x, w.z)
			}
		}
	}
	report["replacement_wall"] = replacement
	if replacement == "" {
		return fmt.Errorf("no colonist wall stands on the replaced cell %d,%d", site.x, site.z)
	}
	enclosure, err := h.Call(ctx, "enclosure-after", "test/wall_enclosure", map[string]any{"x": site.x, "z": site.z, "nx": site.nx, "nz": site.nz})
	if err != nil {
		return err
	}
	report["enclosure_after"] = enclosure
	enclosed, _ := na.AsBool(enclosure["enclosed"])
	roofed, _ := na.AsBool(enclosure["fullyRoofed"])
	collapse, _ := na.AsBool(enclosure["pendingCollapse"])
	if !enclosed || !roofed || collapse {
		return fmt.Errorf("the room behind the replaced wall is not an enclosed, fully roofed room without a pending collapse: %#v", enclosure)
	}
	after, err := observePower(ctx, h, battery, "power-after")
	if err != nil {
		return err
	}
	report["power_after"] = after
	if missing, _ := na.AsBool(after["missing"]); missing {
		return fmt.Errorf("the sheltered battery is gone: %#v", after)
	}
	if roofed, _ := na.AsBool(after["roofed"]); !roofed {
		return fmt.Errorf("the sheltered battery is no longer roofed: %#v", after)
	}
	if before, _ := na.AsMap(report["power_sheltered"]); before != nil && na.AsNumber(after["hitPoints"]) < na.AsNumber(before["hitPoints"]) {
		return fmt.Errorf("the sheltered battery was damaged during the replacement: %#v (before %#v)", after, before)
	}
	return nil
}

// observePower is the power fixture's independent read of one building.
func observePower(ctx context.Context, h *na.Harness, id, label string) (map[string]any, error) {
	reply, err := h.Call(ctx, label, "test/power_observe", map[string]any{"ids": id})
	if err != nil {
		return nil, err
	}
	if success, _ := na.AsBool(reply["success"]); !success {
		return nil, fmt.Errorf("%s: power_observe refused: %#v", label, reply)
	}
	for _, raw := range na.AsSlice(reply["buildings"]) {
		row, _ := na.AsMap(raw)
		if na.AsString(row["id"]) == id {
			row["tick"] = reply["tick"]
			return row, nil
		}
	}
	return nil, fmt.Errorf("%s: power_observe lists no row for %s: %#v", label, id, reply)
}

type colonistWall struct {
	stuff string
	x, z  int
}

// readColonistWalls pages the typed building census for every built player
// Wall, keyed by id.
func readColonistWalls(ctx context.Context, h *na.Harness, identity map[string]any, label string) (map[string]colonistWall, error) {
	walls := map[string]colonistWall{}
	cursor := ""
	for page := 0; page < 32; page++ {
		request := map[string]any{"scope": map[string]any{"expectedIdentity": identity}, "defNames": []any{"Wall"}, "statuses": []any{"built"}, "playerOnly": true, "page": map[string]any{"limit": 256}}
		if cursor != "" {
			request["page"] = map[string]any{"limit": 256, "cursor": cursor}
		}
		reply, err := h.Wire(ctx, fmt.Sprintf("%s-%d", label, page), "observations_list_buildings", request)
		if err != nil {
			return nil, err
		}
		_, observed, err := na.Outcome(reply, "observed")
		if err != nil {
			return nil, fmt.Errorf("%s page %d: %w", label, page, err)
		}
		for _, raw := range na.AsSlice(observed["buildings"]) {
			row, _ := na.AsMap(raw)
			building, _ := na.AsMap(row["building"])
			position, _ := na.AsMap(building["position"])
			walls[na.AsString(building["id"])] = colonistWall{stuff: na.AsString(row["stuff"]), x: int(na.AsNumber(position["x"])), z: int(na.AsNumber(position["z"]))}
		}
		completeness, _ := na.AsMap(observed["completeness"])
		pageInfo, _ := na.AsMap(completeness["page"])
		cursor = na.AsString(pageInfo["nextCursor"])
		if complete, _ := na.AsBool(pageInfo["complete"]); complete || cursor == "" {
			break
		}
	}
	return walls, nil
}
