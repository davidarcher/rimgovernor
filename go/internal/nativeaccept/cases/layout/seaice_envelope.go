package layout

// layout/sea-ice-envelope (#2892): a three-colonist base on a very cold ice
// sheet is planned, built and lived in as one roofed envelope. The run is
// played in five-day segments, each a serve launch that is stopped and
// reattached; at every boundary (the probe cadence: a Hypothermia excursion
// that heals inside one segment is not observed) the reattached harness reads
// the colonists' health and the built roofs and doors of the recorded plan.
// The run ends once the plan's roofed set stands, or fails at the ceiling.
//
// Assertions on the plan the run ends with, against the built ground:
//  1. every non-outdoor planned room (an airlock or porch child included) lies
//     in LayoutPlan.Roofed and every cell of Roofed carries a built roof;
//  2. every airlock child opens its parent's exterior door: the vestibule lies
//     beyond that door, both doors and the two side walls stand, and at least
//     one airlock exists (the plan's tiler places one where it pays; on a very
//     cold map it pays at a room's exterior door);
//  3. the door graph (the rooms' doors and LayoutPlan.Links) joins every roofed
//     room to every other without stepping on a cell outside the roofed set;
//  4. no colonist's Hypothermia passes policy.HypothermiaSerious at any probe
//     (solo.AuditColonists, the solo cases' probe).
//
// World: a new IceSheet / VeryCold start with three colonists on a small quiet
// map. The solo cases' cached world cannot be reused: it is a one-colonist
// start on a larger map, and a different colonist count is a different start
// spec. Needs are frozen (nothing is kept): the claim is the envelope and the
// cold, not the survival gating of solo/sea-ice-*, which this case does not
// duplicate beyond assertion 4.
//
// A snapshot test cannot cover it: the roofs, doors and walls are native
// construction on a map whose ground the plan only predicts; the pure checks
// below replay on a plan in seaice_envelope_test.go.

import (
	"context"
	"errors"
	"fmt"
	"slices"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/solo"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/planstage"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

const (
	// envelopeSegment is one serve launch: five game days.
	envelopeSegment uint64 = 5 * na.TicksPerDay
	// envelopeCeiling is how long the base may take to roof: thirty game days.
	envelopeCeiling uint64 = 30 * na.TicksPerDay
	// envelopeTicksPerSecond is the Ultrafast rate the solo cases measured on
	// a debug colony; the budget derives from it.
	envelopeTicksPerSecond = 899
	// envelopeWall is the wall ceiling of one segment's watch.
	envelopeWall = 2 * time.Hour
)

// envelopeBudget is the ceiling's play time plus a launch, stop and probe per
// segment and the fixed start: about 58 minutes.
const envelopeBudget = time.Duration(envelopeCeiling/envelopeTicksPerSecond)*time.Second +
	time.Duration(envelopeCeiling/envelopeSegment)*3*time.Minute + 7*time.Minute

func init() {
	cases.Register(cases.Case{
		Name: "layout/sea-ice-envelope",
		Scope: "Issue #2892 (epic #2855): on a three-colonist IceSheet start the built base is one roofed envelope. The plan's roofed set holds every " +
			"non-outdoor room and carries a built roof on every cell, each airlock child opens its parent's exterior door with both doors and walls standing, the " +
			"door graph joins the roofed rooms without stepping outside the roofed set, and no colonist's Hypothermia passes serious at a five-day probe. Nightly only.",
		Start: cases.Fixture{Op: na.QuietWorldTool, On: cases.Scenario{Spec: na.ScenarioStart{
			Scenario: na.DebugScenario, Count: na.DebugColonists, Seed: "layout-sea-ice-envelope-2892", Difficulty: na.DebugDifficulty,
			Biome: "IceSheet", WorldTemperature: "VeryCold",
			Size: na.DebugStart{MapSize: 150, PlanetCoverage: 0.05}, SaveName: "layout-sea-ice-envelope-2892"}}},
		RequiredOps: []string{planstage.Tool},
		Serve:       &cases.ServeSpec{NativeTimeout: 30 * time.Second, StepStall: 90 * time.Second, Prefix: "layout-envelope", PlayerSpeed: "Ultrafast"},
		Budget:      envelopeBudget,
		Crew:        cases.Crew{Size: na.DebugColonists},
		Reason: "a multi-room base on a very cold map takes game days to plan, build and roof; the fixture cannot stage a finished envelope, and " +
			"the probe at each five-day boundary is the cadence of the Hypothermia assertion",
		Run: runEnvelope,
	})
}

func runEnvelope(ctx context.Context, s cases.Session) error {
	report := s.Report()
	listed, err := na.ListColonists(ctx, s.Harness(), "envelope-initial", false, false)
	if err != nil {
		return err
	}
	var initial []string
	for _, row := range listed {
		if id := na.RowID(row); id != "" {
			initial = append(initial, id)
		}
	}
	if len(initial) != na.DebugColonists {
		return fmt.Errorf("the start has %d colonists, want %d: %v", len(initial), na.DebugColonists, initial)
	}
	report["initial_colonists"] = initial

	var segments []map[string]any
	var plan policy.LayoutPlan
	var unmet []string
	for index, elapsed := 0, uint64(0); elapsed < envelopeCeiling; index++ {
		label := fmt.Sprintf("seg%02d", index)
		row := map[string]any{"label": label, "start_elapsed": elapsed}
		segments = append(segments, row)
		report["segments"] = segments
		var advanced uint64
		var err error
		plan, advanced, unmet, err = playEnvelopeSegment(ctx, s, label, row, initial)
		elapsed += advanced
		report["elapsed_ticks"] = elapsed
		if err != nil {
			return fmt.Errorf("%s (day %d of %d): %w", label, elapsed/na.TicksPerDay, envelopeCeiling/na.TicksPerDay, err)
		}
		if len(unmet) == 0 {
			break
		}
		if advanced == 0 {
			return fmt.Errorf("%s advanced no ticks", label)
		}
	}
	if len(unmet) > 0 {
		return fmt.Errorf("the envelope did not stand within %d game days: %s", envelopeCeiling/na.TicksPerDay, strings.Join(unmet, "; "))
	}
	report["plan"] = plan.Summary()
	return errors.Join(unroofedRooms(plan), airlockCount(plan), disconnectedRooms(plan))
}

// playEnvelopeSegment serves one window, then probes the reattached game. It
// returns the recorded plan, the ticks advanced and what of the built envelope
// is still missing (empty once it stands).
func playEnvelopeSegment(ctx context.Context, s cases.Session, label string, row map[string]any, initial []string) (policy.LayoutPlan, uint64, []string, error) {
	spec := s.Spec()
	spec.Prefix = "layout-envelope-" + label
	service, err := s.Serve(ctx, spec)
	if err != nil {
		return policy.LayoutPlan{}, 0, nil, fmt.Errorf("serve: %w", err)
	}
	defer service.Stop()
	watch := na.Report{}
	_, watchErr := sustainedfood.Watch(ctx, s.Config(), service, sustainedfood.WatchConfig{
		Watch: envelopeWall, Window: envelopeSegment, Poll: 10 * time.Second,
		// A refusal is part of what the run records, never a reason to cut it.
		FailFast: sustainedfood.FailFast{Disabled: true},
	}, watch)
	var plan policy.LayoutPlan
	var readErr error
	if journal, err := service.Store(ctx); err != nil {
		readErr = err
	} else if review, err := journal.LoadRounds(ctx); err != nil {
		readErr = err
	} else if record, ok, err := journal.LayoutPlan(ctx, review.Snapshot, review.Tick); err != nil {
		readErr = err
	} else if ok {
		plan = record.Plan
	}
	row["keepalive"] = service.Stop()
	var advanced uint64
	if tw, _ := watch["window"].(*sustainedfood.TickWindow); tw != nil {
		advanced = tw.Elapsed
	}
	row["advanced"] = advanced
	h, err := s.Reattach(ctx)
	if err != nil {
		return plan, advanced, nil, errors.Join(watchErr, readErr, fmt.Errorf("reattach: %w", err))
	}
	failures := []error{watchErr, readErr}
	pawns, err := na.ListColonists(ctx, h, label+"-health", true, true)
	if err != nil {
		return plan, advanced, nil, errors.Join(append(failures, err)...)
	}
	failures = append(failures, solo.AuditColonists(pawns, initial, row))
	var unmet []string
	if plan.Roofed.Empty() {
		unmet = append(unmet, "the recorded plan holds no roofed set")
	} else {
		read, err := planstage.Read(ctx, h, label+"-envelope", nil, envelopeCells(plan))
		if err != nil {
			return plan, advanced, nil, errors.Join(append(failures, err)...)
		}
		unbuilt := unbuiltRoofs(plan, read)
		faults := airlockFaults(plan, read)
		row["roofed_cells"], row["unroofed_cells"], row["airlock_faults"] = plan.Roofed.Area(), len(unbuilt), faults
		if len(unbuilt) > 0 {
			unmet = append(unmet, fmt.Sprintf("%d of %d roofed cells have no built roof (first %v)", len(unbuilt), plan.Roofed.Area(), unbuilt[0]))
		}
		unmet = append(unmet, faults...)
	}
	return plan, advanced, unmet, errors.Join(failures...)
}

// envelopeCells are the cells the native audit reads: the roofed set, and every
// airlock's door cells and side walls.
func envelopeCells(plan policy.LayoutPlan) []planstage.Cell {
	seen := map[domain.Cell]bool{}
	var out []planstage.Cell
	add := func(c domain.Cell) {
		if !seen[c] {
			seen[c] = true
			out = append(out, planstage.Cell{X: c.X, Z: c.Z})
		}
	}
	for _, c := range plan.Roofed.Cells() {
		add(c)
	}
	for _, r := range plan.AllRooms() {
		if r.Role != policy.PlannedAirlock {
			continue
		}
		for _, c := range append(r.AirlockWalls(), r.Door, r.Vestibule()) {
			add(c)
		}
		if parent, ok := parentOf(plan, r); ok {
			add(parent.Door)
		}
	}
	return out
}

// parentOf is the room child is attached to.
func parentOf(plan policy.LayoutPlan, child policy.PlannedRoom) (policy.PlannedRoom, bool) {
	for _, r := range plan.AllRooms() {
		if child.Parent != 0 && r.ID == child.Parent {
			return r, true
		}
	}
	return policy.PlannedRoom{}, false
}

// unbuiltRoofs are the cells of the plan's roofed set the audit reads without a
// roof.
func unbuiltRoofs(plan policy.LayoutPlan, a planstage.Audit) []domain.Cell {
	var out []domain.Cell
	for _, c := range plan.Roofed.Cells() {
		if !a.Cells[planstage.Cell{X: c.X, Z: c.Z}].Roofed {
			out = append(out, c)
		}
	}
	return out
}

// airlockFaults are the ways a planned airlock fails to stand as its parent's
// exterior door opens it: no parent, a vestibule not beyond the parent's door,
// or a door, side wall or roof missing on the ground.
func airlockFaults(plan policy.LayoutPlan, a planstage.Audit) []string {
	var out []string
	at := func(c domain.Cell) planstage.CellRead { return a.Cells[planstage.Cell{X: c.X, Z: c.Z}] }
	for _, r := range plan.AllRooms() {
		if r.Role != policy.PlannedAirlock {
			continue
		}
		parent, ok := parentOf(plan, r)
		switch {
		case !ok || parent.Role.IsOutdoor():
			out = append(out, fmt.Sprintf("airlock %d has no roofed parent room", r.ID))
			continue
		case !adjacent(parent.Door, r.Vestibule()):
			out = append(out, fmt.Sprintf("airlock %d vestibule %v is not beside its parent %d's door %v", r.ID, r.Vestibule(), parent.ID, parent.Door))
			continue
		}
		for name, c := range map[string]domain.Cell{"parent door": parent.Door, "outer door": r.Door} {
			if !strings.Contains(strings.ToLower(at(c).Edifice), "door") {
				out = append(out, fmt.Sprintf("airlock %d %s %v holds %q, not a door", r.ID, name, c, at(c).Edifice))
			}
		}
		for _, c := range r.AirlockWalls() {
			if edifice := at(c).Edifice; edifice != "Wall" {
				out = append(out, fmt.Sprintf("airlock %d side wall %v holds %q, not a wall", r.ID, c, edifice))
			}
		}
	}
	slices.Sort(out)
	return out
}

func adjacent(a, b domain.Cell) bool {
	dx, dz := a.X-b.X, a.Z-b.Z
	return dx*dx+dz*dz == 1
}

// airlockCount fails a plan with no airlock: the case asserts the airlocks the
// tiler chose, and on a very cold map it chooses at least one.
func airlockCount(plan policy.LayoutPlan) error {
	for _, r := range plan.AllRooms() {
		if r.Role == policy.PlannedAirlock {
			return nil
		}
	}
	return fmt.Errorf("the plan holds no airlock on a very cold map")
}

// unroofedRooms fails a non-outdoor planned room with a floor cell outside the
// roofed set.
func unroofedRooms(plan policy.LayoutPlan) error {
	var failures []error
	for _, r := range plan.AllRooms() {
		if r.Role.IsOutdoor() {
			continue
		}
		for _, c := range r.Region.Cells() {
			if !plan.Roofed.Contains(c) {
				failures = append(failures, fmt.Errorf("%s room %d floor cell %v is outside the roofed set", r.Role, r.ID, c))
				break
			}
		}
	}
	return errors.Join(failures...)
}

// disconnectedRooms fails a roofed room the door graph does not join to the
// first one: the walk crosses only roofed cells and door cells (a room's doors
// and the plan's link doors), never an open cell outside the roofed set.
func disconnectedRooms(plan policy.LayoutPlan) error {
	walk := map[domain.Cell]bool{}
	for _, c := range plan.Roofed.Cells() {
		walk[c] = true
	}
	for _, r := range plan.AllRooms() {
		for _, c := range plan.DoorsOf(r) {
			walk[c] = true
		}
	}
	var rooms []policy.PlannedRoom
	for _, r := range plan.AllRooms() {
		if !r.Role.IsOutdoor() && !r.Region.Empty() && plan.Roofed.Contains(r.Region.Cells()[0]) {
			rooms = append(rooms, r)
		}
	}
	if len(rooms) == 0 {
		return fmt.Errorf("the plan holds no roofed room")
	}
	reached := map[domain.Cell]bool{}
	start := rooms[0].Region.Cells()[0]
	reached[start] = true
	for queue := []domain.Cell{start}; len(queue) > 0; queue = queue[1:] {
		c := queue[0]
		for _, n := range []domain.Cell{{X: c.X + 1, Z: c.Z}, {X: c.X - 1, Z: c.Z}, {X: c.X, Z: c.Z + 1}, {X: c.X, Z: c.Z - 1}} {
			if walk[n] && !reached[n] {
				reached[n] = true
				queue = append(queue, n)
			}
		}
	}
	var failures []error
	for _, r := range rooms {
		if !reached[r.Region.Cells()[0]] {
			failures = append(failures, fmt.Errorf("%s room %d is not joined to %s room %d over roofed cells and doors", r.Role, r.ID, rooms[0].Role, rooms[0].ID))
		}
	}
	return errors.Join(failures...)
}
