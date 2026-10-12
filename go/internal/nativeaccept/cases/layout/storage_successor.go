package layout

// layout/storage-successor (#2893): a standing storage room holding stock is
// outgrown, the tiled replan sizes a larger successor beside it
// (EmbeddedBlock.Replaces, #3020; the old OutgrownSuccession is gone), and the
// stockpile zone follows the room (#2866) without ever leaving the stock
// unzoned.
//
// Staging (planstage, as clearance/pack-reinstall): an inert controller derives
// the plan on the lab map; the plan's first storage room is put on the ground
// finished (walls, door and roof) with Steel lying inside it; the real
// controller then runs in probe-sized segments. Once a stockpile zone covers
// the stock, the lever is applied: the harness-only stock fault
// (buildingruntime.Faults.StockDemand, FaultsEnv "stock=Steel:n") raises the
// stock target the station chain sizes the storage group for, so GroupOutgrown
// holds for the standing room. Unlike the issue text, which named the old
// OutgrownSuccession path and "the demand input the storage case uses" (the
// storage cases hold no such input), the lever is this fault: the chain's
// stock needs are derived from the review's resource demand, which no fixture
// can raise without also sending the colony to acquire it.
//
// Probes: every segment is storageProbeTicks of play (two game hours); the
// controller is stopped, the harness reattached and one serialized read of the
// zones and the map cells taken. A gap shorter than a segment is not
// observable, so "never absent" means at every probe.
//
// Asserted: (1) the plan holds a storage room whose Replaces is the old
// room's id; (2) at every probe after the lever, while stock lies in the old
// or the successor room, some stockpile zone holds it; (3) the successor's zone
// carries the old zone's filter and priority; (4) at the end the old room has
// left the plan, the old zone is gone and all stock in the two rooms sits in
// the new zone.
//
// A snapshot test cannot cover it: the stock's whereabouts and the zone's
// native filter and priority are read from the game; the zone hand-off and the
// outgrown decision replay in policy (TestStoreReplacedRoomRepointsTheZone,
// TestDiffReplanTile...).

import (
	"context"
	"errors"
	"fmt"
	"slices"
	"sort"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/planstage"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/routinefamily"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const (
	// storageProbeTicks is the probe cadence: two game hours a segment.
	storageProbeTicks = 5000
	// storageSuccessorSegments bounds the lever to the old room leaving the
	// plan: about five game days.
	storageSuccessorSegments = 60
	// storageStockDef is the staged stock and the faulted resource; a Steel stack
	// is 75, so the lever's 30000 is 400 stacks of shelving.
	storageStockDef   = "Steel"
	storageStockCount = 150
	storageLever      = 30000
	// storageSeed is the stock target phase one plans against: enough for the
	// plan to hold a storage room, far below the lever.
	storageSeed = 1500
)

func init() {
	cases.Register(cases.Case{
		Name: "layout/storage-successor",
		Scope: "Issue #2893 (epic #2855): a staged storage room holding Steel is outgrown (the stock fault raises the station chain's stock target) and the tiled replan " +
			"plans a successor (Replaces = the old id); at every two-game-hour probe a stockpile zone holds the stock, the successor's zone carries the old " +
			"filter and priority, and at the end the stock sits in the new zone, the old zone is gone and the old room has left the plan. Nightly only.",
		Start:       cases.LabStart(),
		RequiredOps: []string{planstage.Tool},
		Serve: &cases.ServeSpec{NativeTimeout: 60 * time.Second, Prefix: "storage-successor",
			Families: []routinefamily.Family{routinefamily.Shelter, routinefamily.Expansion, routinefamily.Supply, routinefamily.Work},
			Env:      []string{stockFault(storageSeed)}},
		Budget:       45 * time.Minute,
		Crew:         cases.Crew{Size: 3},
		NoCheckpoint: true,
		Reason: "one serve launch per probe: the zone and the stock are native reads the controller's slot excludes, and the successor is built and the old room " +
			"cleared over game days; the staged room cannot be outgrown without that play",
		Run: runStorageSuccessor,
	})
}

// zoneRead is a stockpile zone as the game reports it.
type zoneRead struct {
	ID       string
	Priority string
	Allowed  []string
	Cells    []domain.Cell
}

func (z zoneRead) covers(r policy.Region) bool {
	return slices.ContainsFunc(z.Cells, r.Contains)
}

func sameSettings(a, b zoneRead) bool {
	return a.Priority == b.Priority && slices.Equal(a.Allowed, b.Allowed)
}

// stockMap is the cells of the given rooms that hold stock, by the id of the
// zone covering each (empty for a loose stack).
type stockMap map[domain.Cell]string

// stockIn reads the cells of rooms holding things and the zone over each.
func stockIn(held map[domain.Cell]bool, zones []zoneRead, rooms ...policy.Region) stockMap {
	zoneOf := map[domain.Cell]string{}
	for _, z := range zones {
		for _, c := range z.Cells {
			zoneOf[c] = z.ID
		}
	}
	out := stockMap{}
	for _, room := range rooms {
		for _, c := range room.Cells() {
			if held[c] {
				out[c] = zoneOf[c]
			}
		}
	}
	return out
}

// neverAbsent is assertion 2 at one probe: stock in the rooms with no zone
// holding any of it is the zone being absent while stock is held.
func (m stockMap) neverAbsent() error {
	if len(m) == 0 {
		return nil
	}
	for _, id := range m {
		if id != "" {
			return nil
		}
	}
	return fmt.Errorf("%d cells hold stock and no stockpile zone covers any", len(m))
}

// successorOf is the storage room of the plan that replaces room id.
func successorOf(plan policy.LayoutPlan, id int32) (policy.PlannedRoom, bool) {
	for _, r := range plan.Rooms {
		if r.Role == policy.PlannedStorage && r.Replaces == id {
			return r, true
		}
	}
	return policy.PlannedRoom{}, false
}

// inPlan reports a room of the plan with the id.
func inPlan(plan policy.LayoutPlan, id int32) bool {
	return slices.ContainsFunc(plan.Rooms, func(r policy.PlannedRoom) bool { return r.ID == id })
}

// handoff is assertions 3 and 4 over a probe: the zone over the successor
// (other than the old one) carries the old settings, the old zone is gone and
// every stock cell of the two rooms is in the new zone. It returns the new
// zone and the failures; an empty failure list is a finished hand-off.
func handoff(old zoneRead, zones []zoneRead, successor policy.Region, stock stockMap) (zoneRead, []string) {
	var fresh zoneRead
	for _, z := range zones {
		if z.ID != old.ID && z.covers(successor) {
			fresh = z
			break
		}
	}
	var out []string
	for _, z := range zones {
		if z.ID == old.ID {
			out = append(out, fmt.Sprintf("old zone %s still stands", old.ID))
		}
	}
	switch {
	case fresh.ID == "":
		out = append(out, "no zone covers the successor room")
	case !sameSettings(old, fresh):
		out = append(out, fmt.Sprintf("new zone %s carries filter %d defs priority %s, the old zone's %d defs priority %s", fresh.ID, len(fresh.Allowed), fresh.Priority, len(old.Allowed), old.Priority))
	}
	if len(stock) == 0 {
		out = append(out, "no stock lies in either room")
	}
	for c, id := range stock {
		if id != fresh.ID {
			out = append(out, fmt.Sprintf("stock at %v is in zone %q, not the new zone %q", c, id, fresh.ID))
			break
		}
	}
	return fresh, out
}

func runStorageSuccessor(ctx context.Context, s cases.Session) error {
	report := s.Report()
	var h *na.Harness

	// Phase one: an inert controller, its stock fault at the seed target, derives
	// and records a layout plan that holds a storage room.
	one, err := planstage.Begin(ctx, s, nil, []routinefamily.Family{routinefamily.Work})
	if err != nil {
		return err
	}
	plan, err := one.Plan(ctx, "layout plan with a storage room", func(p policy.LayoutPlan) bool {
		_, ok := planstage.FirstRoom(p, policy.PlannedStorage)
		return ok
	})
	if err != nil {
		one.Abort()
		return err
	}
	old, _ := planstage.FirstRoom(plan, policy.PlannedStorage)
	if h, err = one.Stop(ctx, "plan_service"); err != nil {
		return err
	}

	// Stage the room finished with Steel in it and a stockpile zone over it.
	ring := planstage.RingOf(plan, old)
	in := old.Region.Bounds()
	zone := planstage.Rect{MinX: in.X, MinZ: in.Z, MaxX: in.X + in.Width - 1, MaxZ: in.Z + in.Height - 1}
	staged := ring.Spec("WoodLog", "")
	staged.Drops = []planstage.Drop{{Def: storageStockDef, Count: storageStockCount, X: zone.MinX, Z: zone.MinZ}}
	staged.Stockpile = &zone
	if _, err := planstage.Stage(ctx, h, "stage-room", staged); err != nil {
		return err
	}
	audit, err := planstage.Read(ctx, h, "audit-room", nil, ring.Cells())
	if err != nil {
		return err
	}
	if err := ring.Sealed(audit); err != nil {
		return fmt.Errorf("the staged storage room is not a standing room: %w", err)
	}
	zones, held, err := probeZones(ctx, h, s, "baseline")
	if err != nil {
		return err
	}
	var before zoneRead
	for _, z := range zones {
		if z.covers(old.Region) {
			before = z
		}
	}
	if before.ID == "" {
		return fmt.Errorf("the staged room has no stockpile zone: %d zones", len(zones))
	}
	if len(stockIn(held, zones, old.Region)) == 0 {
		return errors.New("the staged room holds no stock")
	}
	report["staged"] = map[string]any{"room": old.ID, "bounds": in, "zone": before}

	// Phase two: the real families with the lever applied, in probe-sized
	// segments, until the old room has left the plan and the hand-off finished.
	one.Svc.Spec.Env = []string{stockFault(storageLever)}
	one.Svc.Spec.Families = []routinefamily.Family{routinefamily.Shelter, routinefamily.Expansion, routinefamily.Supply, routinefamily.Work}
	prev := one.Svc
	var failures []string
	for segment := 0; segment < storageSuccessorSegments; segment++ {
		two, err := planstage.Begin(ctx, s, prev, nil)
		if err != nil {
			return err
		}
		var tick domain.Tick
		var now policy.LayoutPlan
		err = two.Until(ctx, "a probe of play", func(review store.Rounds) (string, bool, error) {
			if tick == 0 {
				tick = review.Tick
			}
			record, ok, err := two.St.LayoutPlan(ctx, review.Snapshot, review.Tick)
			if err == nil && ok {
				now = record.Plan
			}
			return "plan", int64(review.Tick-tick) >= storageProbeTicks, err
		})
		if err != nil {
			two.Abort()
			return err
		}
		if h, err = two.Stop(ctx, fmt.Sprintf("segment_%d", segment)); err != nil {
			return err
		}
		prev = two.Svc
		zones, held, err := probeZones(ctx, h, s, fmt.Sprintf("probe-%d", segment))
		if err != nil {
			return err
		}
		succ, found := successorOf(now, old.ID)
		rooms := []policy.Region{old.Region}
		if found {
			rooms = append(rooms, succ.Region)
		}
		stock := stockIn(held, zones, rooms...)
		if err := stock.neverAbsent(); err != nil {
			return fmt.Errorf("probe %d: %w", segment, err)
		}
		if !found || inPlan(now, old.ID) {
			continue
		}
		fresh, failed := handoff(before, zones, succ.Region, stock)
		report["handoff"] = map[string]any{"segment": segment, "successor": succ.ID, "zone": fresh, "failures": failed}
		failures = failed
		if len(failed) == 0 {
			return nil
		}
	}
	return fmt.Errorf("no finished hand-off in %d probes: %s", storageSuccessorSegments, strings.Join(failures, "; "))
}

// stockFault is the FaultsEnv entry raising the Steel stock target to n.
func stockFault(n int) string {
	return fmt.Sprintf("%s=stock=%s:%d", buildingruntime.FaultsEnv, storageStockDef, n)
}

// probeZones reads the stockpile zones with their filters and cells, and the
// cells holding things, in two serialized calls plus the cell grid.
func probeZones(ctx context.Context, h *na.Harness, s cases.Session, label string) ([]zoneRead, map[domain.Cell]bool, error) {
	reply, err := h.Wire(ctx, label+"-zones", "observations_list_zones", map[string]any{"scope": map[string]any{"expectedIdentity": s.Identity()}, "includeFilter": true})
	if err != nil {
		return nil, nil, err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return nil, nil, err
	}
	grid, err := h.MapCells(ctx, label+"-cells", s.Identity())
	if err != nil {
		return nil, nil, err
	}
	cells := na.ZoneCells(grid)
	var zones []zoneRead
	for _, raw := range na.AsSlice(observed["zones"]) {
		row, _ := na.AsMap(raw)
		if na.AsString(row["type"]) != "stockpile" {
			continue
		}
		filter, _ := na.AsMap(row["filter"])
		var allowed []string
		for _, d := range na.AsSlice(filter["allowedDefNames"]) {
			allowed = append(allowed, na.AsString(d))
		}
		sort.Strings(allowed)
		id := na.AsString(row["id"])
		zones = append(zones, zoneRead{ID: id, Priority: fmt.Sprint(row["priority"]), Allowed: allowed, Cells: cells[id]})
	}
	held := map[domain.Cell]bool{}
	for _, c := range grid.Cells() {
		if empty, known := c.StorageEmpty.Value(); known && !empty {
			held[c.Cell] = true
		}
	}
	return zones, held, nil
}
