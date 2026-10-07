package layout

// layout/rich-soil (#1291, epic #1279): a fresh colony on the baseline
// world, whose map holds rich soil beside the centre, derives its own
// layout plan. The audit reads the plan from the journal, the map survey
// and the growing zones natively: no planned room or hallway on rich
// soil, each fertile patch one field zone wholly inside or outside the
// traced ring (crossed only where the ring runs along the edge margin
// line), and the crop zones inside a patch one block with no gap. A
// colonist is then spawned and the plan must grow (a layout_plan replanned row
// for the new count) within one in-game hour.

import (
	"context"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/routinefamily"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/startersite"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// replanWithin is the hourly layout trigger's bound (#1290).
const replanWithin = 2500

func init() {
	cases.Register(cases.Case{
		Name: "layout/rich-soil",
		Scope: "Issue #1291: on the tribal " + sustained.BaselineSave + " world (rich soil near the centre) a fresh colony's own layout plan " +
			"puts no room or hallway on rich soil, keeps each fertile patch one field zone wholly inside or outside the traced ring " +
			"(split only where the ring runs along the edge margin line), zones adjacent crop blocks with no gap, and grows the plan " +
			"within 2500 ticks of a colonist joining. A snapshot test cannot cover it end to end: the crop zones are native, and the " +
			"replan is the live hourly trigger (the plan assertions alone replay offline in TestRichSoilBaselinePlan).",
		Start:       cases.Save{Name: sustained.BaselineSave},
		RequiredOps: []string{na.LabSpawnTool},
		Keep:        []string{string(na.NeedFood)},
		Serve:       &cases.ServeSpec{Families: []routinefamily.Family{routinefamily.Shelter, routinefamily.Expansion, routinefamily.Field}, NativeTimeout: 30 * time.Second, Prefix: "layout-rich-soil"},
		Budget:      8 * time.Minute,
		Reason:      "two short serves: a plan and its first field review, then one in-game hour after a colonist joins",
		Run:         richSoil,
	})
}

func richSoil(ctx context.Context, s cases.Session) error {
	report := s.Report()
	plan, err := servePlanned(ctx, s, report)
	if err != nil {
		return err
	}
	h, err := s.Reattach(ctx)
	if err != nil {
		return err
	}
	if _, err := h.Call(ctx, "pause", "rimgovernor/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	facts, survey, err := startersite.Survey(ctx, h)
	if err != nil {
		return err
	}
	crops, err := cropZones(ctx, s, h)
	if err != nil {
		return err
	}
	audit := auditSoil(plan.Plan, survey, crops)
	report["soil"] = audit
	report["rich_overlap"] = audit.richOverlap()
	if err := audit.err(); err != nil {
		return err
	}
	if audit.RichCells == 0 || audit.Patches == 0 {
		return fmt.Errorf("the world has no rich soil or fertile patch to audit: %+v", audit)
	}
	if audit.CropZones == 0 {
		return fmt.Errorf("no crop zone was laid by tick %d", facts.Identity.Tick)
	}

	pawns, _ := facts.Facts.Colonists.Value()
	at, ok := plan.Plan.Anchor(policy.PlannedBedroom, nil)
	if !ok {
		return fmt.Errorf("the plan holds no bedroom to spawn the colonist in")
	}
	spawned, err := h.Call(ctx, "spawn-colonist", na.LabSpawnTool, map[string]any{"def": "Colonist", "x": at.X, "z": at.Z})
	if err != nil {
		return err
	}
	spawnTick := int64(facts.Identity.Tick)
	report["spawned"] = map[string]any{"reply": spawned, "tick": spawnTick, "colonists_before": pawns}

	service, err := s.Serve(ctx, s.Spec())
	if err != nil {
		return err
	}
	defer service.Stop()
	if _, err := service.Acquire(); err != nil {
		return err
	}
	service.KeepAuthority(ctx)
	var replan *na.FlightRow
	_, err = service.WaitReview(ctx, na.Wait{Ceiling: 4 * time.Minute}, func(r store.Rounds) bool {
		rows, _ := na.ReadFlight(service.FlightPath)
		for i := range rows {
			if isReplan(rows[i]) && int64(na.AsNumber(rows[i].Fields()["colonists"])) == pawns+1 {
				replan = &rows[i]
				return true
			}
		}
		return int64(r.Tick) > spawnTick+2*replanWithin
	})
	if err != nil {
		return err
	}
	if replan == nil {
		return fmt.Errorf("no layout_plan replanned row for %d colonists by tick %d (spawned at %d)", pawns+1, spawnTick+2*replanWithin, spawnTick)
	}
	report["replan"] = map[string]any{"tick": replan.Tick, "payload": replan.Payload, "after_ticks": replan.Tick - spawnTick}
	if replan.Tick-spawnTick > replanWithin {
		return fmt.Errorf("layout_plan replanned for %d colonists at tick %d, %d ticks after the colonist joined (bound %d)", pawns+1, replan.Tick, replan.Tick-spawnTick, replanWithin)
	}
	return nil
}

// servePlanned serves until the journal holds a layout plan and one
// further hour has passed, so the field planner has zoned crops, then
// stops the service.
func servePlanned(ctx context.Context, s cases.Session, report na.Report) (store.LayoutPlanRecord, error) {
	service, err := s.Serve(ctx, s.Spec())
	if err != nil {
		return store.LayoutPlanRecord{}, err
	}
	defer service.Stop()
	if _, err := service.Acquire(); err != nil {
		return store.LayoutPlanRecord{}, err
	}
	service.KeepAuthority(ctx)
	journal, err := service.Store(ctx)
	if err != nil {
		return store.LayoutPlanRecord{}, err
	}
	var plan store.LayoutPlanRecord
	_, err = service.WaitReview(ctx, na.Wait{Ceiling: 3 * time.Minute}, func(r store.Rounds) bool {
		record, ok, err := journal.LayoutPlan(ctx, r.Snapshot, r.Tick)
		if err != nil || !ok {
			return false
		}
		plan = record
		return r.Tick >= record.Tick+replanWithin
	})
	if err != nil {
		return plan, fmt.Errorf("wait for a layout plan and its first field hour: %w", err)
	}
	report["plan"] = map[string]any{"tick": plan.Tick, "summary": plan.Plan.Summary()}
	return plan, nil
}

// cropZones reads every growing zone's cells natively.
func cropZones(ctx context.Context, s cases.Session, h *na.Harness) ([][]domain.Cell, error) {
	reply, err := h.Wire(ctx, "zones", "observations_list_zones", map[string]any{
		"scope": map[string]any{"expectedIdentity": s.Identity()},
	})
	if err != nil {
		return nil, err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return nil, err
	}
	grid, err := h.MapCells(ctx, "zone-cells", s.Identity())
	if err != nil {
		return nil, err
	}
	zones := na.ZoneCells(grid)
	var out [][]domain.Cell
	for _, raw := range na.AsSlice(observed["zones"]) {
		row, _ := na.AsMap(raw)
		if !strings.EqualFold(na.AsString(row["type"]), "growing") {
			continue
		}
		out = append(out, zones[na.AsString(row["id"])])
	}
	return out, nil
}

// isReplan reports a layout replan row: the layout_plan decision with verdict
// replanned.
func isReplan(row na.FlightRow) bool {
	return row.Kind == "layout_plan" && na.AsString(row.Fields()["verdict"]) == "replanned"
}
