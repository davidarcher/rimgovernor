// The cells/planning-view-refresh case proves the planning-window view's
// dirty-chunk refresh (#652) against the live game: a bundle view of a
// three-band region around the clear 8x8 fixture site, read twice at one
// paused tick, reads no cell the second time and reuses every band; the
// fixture's two mutation rounds (a wall, roof, growing zone, stack and
// floor, then a second round a tick later) each come back from the next
// bundle's view equal, row for row, to an authoritative compact get_cells
// read of the same region, room ids and indoors included, with the
// refresh's work and any resync reason reported from the hop's timing
// block. Region changes, reload, rewind, overflow and the scheduled scan
// are proven offline by the native-planning-window-view probe.
package cells

import (
	"context"
	"encoding/json"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
)

func init() {
	cases.Register(cases.Case{
		Name: "cells/planning-view-refresh",
		Scope: "Planning-window view dirty-chunk refresh: a stable paused view reuses every band and reads no cell, and after " +
			"each of the fixture's mutation rounds the next bundle's view equals an authoritative compact get_cells read of the " +
			"region row for row, with the refresh's work counts in the hop's timing block.",
		Start:  cases.Fixture{Op: "test/cells_prepare"},
		Budget: 3 * time.Minute,
		Run:    runPlanningViewRefresh,
	})
}

func runPlanningViewRefresh(ctx context.Context, s cases.Session) error {
	report := s.Report()
	h := s.Harness()
	identity := s.Identity()
	prepared := s.Prepared()
	for _, required := range []string{"rimgovernor/observations_read_bundle", "rimgovernor/observations_get_cells", "test/cells_mutate"} {
		if !na.Contains(s.Names(), required) {
			return fmt.Errorf("missing %s in discovery (fixture build required)", required)
		}
	}
	origin, _ := na.AsMap(prepared["origin"])
	ox, oz, size := int32(na.AsNumber(origin["x"])), int32(na.AsNumber(origin["z"])), int32(na.AsNumber(prepared["size"]))
	if size <= 0 {
		return fmt.Errorf("prepare: missing fixture site: %#v", prepared)
	}
	// Three bands of eight rows: the site's and one on each side.
	region := policy.Rectangle{X: ox - 4, Z: oz - 8, Width: size + 8, Height: 24}
	if region.X < 0 || region.Z < 0 {
		return fmt.Errorf("fixture site %d,%d too close to the map edge for the view region", ox, oz)
	}
	viewRequest := bridge.BundlePlanningWindowViewRequest(region)
	rect := map[string]any{"minimum": map[string]any{"x": region.X, "z": region.Z}, "maximum": map[string]any{"x": region.X + region.Width - 1, "z": region.Z + region.Height - 1}}

	// view reads a bundle carrying only the view and returns it decoded
	// with the hop's planningView work account.
	view := func(label string) (bridge.PlanningWindowView, map[string]any, error) {
		request := map[string]any{"scope": map[string]any{"expectedIdentity": identity}, "planningWindowView": map[string]any{"region": rect}}
		encoded, err := json.Marshal(request)
		if err != nil {
			return bridge.PlanningWindowView{}, nil, err
		}
		envelope, err := h.Call(ctx, label, "rimgovernor/observations_read_bundle", map[string]any{"request": string(encoded)})
		if err != nil {
			return bridge.PlanningWindowView{}, nil, err
		}
		payload, _ := envelope["payload"].(string)
		reply := &o.BundleReply{}
		if err := protojson.Unmarshal([]byte(payload), reply); err != nil {
			return bridge.PlanningWindowView{}, nil, fmt.Errorf("%s: bundle reply: %w", label, err)
		}
		if reply.GetObserved().GetPlanningWindowView() == nil {
			return bridge.PlanningWindowView{}, nil, fmt.Errorf("%s: bundle carried no view: %s", label, payload)
		}
		decoded, err := bridge.DecodePlanningWindowView(reply.GetObserved().GetPlanningWindowView(), viewRequest)
		if err != nil {
			return bridge.PlanningWindowView{}, nil, fmt.Errorf("%s: view refused: %w", label, err)
		}
		timing, _ := na.AsMap(envelope["timing"])
		observation, _ := na.AsMap(timing["observation"])
		work, _ := na.AsMap(observation["planningView"])
		if work == nil {
			return bridge.PlanningWindowView{}, nil, fmt.Errorf("%s: the hop's timing block carries no planningView account", label)
		}
		return decoded, work, nil
	}
	// authority is a compact planning get_cells read of the region now.
	authority := func(label string) (map[domain.Cell]policy.SiteCell, error) {
		reply, err := h.Wire(ctx, label, "observations_get_cells", map[string]any{
			"scope": map[string]any{"expectedIdentity": identity}, "rectangle": rect, "compact": true, "page": map[string]any{"limit": 4096},
			"fields": map[string]any{"terrain": false, "roof": true, "visibility": true, "traversal": true, "zone": true, "room": true, "growth": true},
		})
		if err != nil {
			return nil, err
		}
		_, observed, err := na.Outcome(reply, "observed")
		if err != nil {
			return nil, err
		}
		data, err := json.Marshal(observed)
		if err != nil {
			return nil, err
		}
		snapshot := &o.CellsSnapshot{}
		if err := protojson.Unmarshal(data, snapshot); err != nil {
			return nil, err
		}
		if err := bridge.ExpandCompactCells(snapshot); err != nil {
			return nil, err
		}
		cells, _ := bridge.PlanningCells(snapshot)
		rows := make(map[domain.Cell]policy.SiteCell, len(cells))
		for _, row := range cells {
			rows[row.Cell] = row
		}
		return rows, nil
	}
	// parity compares a view with an authoritative read of the same tick.
	parity := func(label string) (map[string]any, error) {
		got, work, err := view(label)
		if err != nil {
			return nil, err
		}
		want, err := authority(label + "-authority")
		if err != nil {
			return nil, err
		}
		// A band carried over from an earlier tick is as of its validation:
		// daylight may have moved its glow since (the scheduled scan's
		// field), and only that is excused.
		aged := func(z int32) bool {
			for _, chunk := range got.Chunks {
				if z >= chunk.MinZ && z <= chunk.MaxZ {
					return chunk.Validated < got.PublishedTick
				}
			}
			return false
		}
		drift, excused := len(want)-len(got.Cells), 0
		for _, row := range got.Cells {
			have, ok := want[row.Cell]
			if ok && have != row && aged(row.Cell.Z) {
				have.Glow, row.Glow = domain.Fact[float64]{}, domain.Fact[float64]{}
				if have == row {
					excused++
					continue
				}
			}
			if !ok || have != row {
				drift++
			}
		}
		work["drift"], work["glowAged"], work["validated"], work["published"] = drift, excused, got.Validated(), got.PublishedTick
		if drift != 0 {
			return work, fmt.Errorf("%s: the view differs from a full read on %d cells (work %v)", label, drift, work)
		}
		return work, nil
	}
	mutate := func(label, phase string) error {
		result, err := h.Call(ctx, label, "test/cells_mutate", map[string]any{"originX": ox, "originZ": oz, "phase": phase})
		if err != nil {
			return err
		}
		if success, _ := na.AsBool(result["success"]); !success {
			return fmt.Errorf("%s: cells_mutate refused: %#v", label, result)
		}
		return nil
	}
	defer func() {
		if err := mutate("cleanup", "cleanup"); err != nil {
			report["cleanup_error"] = err.Error()
		}
	}()

	boot, err := parity("view-before")
	report["before"] = boot
	if err != nil {
		return err
	}
	stable, err := parity("view-stable")
	report["stable"] = stable
	if err != nil {
		return err
	}
	chunks := na.AsNumber(stable["chunks"])
	if chunks != 3 || na.AsNumber(stable["rebuilt"]) != 0 || na.AsNumber(stable["cellsRead"]) != 0 || na.AsNumber(stable["reused"]) != chunks || stable["resync"] != nil {
		return fmt.Errorf("a stable paused view did work: %v", stable)
	}
	for _, phase := range []string{"first", "second"} {
		if err := mutate("mutate-"+phase, phase); err != nil {
			return err
		}
		work, err := parity("view-after-" + phase)
		report[phase] = work
		if err != nil {
			return err
		}
		if na.AsNumber(work["rebuilt"]) < 1 || na.AsNumber(work["cellsRead"]) > float64(region.Width*region.Height) {
			return fmt.Errorf("after the %s round the view rebuilt nothing or read past the region: %v", phase, work)
		}
	}
	return nil
}
